"""Analyze the monitor's live audio and video locally. Publish only state."""

import argparse
from collections import deque
import csv
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import subprocess
import threading
import time
import uuid
from urllib.parse import parse_qs, urlsplit

import numpy as np
from tflite_runtime.interpreter import Interpreter


WIDTH, HEIGHT = 320, 180
AUDIO_RATE, AUDIO_WINDOW, AUDIO_STEP = 16000, 15600, 3840
FRESH_SECONDS = 5
CRY_RELEASE_SECONDS = 2.5
FAST_BABY_CRY_SCORE = 0.52
MIN_AUDIO_RMS = 0.001
MOTION_THRESHOLD = 0.012
STRONG_MOTION_THRESHOLD = 0.035
MOTION_KEYS = 13
VIDEO_FPS = 12


class MonitorState:
    def __init__(self, now, cry_threshold):
        self.generation = uuid.uuid4().hex
        self.state = "offline"
        self.sequence = 0
        self.changed = now
        self.detected_at = time.time()
        self.motion_energy = 0.0
        self.motion_level = 0
        self.last_cry = float("-inf")
        self.video_at = self.audio_at = float("-inf")
        self.motion_samples = deque(maxlen=3)
        self.cry_votes = deque(maxlen=3)
        self.audio_summaries = deque(maxlen=60)
        self.condition = threading.Condition()
        self.cry_threshold = cry_threshold

    def transition(self, state, level, now):
        if state != self.state or (state != "crying" and level != self.motion_level):
            self.state = state
            self.sequence += 1
            self.changed = now
            self.detected_at = time.time()
            self.condition.notify_all()
        self.motion_level = level

    def sample(self, source, observation, now, baby_score=0.0, rms=0.0):
        with self.condition:
            if source == "video":
                elapsed = min(0.25, now - self.video_at)
                self.video_at = now
                self.motion_samples.append(observation)
                if observation >= STRONG_MOTION_THRESHOLD or sum(value >= MOTION_THRESHOLD for value in self.motion_samples) >= 2:
                    strength = min(1.0, (max(self.motion_samples) - MOTION_THRESHOLD) / 0.09)
                    initial = 1.0 + 5.0 * strength
                    self.motion_energy = min(MOTION_KEYS, max(self.motion_energy, initial) + elapsed * (3.2 + 4.0 * strength))
                else:
                    self.motion_energy = max(0.0, self.motion_energy - elapsed * 2.0)
            else:
                self.audio_at = now
                positive = observation >= self.cry_threshold
                self.cry_votes.append(positive)
                confirmed = baby_score >= FAST_BABY_CRY_SCORE or (positive and sum(self.cry_votes) >= 2)
                if confirmed:
                    self.last_cry = now
                self.summarize_audio(observation, baby_score, rms, positive, confirmed)
            self.update(now)

    def summarize_audio(self, score, baby_score, rms, positive, confirmed):
        bucket = int(time.time()) // 5 * 5
        if not self.audio_summaries or self.audio_summaries[-1]["unix_time"] != bucket:
            self.audio_summaries.append({"unix_time": bucket, "windows": 0, "candidates": 0, "confirmed": 0,
                                         "peak_cry_score": 0.0, "peak_baby_score": 0.0, "peak_rms": 0.0})
        summary = self.audio_summaries[-1]
        summary["windows"] += 1
        summary["candidates"] += int(positive)
        summary["confirmed"] += int(confirmed)
        summary["peak_cry_score"] = max(summary["peak_cry_score"], round(score, 3))
        summary["peak_baby_score"] = max(summary["peak_baby_score"], round(baby_score, 3))
        summary["peak_rms"] = max(summary["peak_rms"], round(rms, 5))

    def audio_diagnostics(self):
        cutoff = time.time() - 300
        with self.condition:
            return {"cry_threshold": self.cry_threshold, "fast_baby_threshold": FAST_BABY_CRY_SCORE,
                    "windows": [row.copy() for row in self.audio_summaries if row["unix_time"] >= cutoff]}

    def update(self, now):
        if now - min(self.video_at, self.audio_at) > FRESH_SECONDS:
            if self.state != "offline":
                self.motion_samples.clear()
                self.cry_votes.clear()
                self.motion_energy = 0.0
                self.last_cry = float("-inf")
            self.transition("offline", 0, now)
            return
        level = int(self.motion_energy + 0.5)
        state = "crying" if now - self.last_cry < CRY_RELEASE_SECONDS else "moving" if level else "idle"
        self.transition(state, level, now)

    def reset_stream(self, now):
        with self.condition:
            self.video_at = self.audio_at = float("-inf")
            self.motion_samples.clear()
            self.cry_votes.clear()
            self.motion_energy = 0.0
            self.last_cry = float("-inf")
            self.update(now)

    def status(self, generation=None, after=None):
        with self.condition:
            if generation == self.generation and after == self.sequence:
                self.condition.wait_for(lambda: self.sequence != after, timeout=2.5)
            now = time.monotonic()
            self.update(now)
            return {
                "state": self.state,
                "generation": self.generation,
                "sequence": self.sequence,
                "motion_level": self.motion_level,
                "detected_at": self.detected_at,
                "server_time": time.time(),
                "state_age": round(now - self.changed, 2),
                "video_online": now - self.video_at <= FRESH_SECONDS,
                "audio_online": now - self.audio_at <= FRESH_SECONDS,
            }


class MotionDetector:
    def __init__(self, roi):
        left, top, right, bottom = roi
        self.region = np.s_[round(top * HEIGHT):round(bottom * HEIGHT),
                           round(left * WIDTH):round(right * WIDTH)]
        self.previous = deque(maxlen=4)

    def detect(self, data):
        frame = np.frombuffer(data, np.uint8).reshape(HEIGHT, WIDTH).astype(np.int16)
        previous = self.previous[0] if self.previous else None
        self.previous.append(frame)
        if previous is None:
            return 0.0
        delta = frame - previous
        delta -= int(np.median(delta))
        changed = np.abs(delta) >= 18
        region = changed[self.region]
        # Require adjacent changed pixels so isolated sensor noise does not count.
        clustered = region[1:, 1:] & region[1:, :-1] & region[:-1, 1:]
        return float(np.mean(clustered)) if float(np.mean(changed)) < 0.45 else 0.0


class CryDetector:
    def __init__(self, model, labels):
        self.model = Interpreter(model_path=model, num_threads=1)
        input_details = self.model.get_input_details()[0]
        if input_details["shape"].tolist() == [1]:
            self.model.resize_tensor_input(input_details["index"], [AUDIO_WINDOW])
        self.model.allocate_tensors()
        self.input = self.model.get_input_details()[0]
        self.output = next(t for t in self.model.get_output_details() if t["shape"][-1] == 521)
        with open(labels, encoding="utf-8", newline="") as table:
            self.labels = {row["display_name"]: int(row["index"]) for row in csv.DictReader(table)}

    def score(self, data):
        pcm = np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768
        rms = float(np.sqrt(np.mean(pcm ** 2)))
        if rms < MIN_AUDIO_RMS:
            return 0.0, 0.0, rms
        self.model.set_tensor(self.input["index"], pcm.reshape(self.input["shape"]))
        self.model.invoke()
        scores = self.model.get_tensor(self.output["index"]).reshape(-1, 521).max(axis=0)
        baby = float(scores[self.labels["Baby cry, infant cry"]])
        combined = max(baby, float(scores[self.labels["Crying, sobbing"]]) * 0.72)
        return combined, baby, rms


def analyze(state, roi, cry, source):
    audio_read, audio_write = os.pipe()
    try:
        process = subprocess.Popen([
            "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error",
            "-rtsp_transport", "tcp", "-timeout", "5000000", "-threads", "1", "-i", source,
            "-map", "0:v:0", "-filter_threads", "1",
            "-vf", f"fps={VIDEO_FPS},scale={WIDTH}:{HEIGHT}:flags=area,format=gray",
            "-threads", "1", "-f", "rawvideo", "pipe:1",
            "-map", "0:a:0", "-ac", "1", "-ar", str(AUDIO_RATE),
            "-f", "s16le", f"pipe:{audio_write}",
        ], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, pass_fds=(audio_write,))
    except BaseException:
        os.close(audio_read)
        raise
    finally:
        os.close(audio_write)

    stopped = threading.Event()
    motion = MotionDetector(roi)

    def video_worker():
        try:
            while data := process.stdout.read(WIDTH * HEIGHT):
                if len(data) == WIDTH * HEIGHT:
                    state.sample("video", motion.detect(data), time.monotonic())
        finally:
            stopped.set()

    def audio_worker():
        try:
            with os.fdopen(audio_read, "rb") as audio:
                window = audio.read(AUDIO_WINDOW * 2)
                while len(window) == AUDIO_WINDOW * 2:
                    score, baby_score, rms = cry.score(window)
                    state.sample("audio", score, time.monotonic(), baby_score, rms)
                    window = window[AUDIO_STEP * 2:] + audio.read(AUDIO_STEP * 2)
        finally:
            stopped.set()

    workers = [threading.Thread(target=target, daemon=True) for target in (video_worker, audio_worker)]
    for worker in workers:
        worker.start()
    started = time.monotonic()
    try:
        while not stopped.wait(0.5):
            status = state.status()
            if time.monotonic() - started > 12 and status["state"] == "offline":
                break
    finally:
        process.kill()
        process.wait()
        for worker in workers:
            worker.join()
        process.stdout.close()
        state.reset_stream(time.monotonic())


def serve(args, state):
    allowed = set(args.allow)

    class Handler(BaseHTTPRequestHandler):
        timeout = 2

        def do_GET(self):
            if self.client_address[0] not in allowed:
                self.send_error(403)
                return
            url = urlsplit(self.path)
            if url.path == "/audio-diagnostics":
                data = state.audio_diagnostics()
            elif url.path == "/status":
                query = parse_qs(url.query)
                generation = query.get("generation", [None])[0]
                try:
                    after = int(query["after"][0]) if "after" in query else None
                except ValueError:
                    self.send_error(400)
                    return
                data = state.status(generation, after)
            else:
                self.send_error(404)
                return
            body = json.dumps(data, separators=(",", ":")).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer((args.bind, args.port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", required=True)
    parser.add_argument("--port", type=int, default=18772)
    parser.add_argument("--allow", action="append", required=True)
    parser.add_argument("--roi", nargs=4, type=float, required=True, metavar=("LEFT", "TOP", "RIGHT", "BOTTOM"))
    parser.add_argument("--cry-threshold", type=float, default=0.22)
    parser.add_argument("--source", required=True, help="RTSP stream containing both video and audio")
    args = parser.parse_args()
    source = urlsplit(args.source)
    if source.scheme not in ("rtsp", "rtsps") or not source.hostname:
        parser.error("provide an RTSP camera or bridge URL")
    left, top, right, bottom = args.roi
    if not (0 <= left < right <= 1 and 0 <= top < bottom <= 1 and
            right - left >= 0.1 and bottom - top >= 0.1 and 0 < args.cry_threshold < 1):
        parser.error("use a normalized nonempty region and a threshold between 0 and 1")
    cry = CryDetector("yamnet.tflite", "yamnet_class_map.csv")
    state = MonitorState(time.monotonic(), args.cry_threshold)
    serve(args, state)
    while True:
        analyze(state, args.roi, cry, args.source)
        time.sleep(3)


if __name__ == "__main__":
    main()
