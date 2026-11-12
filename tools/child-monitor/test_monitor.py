"""Focused checks for the motion bar, cry gating and detector notifications."""

import threading
import time

import numpy as np

from monitor import HEIGHT, MOTION_THRESHOLD, WIDTH, MonitorState, MotionDetector


def test_motion_region():
    detector = MotionDetector((0.25, 0.1, 0.94, 0.90))
    frame = np.zeros((HEIGHT, WIDTH), dtype=np.uint8)
    assert detector.detect(frame.tobytes()) == 0
    frame[20:80, 5:35] = 128
    assert detector.detect(frame.tobytes()) == 0
    frame[60:110, 100:145] = 192
    assert detector.detect(frame.tobytes()) >= MOTION_THRESHOLD
    assert detector.detect(frame.tobytes()) >= MOTION_THRESHOLD
    assert detector.detect(frame.tobytes()) >= MOTION_THRESHOLD
    assert detector.detect(frame.tobytes()) >= MOTION_THRESHOLD
    assert detector.detect(frame.tobytes()) == 0
    frame += 20
    assert detector.detect(frame.tobytes()) == 0


def test_motion_grows_yellow_but_never_red():
    monitor = MonitorState(0, 0.25)
    for t in np.arange(0, 0.75, 0.125):
        monitor.sample("video", 0.025, t)
        monitor.sample("audio", 0.22, t)
    assert monitor.state == "moving" and 0 < monitor.motion_level <= 8
    for t in np.arange(0.75, 4, 0.125):
        monitor.sample("video", 0.04, t)
        monitor.sample("audio", 0.22, t)
    assert monitor.state == "moving" and monitor.motion_level == 13
    for t in np.arange(4, 8, 0.125):
        monitor.sample("video", 0.0, t)
        monitor.sample("audio", 0, t)
    assert monitor.state == "moving" and 0 < monitor.motion_level <= 8
    for t in np.arange(8, 12, 0.125):
        monitor.sample("video", 0.0, t)
        monitor.sample("audio", 0, t)
    assert monitor.state == "idle" and monitor.motion_level == 0


def test_cry_stops_before_remaining_motion():
    monitor = MonitorState(0, 0.25)
    for t in np.arange(0, 1, 0.125):
        monitor.sample("video", 0.0, t)
        monitor.sample("audio", 0.45, t)
    assert monitor.state == "crying" and monitor.motion_level == 0
    for t in np.arange(1, 4, 0.125):
        monitor.sample("video", 0.06, t)
        monitor.sample("audio", 0.1, t)
    assert monitor.state == "moving" and monitor.motion_level > 8
    monitor.reset_stream(4)
    assert monitor.state == "offline" and monitor.motion_level == 0


def test_waiter_wakes_on_motion_change():
    start = time.monotonic()
    monitor = MonitorState(start, 0.25)
    monitor.sample("video", 0.0, start)
    monitor.sample("audio", 0.0, start)
    current = monitor.status()
    results = []
    waiter = threading.Thread(target=lambda: results.append(monitor.status(current["generation"], current["sequence"])))
    waiter.start()
    time.sleep(0.03)
    monitor.sample("video", 0.1, time.monotonic())
    waiter.join(0.75)
    assert not waiter.is_alive() and 0 < results[0]["motion_level"] <= 8


def test_one_high_confidence_baby_cry_opens_red():
    monitor = MonitorState(0, 0.22)
    monitor.sample("video", 0, 0)
    monitor.sample("audio", 0.18, 0)
    monitor.sample("audio", 0.54, 0.24, baby_score=0.54, rms=0.008)
    assert monitor.state == "crying"
    monitor.sample("audio", 0.0, 2.6)
    assert monitor.state == "crying"
    monitor.sample("audio", 0.0, 2.8)
    assert monitor.state == "idle"
    summaries = monitor.audio_diagnostics()["windows"]
    assert sum(row["confirmed"] for row in summaries) == 1
    assert max(row["peak_baby_score"] for row in summaries) == 0.54


if __name__ == "__main__":
    for test in (test_motion_region, test_motion_grows_yellow_but_never_red,
                 test_cry_stops_before_remaining_motion, test_waiter_wakes_on_motion_change,
                 test_one_high_confidence_baby_cry_opens_red):
        test()
        print(test.__name__, "passed")
