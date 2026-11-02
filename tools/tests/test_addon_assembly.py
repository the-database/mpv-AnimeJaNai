import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("assembly", Path(__file__).parents[1] / "assemble-addon-preview.py")
assembly = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assembly)

class NativeAssemblyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.record = dict(schemaVersion=1, platform="win-x64", cRuntime="ucrt", ffmpegLinkage="static",
                           privateSampleAbi=1, privatePlayerSampleAbi=1, privateOutputAbi=1, nullHardwareFramesOptIn=1,
                           privateProbeAbi=1, privateMuxAbi=1, privateSubtitlesAbi=1, files={})
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
    def test_streaming_release_rejects_older_or_partial_native_runtime(self):
        for capability in ("privateProbeAbi", "privateMuxAbi", "privateSubtitlesAbi"):
            for value in (None, 0, 2):
                record = dict(self.record)
                if value is None: del record[capability]
                else: record[capability] = value
                with self.subTest(capability=capability, value=value), self.assertRaises(ValueError):
                    assembly.verify_native(self.root, record)
    def test_unknown_record(self):
        self.record["schemaVersion"] = 2
        with self.assertRaises(ValueError): assembly.verify_native(self.root, self.record)
    def test_paths_stay_relative(self):
        self.assertEqual(assembly.relative("addon-host/runtime/wasmtime.exe"), "addon-host/runtime/wasmtime.exe")
        for name in ("../file", "/file", "D:/file", "folder\\file", ""):
            with self.subTest(name=name), self.assertRaises(ValueError): assembly.relative(name)

class HostAssemblyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.record = dict(schemaVersion=1, platform="win-x64", sourceCommit="a" * 40,
            sourceObjects={"addons": "b" * 40, "shared": "c" * 40, "LICENSE": "d" * 40}, dotnetSdk="fixture", files={})
        for name in ("host/ajn-addon.exe", "host/ajn-addon-launcher.exe", "host/ajn-addon.deps.json", "runtime/wasmtime.exe"):
            path = self.root / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(name.encode())
            self.record["files"][name] = assembly.sha(path)
        self.expected_objects = dict(self.record["sourceObjects"])
        self.mock_git = patch.object(assembly.subprocess, "check_output", side_effect=lambda args, **kwargs:
            self.expected_objects[args[-1].split(":")[-1]] + "\n")
        self.mock_git.start()
        self.addCleanup(self.mock_git.stop)
        self.save()

    def save(self):
        (self.root / "host-build.json").write_text(json.dumps(self.record))

    def verify(self):
        return assembly.verify_host_bundle(self.root, self.root, "fixture-git")

    def test_producing_bundle_matches_committed_source(self):
        self.assertEqual(self.verify()["sourceCommit"], "a" * 40)

    def test_stale_host_source_tree(self):
        self.record["sourceObjects"]["addons"] = "e" * 40
        self.save()
        with self.assertRaisesRegex(ValueError, "committed source differ"): self.verify()

    def test_changed_host_binary(self):
        (self.root / "host/ajn-addon.exe").write_bytes(b"another build")
        with self.assertRaisesRegex(ValueError, "file differs"): self.verify()

    def test_unrecorded_bundle_file(self):
        (self.root / "host/extra.dll").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "contents differ"): self.verify()


if __name__ == "__main__": unittest.main()
