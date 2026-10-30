#!/usr/bin/env python3
"""Create an offline RGB24 example from a local light map; never connects to devices."""
import argparse
import colorsys
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--layout", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=40)
    parser.add_argument("--frame-ms", type=int, default=100)
    args = parser.parse_args()
    if not 1 <= args.frames <= 10000 or not 1 <= args.frame_ms <= 60000:
        parser.error("frames must be 1..10000 and frame-ms must be 1..60000")
    data = json.loads(args.layout.read_text(encoding="utf-8-sig"))
    data = data.get("UnifiedRgb", data)
    data = data.get("AsusRog", data)
    data = data.get("KeyboardPerKey", data)
    keys = data["Maps"][0]["Keys"]
    if not 1 <= len(keys) <= 128:
        parser.error("the selected map must contain 1..128 independent light positions")
    columns = [float(k["Column"]) for k in keys]
    left, span = min(columns), max(columns) - min(columns)
    keyboard, mouse = bytearray(), bytearray()
    for frame in range(args.frames):
        phase = frame / args.frames
        for key in keys:  # Keep the local logical order, never sort by native LED id.
            position = (float(key["Column"]) - left) / span if span else 0.5
            rgb = colorsys.hsv_to_rgb((position + phase + float(key["Row"]) * 0.017) % 1, 1, 0.75)
            keyboard.extend(round(v * 255) for v in rgb)
        mouse.extend(round(v * 255) for v in colorsys.hsv_to_rgb((phase + 0.5) % 1, 1, 0.75))
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / "lighting").mkdir()
    tracks = []
    for role, count, payload in (("keyboard-leds", len(keys), keyboard), ("mouse-rgb", 1, mouse)):
        file = f"lighting/{role}.rgb24"
        (args.out / file).write_bytes(payload)
        tracks.append({"role": role, "file": file, "ledCount": count, "frames": args.frames,
                       "frameDurationMs": args.frame_ms, "pixelFormat": "RGB24",
                       "pixelOrder": "logical-layout", "brightnessBaked": 1})
    manifest = {"format": "UnifiedRgb.ScenePackage", "schemaVersion": 1,
                "id": "peripheral-led-demo", "name": "外设逐灯联动示例", "revision": 1,
                "durationMs": args.frames * args.frame_ms, "loop": True,
                "playbackContract": "rgb24-hold-physical-ms-v1",
                "lighting": tracks, "screens": [], "files": {}}
    (args.out / "scene.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Created {len(keys)}-light keyboard + 1-light mouse, {manifest['durationMs']} ms: {args.out}")


if __name__ == "__main__":
    main()
