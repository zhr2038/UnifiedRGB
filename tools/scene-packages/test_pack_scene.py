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

if __name__ == "__main__":
    unittest.main()
