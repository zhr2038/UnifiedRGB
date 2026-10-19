"""Regenerate the public 9-light/5-screen fixture using only the Python standard library.

Synthetic RGB and two-colour GIF frames contain no personal theme, binding or device data.
The manifest, timeline and archive bytes are intentionally deterministic.
"""

import hashlib
import json
import struct
import zipfile
from pathlib import Path


def gif():
    width, height = 8, 4
    data = bytearray(b"GIF89a" + struct.pack("<HHBBB", width, height, 0x91, 0, 0))
    data.extend(bytes((0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255)))
    for colour in (1, 3):
        data.extend(b"\x21\xf9\x04\x04" + struct.pack("<H", 600) + b"\0\0")
        data.extend(b"\x2c" + struct.pack("<HHHH", 0, 0, width, height) + b"\0\x02")
        codes = [4]
        for pixel in range(width * height):
            if pixel and pixel % 2 == 0:
                codes.append(4)
            codes.append(colour)
        codes.append(5)
        value = sum(code << (3 * index) for index, code in enumerate(codes))
        payload = value.to_bytes((3 * len(codes) + 7) // 8, "little")
        data.extend(bytes((len(payload),)) + payload + b"\0")
    data.append(0x3B)
    return bytes(data)


def generate():
    files = {}
    lighting, screens = [], []
    for track in range(9):
        name = f"lighting/strip-{track + 1}.rgb24"
        rgb = bytearray()
        for frame in range(200):
            for led in range(8):
                rgb.extend((255, 255, 255) if led == (frame + track) % 8 else (0, 0, frame * 7 % 64))
        files[name] = bytes(rgb)
        lighting.append(dict(role=f"strip-{track + 1}", file=name, ledCount=8,
                             frames=200, frameDurationMs=60, pixelFormat="RGB24", pixelOrder="logical-layout"))
    for track in range(5):
        name = f"media/panel-{track + 1}.gif"
        files[name] = gif()
        screens.append(dict(role=f"panel-{track + 1}", file=name, durationMs=12000,
                            overlay="compact", fit="cover", fps=5))
    manifest = dict(format="UnifiedRgb.ScenePackage", schemaVersion=1, id="generic-full-case",
                    name="Generic full-case fixture", revision=1, durationMs=12000, loop=True,
                    playbackContract="rgb24-hold-physical-ms-v1", lighting=lighting, screens=screens,
                    files={name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())})
    raw = json.dumps(manifest, ensure_ascii=True, separators=(",", ":")).encode()
    destination = Path(__file__).resolve().parents[1] / "UnifiedRgb.Host.Tests/Fixtures/ScenePackages/generic-nine-five-v1.zip"
    with zipfile.ZipFile(destination, "w") as archive:
        for name, data in [("scene.json", raw), *sorted(files.items())]:
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 19, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            archive.writestr(info, data)
    print(json.dumps(dict(contentId=hashlib.sha256(raw).hexdigest(),
                          zipSha256=hashlib.sha256(destination.read_bytes()).hexdigest(),
                          bytes=destination.stat().st_size)))


if __name__ == "__main__":
    generate()
