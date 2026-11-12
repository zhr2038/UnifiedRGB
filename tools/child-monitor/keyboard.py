"""Display Home Assistant's child-monitor state on the Azoth X key lights."""

import argparse
from contextlib import ExitStack
import json
import logging
from logging.handlers import RotatingFileHandler
import threading
import time

import pywintypes
import requests
import win32con
import win32file
import win32pipe
import win32security

LOG = logging.getLogger("child_monitor")
KEYBOARD_PREFIX = "asusr:wired:"
PIPE = r"\\.\pipe\UnifiedRgb"
TRUSTED = {"S-1-5-32-544", "S-1-5-18"}
NUMBER_ROW_START = 13
NUMBER_ROW_KEYS = ("`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "Bksp", "Ins")
BAR_COLORS = ("#00E830",) * 8 + ("#FFB000",) * 5 + ("#FF1600",) * 2


class PipeError(RuntimeError):
    pass


class RgbPipe:
    def __init__(self, start, key_names):
        self.start = start
        self.key_names = key_names

    def __enter__(self):
        win32pipe.WaitNamedPipe(PIPE, 2500)
        self.handle = win32file.CreateFile(
            PIPE, win32con.GENERIC_READ | win32con.GENERIC_WRITE, 0, None, win32con.OPEN_EXISTING, 0, None
        )
        try:
            security = win32security.GetSecurityInfo(
                self.handle, win32security.SE_KERNEL_OBJECT, win32security.OWNER_SECURITY_INFORMATION
            )
            owner = win32security.ConvertSidToStringSid(security.GetSecurityDescriptorOwner())
            if owner not in TRUSTED:
                raise PipeError("The RGB host pipe owner is not Administrators or SYSTEM")
        except BaseException:
            self.handle.Close()
            raise
        self.counter = 0
        self.buffer = bytearray()
        return self

    def __exit__(self, *args):
        self.handle.Close()

    def call(self, method, *params):
        self.counter += 1
        body = json.dumps({"jsonrpc": "2.0", "id": self.counter, "method": method, "params": params}).encode()
        win32file.WriteFile(self.handle, b"Content-Length: %d\r\n\r\n" % len(body) + body)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            _, available, _ = win32pipe.PeekNamedPipe(self.handle, 0)
            if available:
                _, data = win32file.ReadFile(self.handle, min(available, 65536))
                self.buffer.extend(data)
            while (header_end := self.buffer.find(b"\r\n\r\n")) >= 0:
                headers = dict(line.lower().split(b":", 1) for line in bytes(self.buffer[:header_end]).split(b"\r\n"))
                length = int(headers[b"content-length"])
                start = header_end + 4
                if len(self.buffer) < start + length:
                    break
                message = json.loads(self.buffer[start:start + length])
                del self.buffer[:start + length]
                if message.get("id") != self.counter:
                    continue
                if "error" in message:
                    raise PipeError(message["error"]["message"])
                return message["result"]
            time.sleep(0.02)
        raise PipeError(f"Timed out waiting for {method}")

    def keyboard(self):
        matches = [c for c in self.call("ListControllersAsync") if c["id"].startswith(KEYBOARD_PREFIX)
                   and c.get("state") == "Active" and any(
                       (zone.get("perKey") or {}).get("state") == "Ready" for zone in c["zones"])]
        if len(matches) != 1:
            raise PipeError("Expected one active verified Azoth X controller")
        return matches[0]["id"]

    def overlay(self, controller, colors):
        return self.call("SetKeyboardLightOverlayAsync", controller, self.start, colors, self.key_names)


def bar_colors(reading):
    if not (reading.get("video_online") is True and reading.get("audio_online") is True):
        return ()
    state = reading.get("state")
    if state == "crying":
        return BAR_COLORS
    if state != "moving":
        return ()
    level = reading.get("motion_level")
    if type(level) is not int or not 0 <= level <= 13:
        raise ValueError("Invalid camera motion level")
    return BAR_COLORS[:level]


class Inputs:
    def __init__(self, enabled):
        self.condition = threading.Condition()
        self.enabled = enabled
        self.epoch = 0
        self.version = 0
        self.reading = None
        self.received = 0.0

    def set_enabled(self, enabled):
        with self.condition:
            if enabled == self.enabled:
                return
            self.enabled = enabled
            self.epoch += 1
            self.version += 1
            self.reading = None
            LOG.info("Child monitor lights requested %s", "on" if enabled else "off")
            self.condition.notify_all()

    def publish(self, reading):
        with self.condition:
            current = self.reading
            if reading and current and reading["generation"] == current["generation"]:
                if reading["sequence"] < current["sequence"]:
                    return
            self.reading = reading
            self.received = time.monotonic()
            self.version += 1
            self.condition.notify_all()

    def view(self, previous_version):
        with self.condition:
            self.condition.wait_for(lambda: self.version != previous_version, timeout=0.25)
            return self.version, self.epoch, self.enabled, self.reading, self.received

    def admits(self, epoch):
        with self.condition:
            return self.enabled and self.epoch == epoch


def read_status(session, source, cursor=None):
    with session.get(source, params=cursor, timeout=(1.2, 3.6 if cursor else 1.5), allow_redirects=False) as response:
        response.raise_for_status()
        if response.status_code != 200:
            raise ValueError("Unexpected camera status response")
        reading = response.json()
    if not isinstance(reading, dict) or type(reading.get("sequence")) is not int or not isinstance(reading.get("generation"), str):
        raise ValueError("Invalid camera status")
    return reading


def poll_source(source, inputs, stop):
    with requests.Session() as session:
        session.trust_env = False
        cursor = None
        while not stop.is_set():
            try:
                reading = read_status(session, source, cursor)
                cursor = {"generation": reading["generation"], "after": reading["sequence"]}
                inputs.publish(reading)
            except (requests.RequestException, ValueError, KeyError) as exc:
                inputs.publish(None)
                cursor = None
                LOG.warning("Child monitor detector unavailable (%s)", type(exc).__name__)
                stop.wait(0.7)


def run(source, switch_reader, stop=None, on_write=None, start=NUMBER_ROW_START, key_names=NUMBER_ROW_KEYS):
    stop = stop or threading.Event()
    inputs = Inputs(enabled=False)
    threading.Thread(target=poll_source, args=(source, inputs, stop), daemon=True).start()
    threading.Thread(target=switch_reader, args=(inputs.set_enabled, stop), daemon=True).start()

    with requests.Session() as fresh, ExitStack() as stack:
        fresh.trust_env = False
        rgb = controller = None
        previous = ()
        last_write = 0.0
        version, applied_epoch = -1, 0
        fresh_needed = False

        def release(reason):
            nonlocal rgb, controller, previous
            try:
                if rgb is not None and previous:
                    route = rgb.overlay(controller, ())
                    LOG.info("Child monitor overlay cleared (%s) via %s", reason, route)
                    if on_write:
                        on_write({"state": reason, "keys": 0, "route": route})
            finally:
                stack.close()
                rgb = controller = None
                previous = ()

        try:
            while not stop.is_set():
                version, epoch, enabled, reading, received = inputs.view(version)
                try:
                    if epoch != applied_epoch:
                        applied_epoch = epoch
                        fresh_needed = enabled
                        release("switch_off" if not enabled else "switch_transition")
                    if not enabled or not inputs.admits(epoch):
                        continue
                    if fresh_needed:
                        reading = read_status(fresh, source)
                        if not inputs.admits(epoch):
                            continue
                        inputs.publish(reading)
                        received = time.monotonic()
                        fresh_needed = False
                    if reading is None:
                        release("detector_unavailable")
                        continue
                    colors = bar_colors(reading)
                    changed = colors != previous
                    if not changed and (not colors or time.monotonic() - last_write < 2):
                        continue
                    if rgb is None:
                        rgb = stack.enter_context(RgbPipe(start, key_names))
                        controller = rgb.keyboard()
                    if not inputs.admits(epoch):
                        continue
                    route = rgb.overlay(controller, colors)
                    acknowledged = time.time()
                    last_write = time.monotonic()
                    previous = colors
                    if changed:
                        published_ms = round((reading.get("server_time", reading["detected_at"])
                                              - reading["detected_at"]) * 1000)
                        host_ms = round((last_write - received) * 1000)
                        LOG.info("Keyboard %s keys=%d via %s, NAS event age=%dms, PC receipt-to-ack=%dms",
                                 reading["state"], len(colors), route, published_ms, host_ms)
                        if on_write:
                            on_write({"state": reading["state"], "keys": len(colors), "route": route,
                                      "detector_to_ack_ms": round((acknowledged - reading["detected_at"]) * 1000),
                                      "sequence": reading["sequence"]})
                except (requests.RequestException, PipeError, pywintypes.error, ValueError, KeyError) as exc:
                    LOG.warning("Keyboard bar temporarily unavailable (%s)", type(exc).__name__)
                    stack.close()
                    rgb = controller = None
                    previous = ()
                    stop.wait(0.7)
        finally:
            release("worker_stopped")



def main():
    from functools import partial
    from urllib.parse import urlsplit
    from light_switch import normalize_ha_url, watch_switch

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--status-url", required=True, help="LAN URL of the detector /status endpoint")
    parser.add_argument("--ha-url", required=True, help="Home Assistant server root used in Windows Credential Manager")
    parser.add_argument("--start-index", type=int, default=NUMBER_ROW_START)
    parser.add_argument("--key-names", nargs=len(NUMBER_ROW_KEYS), default=NUMBER_ROW_KEYS)
    args = parser.parse_args()
    try:
        ha_url = normalize_ha_url(args.ha_url)
        status = urlsplit(args.status_url)
        if (status.scheme not in ("http", "https") or not status.hostname or status.username is not None
                or status.path != "/status" or status.query or status.fragment):
            raise ValueError("use the detector's HTTP(S) /status URL")
        if args.start_index < 0:
            raise ValueError("start index must be nonnegative")
    except ValueError as exc:
        parser.error(str(exc))

    log = RotatingFileHandler("child-monitor.log", maxBytes=512000, backupCount=2, encoding="utf-8")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=[log])
    run(args.status_url, partial(watch_switch, ha_url), start=args.start_index, key_names=args.key_names)


if __name__ == "__main__":
    main()
