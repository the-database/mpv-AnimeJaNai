import importlib.util
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
