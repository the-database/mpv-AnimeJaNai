import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("assembly", Path(__file__).parents[1] / "assemble-addon-preview.py")
assembly = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assembly)

class NativeAssemblyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.record = dict(schemaVersion=1, platform="win-x64", cRuntime="ucrt", ffmpegLinkage="static",
                           privateSampleAbi=1, privatePlayerSampleAbi=1, privateOutputAbi=1, nullHardwareFramesOptIn=1, files={})
        for name in ("mpv.exe", "libmpv-2.dll"):
            (self.root / name).write_bytes(name.encode())
            self.record["files"][name] = assembly.sha(self.root / name)
    def test_static_needs_no_separate_muxer(self):
        assembly.verify_native(self.root, self.record)
    def test_complete_shared_dependency_set(self):
        self.record["ffmpegLinkage"] = "shared"
        for name in ["avformat-63.dll", "libstdc++-6.dll", *[f"dependency-{i}.dll" for i in range(135)]]:
            (self.root / name).write_bytes(name.encode())
            self.record["files"][name] = assembly.sha(self.root / name)
        assembly.verify_native(self.root, self.record)
        self.record["ffmpegLinkage"] = "static"
        with self.assertRaises(ValueError): assembly.verify_native(self.root, self.record)
    def test_windows_duplicate_filenames(self):
        self.record["files"]["LIBMPV-2.dll"] = self.record["files"]["libmpv-2.dll"]
        with self.assertRaises(ValueError): assembly.verify_native(self.root, self.record)
    def test_retiring_shared_dependencies_keeps_unrelated_core_files(self):
        info = self.root / "build-info"
        info.mkdir()
        old = self.root / "libSvtAv1Enc-4.dll"
        old.write_bytes(b"old dependency")
        unrelated = self.root / "manager-helper.dll"
        unrelated.write_bytes(b"keep")
        (info / "runtime-origins.json").write_text(json.dumps({old.name.lower(): "historical build path"}))
        (info / "sha256.json").write_text(json.dumps({old.name: assembly.sha(old)}))
        assembly.retire_core_native(self.root, self.record["files"])
        self.assertFalse(old.exists())
        self.assertTrue(unrelated.exists())
    def test_changed_binary(self):
        (self.root / "libmpv-2.dll").write_bytes(b"different build")
        with self.assertRaises(ValueError): assembly.verify_native(self.root, self.record)
    def test_missing_capability(self):
        del self.record["nullHardwareFramesOptIn"]
        with self.assertRaises(ValueError): assembly.verify_native(self.root, self.record)
    def test_unknown_record(self):
        self.record["schemaVersion"] = 2
        with self.assertRaises(ValueError): assembly.verify_native(self.root, self.record)
    def test_paths_stay_relative(self):
        self.assertEqual(assembly.relative("addon-host/runtime/wasmtime.exe"), "addon-host/runtime/wasmtime.exe")
        for name in ("../file", "/file", "D:/file", "folder\\file", ""):
            with self.subTest(name=name), self.assertRaises(ValueError): assembly.relative(name)

if __name__ == "__main__": unittest.main()
