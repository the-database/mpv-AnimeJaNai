"""Assemble a Windows addon preview or support overlay from explicit, verified inputs.

The host bundle is built by addons/tools/package.ps1; Manager and updater inputs
are normal dotnet publish outputs. Native metadata MUST come from the producing
mpv-winbuild job. This tool cannot create native capability claims from DLLs.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import subprocess
import tempfile
import zipfile

RUNTIME_SHA256 = "37045d920f7abbd202f0ea8c52ae9bbc0e49bcdca47e70f37649d1dc0ae1cff5"


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def relative(name):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name or ".." in PurePosixPath(name).parts or PureWindowsPath(name).drive or name.startswith("/"):
        raise ValueError(f"Invalid package-relative path: {name}")
    return name


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def copy_tree(source, target):
    for item in source.iterdir():
        if item.is_symlink() or item.is_junction():
            raise ValueError(f"Input trees must not contain links: {item}")
        destination = target / item.name
        if item.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            copy_tree(item, destination)
        else:
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, destination)


def verify_native(directory, record):
    if record.get("schemaVersion") != 1 or record.get("platform") != "win-x64" or record.get("cRuntime") != "ucrt" or record.get("ffmpegLinkage") not in ("shared", "static"):
        raise ValueError("Unsupported producer native metadata")
    for capability in ("privateSampleAbi", "privatePlayerSampleAbi", "privateOutputAbi", "nullHardwareFramesOptIn",
                       "privateProbeAbi", "privateMuxAbi", "privateSubtitlesAbi"):
        if record.get(capability) != 1:
            raise ValueError(f"Unsupported or missing native capability: {capability}")
    files = record.get("files", {})
    if not {"mpv.exe", "libmpv-2.dll"}.issubset(files) or not 2 <= len(files) <= 256:
        raise ValueError("Native producer record is incomplete")
    if len({name.lower() for name in files}) != len(files):
        raise ValueError("Native producer record contains duplicate Windows filenames")
    has_muxer = any(re.fullmatch(r"avformat-\d+\.dll", name) for name in files)
    if has_muxer != (record["ffmpegLinkage"] == "shared"):
        raise ValueError("Native binary layout disagrees with the producer linkage")
    for name, expected in files.items():
        relative(name)
        if not re.fullmatch(r"[A-Za-z0-9._+-]{1,128}", name) or not (name.endswith(".dll") or name in ("mpv.exe", "mpv.com")) or not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected) or sha(directory / name) != expected:
            raise ValueError(f"Native artifact does not match producer record: {name}")


def verify_host_bundle(directory, main_source, git):
    path = directory / "host-build.json"
    if not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise ValueError("Use a host bundle with the producer record emitted by addons/tools/package.ps1")
    record = json.loads(path.read_text(encoding="utf-8"))
    if record.get("schemaVersion") != 1 or record.get("platform") != "win-x64" or not re.fullmatch(r"[0-9a-f]{40}", record.get("sourceCommit", "")):
        raise ValueError("Unsupported host build record")
    for name in ("addons", "shared", "LICENSE"):
        actual = subprocess.check_output([git, "-C", str(main_source), "rev-parse", "HEAD:" + name], text=True).strip()
        if record.get("sourceObjects", {}).get(name) != actual:
            raise ValueError(f"Host bundle and committed source differ: {name}")
    files = record.get("files", {})
    required = {"host/ajn-addon.exe", "host/ajn-addon-launcher.exe", "host/ajn-addon.deps.json", "runtime/wasmtime.exe"}
    if not required.issubset(files):
        raise ValueError("Host producer inventory is incomplete")
    actual_files = {p.relative_to(directory).as_posix() for p in directory.rglob("*") if p.is_file()}
    if actual_files != set(files) | {"host-build.json"}:
        raise ValueError("Host bundle contents differ from the producing build inventory")
    for name, expected in files.items():
        relative(name)
        file = directory / name
        if not file.resolve().is_relative_to(directory.resolve()) or sha(file) != expected:
            raise ValueError(f"Host bundle file differs from the producing build: {name}")
    return record


def extract_core(archive, expected, sevenzip, destination):
    if sha(archive) != expected:
        raise ValueError("Core archive checksum mismatch")
    listing = subprocess.check_output([sevenzip, "l", "-slt", str(archive)], text=True, encoding="utf-8")
    if "----------" not in listing:
        raise ValueError("Unrecognized archive listing")
    for line in listing.split("----------", 1)[1].splitlines():
        if line.startswith("Path = "):
            relative(line[7:].replace("\\", "/"))
        if line.startswith(("Symbolic Link = ", "Hard Link = ")):
            raise ValueError("Core archive cannot contain links")
    subprocess.run([sevenzip, "x", "-y", str(archive), "-o" + str(destination)], check=True, stdout=subprocess.DEVNULL)
    if (destination / "manifest.json").is_file():
        return destination
    candidates = [p for p in destination.iterdir() if p.is_dir() and (p / "manifest.json").is_file()]
    if len(candidates) != 1:
        raise ValueError("Expected one versioned core package root")
    return candidates[0]


def retire_core_native(directory, incoming_files):
    # Historical test cores record their shared native dependency closure.
    # Remove only unchanged, recorded files from this newly extracted copy,
    # so switching to a static build cannot leave old FFmpeg DLLs alongside it.
    origins = directory / "build-info/runtime-origins.json"
    hashes = directory / "build-info/sha256.json"
    if not origins.is_file() or not hashes.is_file():
        return
    old_files = json.loads(origins.read_text())
    old_hashes = json.loads(hashes.read_text())
    hashes_by_name = {name.lower(): (name, digest) for name, digest in old_hashes.items()}
    if len(hashes_by_name) != len(old_hashes):
        raise ValueError("Ambiguous Windows filenames in the core native inventory")
    incoming_names = {name.lower() for name in incoming_files}
    obsolete = []
    for name in old_files:
        if not re.fullmatch(r"[A-Za-z0-9._+-]{1,128}", name) or not (name.endswith(".dll") or name in ("mpv.exe", "mpv.com")):
            raise ValueError("Unrecognized core native inventory")
        recorded_name, expected = hashes_by_name.get(name.lower(), (name, None))
        path = directory / recorded_name
        if not path.is_file() or sha(path) != expected:
            raise ValueError(f"Core native inventory does not match its binary: {name}")
        if name.lower() not in incoming_names:
            obsolete.append(path)
    for path in obsolete:
        path.unlink()


def assemble(args):
    output = args.output.resolve()
    archive = output.with_name(output.name + ".zip")
    if output.exists() or archive.exists():
        raise ValueError("Choose a new output directory and archive name")
    if args.native_metadata.stat().st_size > 1024 * 1024:
        raise ValueError("Native producer metadata is too large")
    record = json.loads(args.native_metadata.read_text(encoding="utf-8"))
    verify_native(args.native_directory, record)
    host_record = verify_host_bundle(args.host_bundle, args.main_source, args.git)
    if sha(args.host_bundle / "runtime/wasmtime.exe") != RUNTIME_SHA256:
        raise ValueError("Host bundle does not contain the pinned Wasmtime runtime")
    for name in ("ajn-addon.exe", "ajn-addon-launcher.exe", "ajn-addon.deps.json"):
        if not (args.host_bundle / "host" / name).is_file():
            raise ValueError(f"Incomplete host bundle: {name}")
    sources = {}
    for name in ("main", "manager", "mpv", "winbuild"):
        path = getattr(args, name + "_source")
        def git(*command):
            return subprocess.check_output([args.git, "-C", str(path), *command], text=True).strip()
        if git("status", "--porcelain", "--untracked-files=no"):
            raise ValueError(f"Commit tracked {name} source changes before packaging")
        sources[name] = git("rev-parse", "HEAD")
    if sources["mpv"] != record["sources"]["mpv"]:
        raise ValueError("Native source pin differs from the producing build")
    # Later build-script tests/docs do not change an already-produced binary.
    # Archive the actual producing revision, not whichever revision is checked out.
    sources["winbuild"] = subprocess.check_output([args.git, "-C", str(args.winbuild_source), "rev-parse", "--verify",
        record["producer"]["commit"] + "^{commit}"], text=True).strip()
    if (args.main_source / "addons/sdk/csharp/ManagementClient.cs").read_text() != (args.manager_source / "AnimeJaNaiConfEditor/Services/AddonHostClient.cs").read_text():
        raise ValueError("Manager's copied management client differs from the canonical client")
    output.mkdir(parents=True)
    if args.core_archive:
        if not args.core_sha256:
            raise ValueError("--core-sha256 is required with --core-archive")
        # This exact new temporary directory belongs to this assembly attempt.
        with tempfile.TemporaryDirectory(prefix="ajn-core-", dir=output.parent) as work:
            core = extract_core(args.core_archive.resolve(), args.core_sha256, args.sevenzip, Path(work))
            copy_tree(core, output)
        retire_core_native(output, record["files"])
        info = output / "build-info"
        if info.is_dir():
            historical = list(info.iterdir())
            (info / "core").mkdir()
            for item in historical:
                destination = info / "core" / item.name
                if not item.resolve().is_relative_to(output) or not destination.resolve().is_relative_to(output):
                    raise ValueError("Historical evidence path left the new package")
                item.rename(destination)
    for name in record["files"]:
        shutil.copy2(args.native_directory / name, output / name)
    for path in args.manager_directory.iterdir():
        if path.is_file() and path.suffix.lower() in (".exe", ".dll"):
            shutil.copy2(path, output / path.name)
    if not (output / "AnimeJaNaiManager.exe").is_file():
        raise ValueError("Manager publish output is incomplete")
    shutil.copy2(args.updater_executable, output / "AnimeJaNaiUpdater.exe")
    for src, dst in (("host", "addon-host"), ("runtime", "addon-host/runtime"), ("licenses", "addon-host/licenses"),
                     ("source", "addon-development/source"), ("shared", "addon-development/shared"), ("sdk", "addon-development/sdk"),
                     ("examples", "addon-development/examples"), ("tools", "addon-development/tools")):
        copy_tree(args.host_bundle / src, output / dst)
    for path in args.host_bundle.iterdir():
        if path.is_file() and path.suffix in (".ajnaddon", ".md", ".html"):
            shutil.copy2(path, output / "addon-development" / path.name)
    # The standalone bundle's quick start uses host/ and runtime/ beside it.
    # An integrated package opens with the Manager instructions and its own paths.
    for suffix in (".md", ".html"):
        shutil.copy2(args.host_bundle / ("USER-GUIDE" + suffix), output / "addon-development" / ("START-HERE" + suffix))
    write(output / "addon-host/native-capabilities.json", record)
    write(output / "addon-host/native-media.json", {"schemaVersion": 1, "sessionApi": {"major": 1, "minor": 1}, "mpvRevision": sources["mpv"]})
    script = args.main_source / "BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/portable_config/scripts/animejanai_addons.lua"
    destination = output / "portable_config/scripts/animejanai_addons.lua"
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(script, destination)
    for name in sources:
        subprocess.run([args.git, "-C", str(getattr(args, name + "_source")), "archive", "--format=zip", "-o",
                        str(output / "addon-development" / (name + "-source.zip")), sources[name]], check=True)
    for name, source in (("Manager", args.manager_source / "LICENSE"), ("mpv", args.mpv_source / "LICENSE.GPL"), ("AJN", args.main_source / "LICENSE")):
        target = output / "licenses" / (name + "-LICENSE")
        target.parent.mkdir(exist_ok=True)
        shutil.copy2(source, target)
    if (args.native_directory / "licenses").is_dir():
        copy_tree(args.native_directory / "licenses", output / "licenses/native")
    if (args.native_directory / "build-info").is_dir():
        copy_tree(args.native_directory / "build-info", output / "build-info/native")
    provenance = {"schemaVersion": 1, "version": args.version, "sources": sources, "nativeProducer": record["producer"],
                  "hostProducer": {k: host_record[k] for k in ("sourceCommit", "sourceObjects", "dotnetSdk")},
                  "coreSha256": args.core_sha256, "nativeMetadataSha256": sha(args.native_metadata), "tests": []}
    write(output / "build-info/host-build.json", host_record)
    for evidence in args.evidence:
        target = output / "build-info/addon-tests" / evidence.name
        if target.exists():
            raise ValueError("Test evidence filenames must be distinct")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(evidence, target)
        provenance["tests"].append({"file": target.relative_to(output).as_posix(), "sha256": sha(evidence)})
    write(output / "build-info/addon-preview.json", provenance)
    inventory_paths = set(record["files"])
    inventory_paths.update(p.name for p in args.manager_directory.iterdir() if p.is_file() and p.suffix.lower() in (".exe", ".dll"))
    inventory_paths.update(p.relative_to(output).as_posix() for p in (output / "addon-host").rglob("*") if p.is_file())
    inventory_paths.add("portable_config/scripts/animejanai_addons.lua")
    write(output / "addon-package.json", {"schemaVersion": 1, "platform": "win-x64", "sources": sources,
                                         "files": {p: sha(output / p) for p in sorted(inventory_paths)}})
    if args.core_archive:
        manifest = json.loads((output / "manifest.json").read_text())
        if manifest.get("platform", "win-x64") != "win-x64":
            raise ValueError("Linux core packages cannot include this Windows addon runtime")
        manifest["package_version"] = args.version
        manifest.setdefault("user_preserve", []).append("animejanai/addons")
        manifest.setdefault("overlay_paths", []).extend(["addon-host", "addon-development", "addon-package.json", "build-info"])
        manifest.setdefault("deps", {})["addon_native"] = sha(output / "addon-host/native-capabilities.json")
        manifest["deps"]["mpvfork"] = "source:" + sources["mpv"]
        write(output / "manifest.json", manifest)
        (output / "version.txt").write_text(args.version + "\n", encoding="utf-8")
        (output / "TEST-BUILD.txt").write_text(
            f"AnimeJaNai {args.version} - Windows addon integration preview\n\n"
            "Extract the complete folder to a new location and open mpvnet.exe.\n"
            "Open AnimeJaNai Manager's Addons page to install a local .ajnaddon package.\n"
            "Start with addon-development/USER-GUIDE.html. Addon authors should read\n"
            "addon-development/CREATOR-GUIDE.html and RELEASE-INTEGRATION.html.\n\n"
            "This is a preview, not an official stable release or a public API freeze.\n"
            "See build-info/addon-preview.json for exact sources and test evidence;\n"
            "build-info/core contains historical evidence for the reused core.\n",
            encoding="utf-8")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for path in sorted(output.rglob("*")):
            if path.is_file():
                zipped.write(path, path.relative_to(output).as_posix())
    print(json.dumps({"directory": str(output), "archive": str(archive), "sha256": sha(archive)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("native-directory", "native-metadata", "host-bundle", "manager-directory", "updater-executable", "output",
                 "main-source", "manager-source", "mpv-source", "winbuild-source"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--git", default="git")
    parser.add_argument("--sevenzip", default="7z")
    parser.add_argument("--core-archive", type=Path)
    parser.add_argument("--core-sha256")
    parser.add_argument("--evidence", type=Path, action="append", default=[])
    assemble(parser.parse_args())
