import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("pack_scene", Path(__file__).with_name("pack_scene.py"))
pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack)

class AuthoringTests(unittest.TestCase):
    def test_v4_sequence_pins_each_clip_and_rejects_ambiguous_or_old_schema(self):
        video=Path(__file__).resolve().parents[2]/"tests"/"Fixtures"/"media"/"h264-30.mp4"
        for n in ["a","b"]:(self.source/f"screens/{n}.mp4").write_bytes(video.read_bytes())
        track={"role":"video-panel","segments":[{"file":f"screens/{n}.mp4","durationMs":6000} for n in ["a","b"]],"durationMs":12000,"overlay":"none","fit":"contain","fps":30}
        self.edit(lambda m:m.update(schemaVersion=4,screens=[track]))
        encoded,files=pack.prepare(self.source)
        self.assertEqual(4,json.loads(encoded)["schemaVersion"])
        self.assertIn("screens/a.mp4",files);self.assertIn("screens/b.mp4",files)
        self.edit(lambda m:m["screens"][0].update(file="screens/a.mp4"))
        with self.assertRaisesRegex(ValueError,"without file"):pack.prepare(self.source)
        self.edit(lambda m:m.update(schemaVersion=3))
        with self.assertRaisesRegex(ValueError,"schemaVersion 4"):pack.prepare(self.source)

    def test_sample_does_not_silently_downgrade_a_video_schema(self):
        with self.assertRaisesRegex(ValueError,"schema must be 1 or 2"):
            pack.sample(self.root/"video",schema=3)
        self.assertFalse((self.root/"video").exists())

    def test_v3_mp4_is_explicit_and_source_media_is_unchanged(self):
        video = Path(__file__).resolve().parents[2] / "tests" / "Fixtures" / "media" / "h264-30.mp4"
        target = self.source / "screens" / "panel.mp4"
        target.write_bytes(video.read_bytes())
        self.edit(lambda m: m.update(schemaVersion=3, screens=[{"role":"video-panel","file":"screens/panel.mp4","durationMs":12000,"overlay":"dashboard","fit":"contain","fps":30}]))
        before=target.read_bytes()
        manifest,_=pack.prepare(self.source)
        self.assertEqual(3,json.loads(manifest)["schemaVersion"])
        self.assertEqual(before,target.read_bytes())
        self.edit(lambda m:m.update(schemaVersion=2))
        with self.assertRaises(ValueError): pack.prepare(self.source)
        # This helper checks package structure; the Windows importer remains authoritative for video decoding and duration.

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        pack.sample(self.source)

    def edit(self, change):
        path = self.source / "scene.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        change(data)
        path.write_text(json.dumps(data),encoding="utf-8")

    def test_packages_are_deterministic_pin_all_assets_and_leave_authored_files_untouched(self):
        before = {str(p.relative_to(self.source)):p.read_bytes() for p in self.source.rglob("*") if p.is_file()}
        a,b = self.root/"a.zip", self.root/"b.zip"
        pack.build(self.source,a); pack.build(self.source,b)
        self.assertEqual(a.read_bytes(),b.read_bytes())
        self.assertEqual(before,{str(p.relative_to(self.source)):p.read_bytes() for p in self.source.rglob("*") if p.is_file()})
        with zipfile.ZipFile(a) as z:
            m = json.loads(z.read("scene.json"))
            self.assertEqual(set(z.namelist()),{"scene.json",*m["files"]})
            for name, expected in m["files"].items():
                self.assertEqual(expected,hashlib.sha256(z.read(name)).hexdigest())
            self.assertEqual(len(z.read(m["lighting"][0]["file"])),200*8*3)

    def test_released_artifact_is_not_overwritten(self):
        out=self.root/"release.zip"; out.write_bytes(b"keep")
        with self.assertRaisesRegex(ValueError,"already exists"): pack.build(self.source,out)
        self.assertEqual(out.read_bytes(),b"keep")

    def test_wrong_rgb_length_is_rejected_without_publishing(self):
        (self.source/"lighting/strip.rgb24").write_bytes(b"bad")
        with self.assertRaisesRegex(ValueError,"expected"): pack.build(self.source,self.root/"bad.zip")
        self.assertFalse((self.root/"bad.zip").exists())

    def test_draft_is_never_silently_converted(self):
        self.edit(lambda m:m.update(format="UnifiedRgb.ScenePackage.Draft"))
        with self.assertRaisesRegex(ValueError,"never upgraded"): pack.build(self.source,self.root/"bad.zip")

    def test_path_traversal_and_windows_reserved_names_are_rejected(self):
        self.edit(lambda m:m["lighting"][0].update(file="../outside.rgb24"))
        with self.assertRaisesRegex(ValueError,"Unsafe"): pack.build(self.source,self.root/"bad.zip")
        for name in ("/absolute", "C:/drive", "a\\b", "a//b", "A/CON.txt", "a./b"):
            with self.assertRaises(ValueError): pack.relative_name(name)

    def test_case_colliding_names_are_rejected(self):
        def change(m):
            track = dict(m["lighting"][0], role="second-strip", file="lighting/STRIP.rgb24")
            m["lighting"].append(track)
        self.edit(change)
        with self.assertRaisesRegex(ValueError,"Colliding"): pack.build(self.source,self.root/"bad.zip")

    def test_executable_extension_is_not_treated_as_rgb_data(self):
        path=self.source/"lighting/strip.rgb24"; path.rename(path.with_suffix(".ps1"))
        self.edit(lambda m:m["lighting"][0].update(file="lighting/strip.ps1"))
        with self.assertRaisesRegex(ValueError,".rgb24"): pack.build(self.source,self.root/"bad.zip")

    def test_mismatched_timing_and_duplicate_roles_are_rejected(self):
        self.edit(lambda m:m["lighting"][0].update(frameDurationMs=50))
        with self.assertRaisesRegex(ValueError,"match duration"): pack.build(self.source,self.root/"bad.zip")
        self.edit(lambda m:(m["lighting"][0].update(frameDurationMs=60),m["screens"][0].update(role="accent-strip")))
        with self.assertRaisesRegex(ValueError,"duplicate role"): pack.build(self.source,self.root/"bad.zip")

    def test_schema1_output_is_unchanged(self):
        # the shipped aurora-starter-v1.zip manifest: v2 support must not change one v1 byte
        result = pack.build(self.source,self.root/"v1.zip")
        self.assertEqual(result["contentId"],"17313811f9c4bdbca3857ac84653f06ce66658877783500a7f07506b0fe09710")
        with zipfile.ZipFile(self.root/"v1.zip") as z:
            screen = json.loads(z.read("scene.json"))["screens"][0]
        self.assertNotIn("accentColor",screen); self.assertNotIn("overlayLayout",screen)

    def test_schema1_rejects_each_v2_field_even_as_null(self):
        for field, value in (("accentColor","#FAD61D"),("accentColor",None),("overlayLayout",{"x":0,"y":0,"width":1,"height":1,"columns":1,"backgroundOpacity":0}),("overlayLayout",None)):
            self.edit(lambda m:(m["screens"][0].pop("accentColor",None),m["screens"][0].pop("overlayLayout",None),m["screens"][0].update({field:value})))
            with self.assertRaisesRegex(ValueError,"needs schemaVersion 2"): pack.build(self.source,self.root/"bad.zip")

    def test_unknown_schema_versions_fail_closed(self):
        for version in (0,5,"2",2.0,True):
            self.edit(lambda m:m.update(schemaVersion=version))
            with self.assertRaisesRegex(ValueError,"v1/v2/v3"): pack.build(self.source,self.root/"bad.zip")

class RegionAuthoringTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        pack.sample(self.source, schema=2)

    def edit(self, change):
        path = self.source / "scene.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        change(data["screens"][0])
        path.write_text(json.dumps(data),encoding="utf-8")

    def bad(self, change, message):
        self.edit(change)
        with self.assertRaisesRegex(ValueError,message): pack.build(self.source,self.root/"bad.zip")
        self.assertFalse((self.root/"bad.zip").exists())
        pack.sample(self.root/"fresh", schema=2)
        (self.source/"scene.json").write_bytes((self.root/"fresh"/"scene.json").read_bytes())
        import shutil; shutil.rmtree(self.root/"fresh")

    def test_region_package_is_deterministic_and_keeps_the_fields(self):
        a,b = self.root/"a.zip", self.root/"b.zip"
        pack.build(self.source,a); pack.build(self.source,b)
        self.assertEqual(a.read_bytes(),b.read_bytes())
        with zipfile.ZipFile(a) as z:
            m = json.loads(z.read("scene.json"))
        self.assertEqual(m["schemaVersion"],2)
        self.assertEqual(m["screens"][0]["overlay"],"dashboard")
        self.assertEqual(m["screens"][0]["accentColor"],"#FAD61D")
        self.assertEqual(m["screens"][0]["overlayLayout"],{"x":0.5,"y":0.16,"width":0.48,"height":0.74,"columns":2,"backgroundOpacity":0})

    def test_v2_without_a_region_is_valid(self):
        self.edit(lambda t:(t.pop("overlayLayout"),t.pop("accentColor")))
        pack.build(self.source,self.root/"plain.zip")

    def test_bad_regions_are_rejected(self):
        layout = lambda **kw: (lambda t:t["overlayLayout"].update(kw))
        for change, message in (
            (layout(x=-0.1),"x and y"),(layout(y=1.5),"x and y"),(layout(width=0),"width and height"),(layout(height=1.2),"width and height"),
            (layout(width=0.6),"inside the canvas"),(layout(height=0.9),"inside the canvas"),(layout(x=float("inf")),"finite"),(layout(x=float("nan")),"finite"),
            (layout(x=True),"finite"),(layout(x="0.5"),"finite"),(layout(columns=0),"columns"),(layout(columns=5),"columns"),(layout(columns=2.0),"columns"),
            (layout(columns="2"),"columns"),(layout(backgroundOpacity=1.01),"backgroundOpacity"),(layout(backgroundOpacity=None),"finite"),
            (layout(padding=0.1),"exactly"),(lambda t:t["overlayLayout"].pop("columns"),"exactly"),(lambda t:t.update(overlayLayout=None),"exactly"),
            (lambda t:t.update(overlayLayout=[0.5]),"exactly"),(lambda t:t.update(overlay="compact"),"needs overlay dashboard"),
            (lambda t:t.update(overlay="none"),"accentColor needs"),(lambda t:(t.pop("accentColor"),t.update(overlay="none")),"needs overlay dashboard"),
            (lambda t:t.update(accentColor="#FAD61"),"#RRGGBB"),(lambda t:t.update(accentColor="FAD61D"),"#RRGGBB"),(lambda t:t.update(accentColor="#GGGGGG"),"#RRGGBB"),
            (lambda t:t.update(accentColor="#FAD61D\n"),"#RRGGBB"),(lambda t:t.update(accentColor=None),"#RRGGBB"),
        ):
            with self.subTest(message=message):
                self.bad(change, message)

if __name__ == "__main__":
    unittest.main()
