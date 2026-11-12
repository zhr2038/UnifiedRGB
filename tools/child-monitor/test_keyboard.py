"""Exercise switch-off restoration and fresh status on re-enable without touching hardware."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import queue
import threading
import time
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

from keyboard import BAR_COLORS, NUMBER_ROW_KEYS, run
from light_switch import normalize_ha_url


class KeyboardTests(unittest.TestCase):
    def test_home_assistant_url_matches_windows_credential_authority(self):
        self.assertEqual(normalize_ha_url("http://10.20.30.40:80/"), "http://10.20.30.40")
        self.assertEqual(normalize_ha_url("https://ha.example.invalid:8443"), "https://ha.example.invalid:8443")
        for invalid in ("http://ha.example.invalid", "https://someone@ha.example.invalid",
                        "https://ha.example.invalid/path", "https://ha.example.invalid/?wrong=true"):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                normalize_ha_url(invalid)

    def test_off_clears_during_long_poll_and_on_uses_the_current_reading(self):
        outputs = queue.Queue()
        controls = queue.Queue()
        changed = threading.Condition()
        poll_waiting = threading.Event()
        stop = threading.Event()
        reading = {"state": "moving", "generation": "fixture", "sequence": 1, "motion_level": 11,
                   "detected_at": time.time(), "audio_online": True, "video_online": True}

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                query = parse_qs(urlsplit(self.path).query)
                with changed:
                    if query.get("after") == [str(reading["sequence"])]:
                        poll_waiting.set()
                        changed.wait_for(lambda: stop.is_set() or query["after"] != [str(reading["sequence"])],
                                         timeout=2)
                    payload = json.dumps(reading).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                try:
                    self.wfile.write(payload)
                except (BrokenPipeError, ConnectionResetError):
                    pass

            def log_message(self, *args):
                pass

        class Pipe:
            def __init__(self, start, expected_keys):
                self.start = start
                self.keys = tuple(expected_keys)

            def __enter__(self):
                return self

            def __exit__(self, *args):
                pass

            def keyboard(self):
                return "test-controller"

            def overlay(self, controller, colors):
                assert controller == "test-controller"
                outputs.put((self.start, self.keys, tuple(colors)))
                return "test-transport"

        def switch_reader(set_enabled, _stop):
            controls.put(set_enabled)
            stop.wait()

        def update(state, level, sequence):
            with changed:
                reading.update(state=state, motion_level=level, sequence=sequence, detected_at=time.time())
                changed.notify_all()

        def assert_overlay(colors):
            start, keys, actual = outputs.get(timeout=3)
            self.assertEqual(start, 13)
            self.assertEqual(keys, NUMBER_ROW_KEYS)
            self.assertEqual(actual, colors)

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server, patch("keyboard.RgbPipe", Pipe):
            threading.Thread(target=server.serve_forever, daemon=True).start()
            url = f"http://127.0.0.1:{server.server_port}/status"
            worker = threading.Thread(target=run, args=(url, switch_reader, stop), daemon=True)
            worker.start()
            try:
                set_enabled = controls.get(timeout=3)
                self.assertTrue(poll_waiting.wait(3))
                with self.assertRaises(queue.Empty):
                    outputs.get(timeout=0.25)
                set_enabled(True)
                assert_overlay(BAR_COLORS[:11])
                set_enabled(False)
                assert_overlay(())
                update("crying", 11, 2)
                with self.assertRaises(queue.Empty):
                    outputs.get(timeout=0.4)
                update("moving", 4, 3)
                set_enabled(True)
                assert_overlay(BAR_COLORS[:4])
                set_enabled(False)
                assert_overlay(())
            finally:
                stop.set()
                with changed:
                    changed.notify_all()
                worker.join(timeout=4)
                server.shutdown()
            self.assertFalse(worker.is_alive())


if __name__ == "__main__":
    unittest.main()
