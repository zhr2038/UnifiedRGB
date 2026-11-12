"""Home Assistant's persisted child-monitor light switch."""

from contextlib import contextmanager
from ipaddress import IPv4Address, IPv4Network
import json
import logging
from urllib.parse import urlsplit

import pywintypes
import websocket
import win32cred


HELPER = "input_boolean.unifiedrgb_child_monitor_lights"
LOG = logging.getLogger("child_monitor")
PRIVATE_IPV4 = tuple(IPv4Network(network) for network in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))


class SwitchUnavailable(RuntimeError):
    pass


class AuthenticationRejected(SwitchUnavailable):
    pass


def normalize_ha_url(raw):
    url = urlsplit(raw)
    if (url.scheme not in ("https", "http") or not url.hostname or url.username is not None
            or url.path not in ("", "/") or url.query or url.fragment):
        raise ValueError("use the Home Assistant server root")
    host = url.hostname
    if url.scheme == "http":
        try:
            ip = IPv4Address(host)
        except ValueError:
            raise ValueError("use HTTPS or a private IPv4 address for HTTP") from None
        if not (ip.is_loopback or any(ip in network for network in PRIVATE_IPV4)):
            raise ValueError("use HTTPS or a private IPv4 address for HTTP")
    port = url.port
    authority = f"[{host}]" if ":" in host else host
    if port is not None and port != (443 if url.scheme == "https" else 80):
        authority += f":{port}"
    return f"{url.scheme}://{authority}"


@contextmanager
def keyboard_websocket(base):
    credential = win32cred.CredRead("UnifiedRgb/HomeAssistant/" + base, win32cred.CRED_TYPE_GENERIC)
    token = credential["CredentialBlob"].decode("utf-16-le").rstrip("\x00")
    del credential
    scheme = "wss" if base.startswith("https://") else "ws"
    url = f"{scheme}://{base.split('://', 1)[1]}/api/websocket"
    ws = websocket.create_connection(url, timeout=2, suppress_origin=True, redirect_limit=0,
                                     http_no_proxy=[urlsplit(base).hostname])
    try:
        if json.loads(ws.recv()).get("type") != "auth_required":
            raise SwitchUnavailable("Unexpected Home Assistant WebSocket greeting")
        ws.send(json.dumps({"type": "auth", "access_token": token}))
        del token
        if json.loads(ws.recv()).get("type") != "auth_ok":
            raise AuthenticationRejected("Home Assistant rejected the keyboard account")
        yield ws
    finally:
        ws.close()


def watch_switch(base, set_enabled, stop):
    while not stop.is_set():
        try:
            with keyboard_websocket(base) as ws:
                def command(message_id, kind, **fields):
                    ws.send(json.dumps({"id": message_id, "type": kind, **fields}))
                    while True:
                        message = json.loads(ws.recv())
                        if message.get("id") != message_id:
                            continue
                        if not message.get("success"):
                            raise SwitchUnavailable("Home Assistant did not admit the state subscription")
                        return message.get("result")

                command(1, "subscribe_events", event_type="state_changed")
                states = command(2, "get_states")
                current = next((state for state in states if state["entity_id"] == HELPER), {})
                set_enabled(current.get("state") == "on")
                ping_id = 2
                while not stop.is_set():
                    try:
                        message = json.loads(ws.recv())
                    except websocket.WebSocketTimeoutException:
                        ping_id += 1
                        ws.send(json.dumps({"id": ping_id, "type": "ping"}))
                        message = json.loads(ws.recv())
                    change = message.get("event", {}).get("data", {})
                    if change.get("entity_id") == HELPER:
                        set_enabled((change.get("new_state") or {}).get("state") == "on")
        except AuthenticationRejected:
            set_enabled(False)
            LOG.error("Home Assistant rejected the Windows Credential Manager entry")
            return
        except (SwitchUnavailable, websocket.WebSocketException, pywintypes.error, OSError, ValueError, KeyError) as exc:
            set_enabled(False)
            LOG.warning("Child monitor switch unavailable (%s)", type(exc).__name__)
            stop.wait(2)
    set_enabled(False)
