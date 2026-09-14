"""Assemble a Windows addon preview or support overlay from explicit, verified inputs.

The host bundle is built by addons/tools/package.ps1; Manager and updater inputs
are normal dotnet publish outputs. Native metadata MUST come from the producing
mpv-winbuild job. This tool cannot create native capability claims from DLLs.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
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
    for capability in ("privateSampleAbi", "privatePlayerSampleAbi", "privateOutputAbi", "nullHardwareFramesOptIn"):
        if record.get(capability) != 1:
            raise ValueError(f"Unsupported or missing native capability: {capability}")
    files = record.get("files", {})
    if not {"mpv.exe", "libmpv-2.dll"}.issubset(files) or not 2 <= len(files) <= 64:
        raise ValueError("Native producer record is incomplete")
    for name, expected in files.items():
        relative(name)
        if "/" in name or len(expected) != 64 or sha(directory / name) != expected:
            raise ValueError(f"Native artifact does not match producer record: {name}")


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


def assemble(args):
    output = args.output.resolve()
    archive = output.with_name(output.name + ".zip")
    if output.exists() or archive.exists():
        raise ValueError("Choose a new output directory and archive name")
    record = json.loads(args.native_metadata.read_text(encoding="utf-8"))
    verify_native(args.native_directory, record)
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
    if sources["mpv"] != record["sources"]["mpv"] or sources["winbuild"] != record["producer"]["commit"]:
        raise ValueError("Source archive pins differ from the native producing build")
    if sha(args.main_source / "addons/sdk/csharp/ManagementClient.cs") != sha(args.manager_source / "AnimeJaNaiConfEditor/Services/AddonHostClient.cs"):
        raise ValueError("Manager's copied management client differs from the canonical client")
    output.mkdir(parents=True)
    if args.core_archive:
        if not args.core_sha256:
            raise ValueError("--core-sha256 is required with --core-archive")
        # This exact new temporary directory belongs to this assembly attempt.
        with tempfile.TemporaryDirectory(prefix="ajn-core-", dir=output.parent) as work:
            core = extract_core(args.core_archive.resolve(), args.core_sha256, args.sevenzip, Path(work))
            copy_tree(core, output)
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
    provenance = {"schemaVersion": 1, "version": args.version, "sources": sources, "nativeProducer": record["producer"],
                  "coreSha256": args.core_sha256, "nativeMetadataSha256": sha(args.native_metadata), "tests": []}
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
        manifest.setdefault("overlay_paths", []).extend(["addon-host", "addon-development", "addon-package.json"])
        manifest.setdefault("deps", {})["addon_native"] = sha(output / "addon-host/native-capabilities.json")
        write(output / "manifest.json", manifest)
        (output / "version.txt").write_text(args.version + "\n", encoding="utf-8")
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
