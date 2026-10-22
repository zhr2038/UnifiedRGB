"""Data-only ScenePackage v1/v2/v3/v4 authoring helper. Runtime import remains authoritative.

schemaVersion 2 only adds two optional screen fields: accentColor (#RRGGBB) and overlayLayout (a telemetry
region on rectangular screens). v3 adds MP4/H.264. v4 adds bounded MP4 sequences. Earlier schemas retain their original meaning."""
from __future__ import annotations
import argparse
import hashlib
import json
import math
from pathlib import Path, PurePosixPath
import re
import struct
import tempfile
import zipfile
import zlib

FORMAT = "UnifiedRgb.ScenePackage"
CONTRACT = "rgb24-hold-physical-ms-v1"
INERT = {"README.md", "README.txt", "bindings.template.json"}
SCHEMAS = (1, 2, 3, 4)
REGION_KEYS = ("x", "y", "width", "height", "columns", "backgroundOpacity")
V2_SCREEN_FIELDS = ("accentColor", "overlayLayout")
RESERVED = {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"} | {f"{p}{i}" for p in ("COM", "LPT") for i in range(1, 10)}

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON property: {key}")
        result[key] = value
    return result

def relative_name(name):
    if not isinstance(name, str) or not 1 <= len(name) <= 200 or not re.fullmatch(r"[A-Za-z0-9._/-]+", name):
        raise ValueError(f"Unsafe asset path: {name!r}")
    if any(p in ("", ".", "..") or p.endswith(".") or p.split(".")[0].upper() in RESERVED for p in name.split("/")):
        raise ValueError(f"Unsafe asset path: {name!r}")
    return name

def finite_positive(value, what):
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value) or value <= 0:
        raise ValueError(f"{what} must be positive and finite")
    return value

def integer(value, low, high, what):
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{what} must be an integer in {low}..{high}")
    return value

def number(value, what):
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value):
        raise ValueError(f"{what} must be a finite number")
    return value

def screen_v2(track, role):
    """Checks the optional v2 accentColor/overlayLayout of one screen track (same rules as the application)."""
    if "accentColor" in track:
        accent = track["accentColor"]
        if not isinstance(accent, str) or not re.fullmatch(r"#[0-9A-Fa-f]{6}", accent):
            raise ValueError(f"{role}: accentColor must be #RRGGBB")
        if track.get("overlay") == "none":
            raise ValueError(f"{role}: accentColor needs a compact or dashboard overlay")
    if "overlayLayout" in track:
        layout = track["overlayLayout"]
        if not isinstance(layout, dict) or sorted(layout) != sorted(REGION_KEYS):
            raise ValueError(f"{role}: overlayLayout must be an object with exactly {', '.join(REGION_KEYS)}")
        if track.get("overlay") != "dashboard":
            raise ValueError(f"{role}: overlayLayout needs overlay dashboard")
        x, y, w, h, o = (number(layout[k], f"{role}: overlayLayout.{k}") for k in ("x", "y", "width", "height", "backgroundOpacity"))
        if not (0 <= x <= 1 and 0 <= y <= 1):
            raise ValueError(f"{role}: overlayLayout x and y must be within 0..1")
        if not (0 < w <= 1 and 0 < h <= 1):
            raise ValueError(f"{role}: overlayLayout width and height must be > 0 and <= 1")
        if x + w > 1 or y + h > 1:
            raise ValueError(f"{role}: overlayLayout must lie inside the canvas")
        integer(layout["columns"], 1, 4, f"{role}: overlayLayout.columns")
        if not 0 <= o <= 1:
            raise ValueError(f"{role}: overlayLayout backgroundOpacity must be within 0..1")

def prepare(folder):
    folder = Path(folder).resolve(strict=True)
    raw = (folder / "scene.json").read_bytes()
    if len(raw) > 256 * 1024:
        raise ValueError("scene.json exceeds 256 KiB")
    manifest = json.loads(raw, object_pairs_hook=unique_object)
    schema = manifest.get("schemaVersion")
    if manifest.get("format") != FORMAT or type(schema) is not int or schema not in SCHEMAS or manifest.get("playbackContract") != CONTRACT:
        raise ValueError("Explicit ScenePackage v1/v2/v3/v4 / rgb24-hold-physical-ms-v1 required; drafts are never upgraded")
    if manifest.get("loop") is not True:
        raise ValueError("v1 requires loop:true")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", manifest.get("id", "")):
        raise ValueError("Invalid package id")
    integer(manifest.get("revision"), 1, 2**31-1, "revision")
    duration = finite_positive(manifest.get("durationMs"), "durationMs")
    if duration > 600000:
        raise ValueError("durationMs exceeds 600000")
    lighting, screens = manifest.get("lighting", []), manifest.get("screens", [])
    if not isinstance(lighting, list) or not isinstance(screens, list) or not lighting + screens or len(lighting) > 64 or len(screens) > 16:
        raise ValueError("Need 1+ tracks, at most 64 lighting / 16 screen tracks")
    roles, files, folded = set(), {}, set()
    timing, rgb_total = None, 0
    for kind, tracks in (("lighting", lighting), ("screen", screens)):
        for track in tracks:
            role = track.get("role", "")
            if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", role) or role in roles:
                raise ValueError(f"Invalid or duplicate role: {role}")
            roles.add(role)
            if "segments" in track:
                if schema != 4 or kind != "screen" or "file" in track:
                    raise ValueError(f"{role}: segments require schemaVersion 4 screen media without file")
                clips = track["segments"]
                if not isinstance(clips,list) or not 2 <= len(clips) <= 32:
                    raise ValueError(f"{role}: need 2..32 segments")
                total = 0
                for clip in clips:
                    if not isinstance(clip,dict) or set(clip) != {"file","durationMs"}:
                        raise ValueError(f"{role}: invalid segment properties")
                    total += integer(clip["durationMs"],1,30000,"segment durationMs")
                    name = relative_name(clip["file"])
                    if PurePosixPath(name).suffix.lower() != ".mp4" or name.casefold() in folded and name not in files:
                        raise ValueError(f"{role}: invalid/colliding segment file")
                    path = (folder/name).resolve(strict=True)
                    if not path.is_relative_to(folder) or not path.is_file() or not 16 <= path.stat().st_size <= 12*1024*1024:
                        raise ValueError(f"{role}: segment escapes source or exceeds per-file budget")
                    data=path.read_bytes()
                    if data[4:8] != b"ftyp": raise ValueError(f"{role}: segment is not MP4")
                    files[name]=data;folded.add(name.casefold())
                if total != duration or track.get("durationMs") != duration or track.get("overlay") not in ("none","compact","dashboard") or track.get("fit") not in ("contain","cover"):
                    raise ValueError(f"{role}: invalid sequence timing/track")
                integer(track.get("fps"),1,60,"fps")
                screen_v2(track,role)
                if sum(len(files[c["file"]]) for c in clips) > 64*1024*1024:
                    raise ValueError(f"{role}: sequence sources exceed 64 MiB")
                if sum(map(len,files.values())) > 256*1024*1024:
                    raise ValueError("Expanded package exceeds 256 MiB")
                continue
            name = relative_name(track.get("file"))
            if name == "scene.json" or name.casefold() in folded and name not in files:
                raise ValueError(f"Colliding asset name: {name}")
            path = (folder / name).resolve(strict=True)
            if not path.is_relative_to(folder) or not path.is_file():
                raise ValueError(f"Asset escapes source folder: {name}")
            if kind == "lighting":
                if PurePosixPath(name).suffix.lower() != ".rgb24":
                    raise ValueError("Lighting files must use .rgb24")
                count = integer(track.get("frames"), 1, 4096, "frames")
                leds = integer(track.get("ledCount"), 1, 4096, "ledCount")
                dwell = finite_positive(track.get("frameDurationMs"), "frameDurationMs")
                if dwell > 60000 or track.get("pixelFormat") != "RGB24" or track.get("pixelOrder") != "logical-layout":
                    raise ValueError(f"Invalid RGB24 contract: {role}")
                if not math.isclose(count * dwell, duration, rel_tol=1e-9) or timing not in (None, (count, dwell)):
                    raise ValueError("All lighting tracks must have equal frames/dwell and match durationMs")
                timing = (count, dwell)
                expected = count * leds * 3
                if path.stat().st_size != expected:
                    raise ValueError(f"{name}: expected {expected} RGB24 bytes")
                rgb_total += expected
                if rgb_total > 32*1024*1024:
                    raise ValueError("Lighting data exceeds 32 MiB")
                baked = track.get("brightnessBaked", 1)
                if isinstance(baked, bool) or not isinstance(baked, (int,float)) or not math.isfinite(baked) or not 0 <= baked <= 1:
                    raise ValueError("brightnessBaked must be metadata in 0..1")
            else:
                video = schema >= 3 and PurePosixPath(name).suffix.lower() == ".mp4"
                if (not video and PurePosixPath(name).suffix.lower() not in (".png", ".jpg", ".jpeg", ".gif")) or path.stat().st_size > 12*1024*1024:
                    raise ValueError(f"Unsupported or oversized screen asset: {name}")
                if track.get("durationMs") != duration or track.get("overlay") not in ("none", "compact", "dashboard") or track.get("fit") not in ("contain", "cover"):
                    raise ValueError(f"Invalid screen track: {role}")
                integer(track.get("fps"), 1, 60 if video else 15, "fps")
                if schema == 1:
                    for field in V2_SCREEN_FIELDS:
                        if field in track:  # even an explicit null: v1 never had these fields
                            raise ValueError(f"{role}: {field} needs schemaVersion 2")
                else:
                    screen_v2(track, role)
            folded.add(name.casefold())
            if name not in files and sum(map(len, files.values())) + path.stat().st_size > 256*1024*1024:
                raise ValueError("Expanded package exceeds 256 MiB")
            files[name] = path.read_bytes()
    if rgb_total > 32*1024*1024:
        raise ValueError("Lighting data exceeds 32 MiB")
    for name in sorted(INERT):
        path = folder / name
        if path.exists():
            resolved = path.resolve(strict=True)
            if not resolved.is_relative_to(folder) or resolved.stat().st_size > 256*1024:
                raise ValueError(f"Invalid inert file: {name}")
            files[name] = resolved.read_bytes()
    if len(files) + 1 > 128 or sum(map(len, files.values())) > 256*1024*1024:
        raise ValueError("Too many entries or expanded package too large")
    manifest["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())}
    encoded = (json.dumps(manifest, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
    if len(encoded) > 256*1024:
        raise ValueError("Generated manifest exceeds 256 KiB")
    return encoded, files

def build(folder, output):
    manifest, files = prepare(folder)
    output = Path(output).resolve()
    if output.exists():
        raise ValueError("Output already exists; use a new revision filename")
    output.parent.mkdir(parents=True, exist_ok=True)
    # A fixed ZIP timestamp/order makes repeated authoring deterministic. Source files are never edited.
    with tempfile.TemporaryDirectory(prefix="scene-build-", dir=output.parent) as stage:
        temporary = Path(stage) / "package.zip"
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, data in [("scene.json", manifest), *sorted(files.items())]:
                entry = zipfile.ZipInfo(name, (2020, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                archive.writestr(entry, data)
        if temporary.stat().st_size > 64*1024*1024:
            raise ValueError("ZIP exceeds 64 MiB")
        # Windows rename refuses an existing destination; never silently replace a released artifact.
        temporary.rename(output)
    return {"path": str(output), "contentId": hashlib.sha256(manifest).hexdigest(), "zipSha256": hashlib.sha256(output.read_bytes()).hexdigest(), "bytes": output.stat().st_size, "validation": "authoring checks only; import into the application for authoritative media/device/wire validation"}

def sample(folder, schema=1):
    if schema not in (1, 2):
        raise ValueError("schema must be 1 or 2")
    folder = Path(folder).resolve()
    if folder.exists():
        raise ValueError("Sample directory already exists")
    (folder / "lighting").mkdir(parents=True)
    (folder / "screens").mkdir()
    rgb = bytearray()
    for frame in range(200):
        for led in range(8):
            level = max(0, 1 - abs((frame % 50)/49 - led/7)*3)
            rgb.extend((int(60*level), int(220*level), int(180*level)))
    (folder / "lighting/strip.rgb24").write_bytes(rgb)
    def chunk(tag, data):
        return struct.pack(">I",len(data)) + tag + data + struct.pack(">I",zlib.crc32(tag+data)&0xffffffff)
    pixels = b"".join(b"\0" + bytes((13,31,38))*64 for _ in range(32))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR",struct.pack(">IIBBBBB",64,32,8,2,0,0,0)) + chunk(b"IDAT",zlib.compress(pixels)) + chunk(b"IEND",b"")
    (folder / "screens/panel.png").write_bytes(png)
    manifest = {"format":FORMAT,"schemaVersion":1,"id":"aurora-starter","name":"极光 · 入门示例","revision":1,"durationMs":12000,"loop":True,"playbackContract":CONTRACT,"lighting":[{"role":"accent-strip","label":"装饰灯条","file":"lighting/strip.rgb24","ledCount":8,"frames":200,"frameDurationMs":60,"pixelFormat":"RGB24","pixelOrder":"logical-layout","brightnessBaked":1}],"screens":[{"role":"main-panel","label":"主画面","file":"screens/panel.png","durationMs":12000,"overlay":"dashboard","fit":"contain","fps":5}],"files":{}}
    if schema == 2:
        # a generic telemetry-region starter for a wide rectangular screen: real PC status on the right half
        manifest.update({"schemaVersion":2,"id":"region-starter","name":"区域状态 · 入门示例"})
        manifest["screens"][0].update({"label":"宽屏画面","fit":"cover","accentColor":"#FAD61D","overlayLayout":{"x":0.5,"y":0.16,"width":0.48,"height":0.74,"columns":2,"backgroundOpacity":0}})
    (folder / "scene.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    return {"path":str(folder),"next":"edit assets/scene.json, then build; device bindings are chosen separately inside the application"}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    make = sub.add_parser("sample"); make.add_argument("folder"); make.add_argument("--schema", type=int, choices=(1,2), default=1)
    pack = sub.add_parser("build"); pack.add_argument("folder"); pack.add_argument("output")
    args = parser.parse_args()
    try:
        result = sample(args.folder, args.schema) if args.command == "sample" else build(args.folder,args.output)
    except (ValueError,OSError,KeyError,TypeError) as error:
        parser.exit(1,f"ERROR: {error}\n")
    print(json.dumps(result,ensure_ascii=False,indent=2))

if __name__ == "__main__":
    main()
