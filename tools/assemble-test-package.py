"""Assemble a portable AJN test package from explicit, already-built inputs.

Reuses a pristine 3.6.0 core tree and its unchanged inference/model components.
Replaces both player entry points, libmpv, Manager and the updater. Output must
not exist; an installed/user-edited folder should never be used as the base.
"""
from pathlib import Path
import argparse
import hashlib
import json
import shutil

parser = argparse.ArgumentParser(description=__doc__)
for name in ('base', 'native', 'manager', 'updater', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
parser.add_argument('--version', default='3.6.1-test.1')
parser.add_argument('--component-version', default='3.6.0')
parser.add_argument('--mpv-commit', required=True)
parser.add_argument('--mpv-repository', default='the-database/mpv',
                    help='Owner/repository recorded for the native player source')
parser.add_argument('--main-commit', required=True)
parser.add_argument('--manager-commit', required=True)
parser.add_argument('--native-run-url', required=True)
args = parser.parse_args()
paths = {name: getattr(args, name).resolve()
         for name in ('base', 'native', 'manager', 'updater', 'output')}
destination = paths['output']
if destination.exists():
    raise FileExistsError(f'Use a new output directory: {destination}')
for name, source in paths.items():
    if name != 'output' and (destination.is_relative_to(source) or source.is_relative_to(destination)):
        raise ValueError(f'Output must be separate from {name}')
required = {
    'base': ['manifest.json', 'version.txt', 'mpvnet.exe', '7za.exe'],
    'native': ['mpv.exe', 'mpv.com', 'libmpv-2.dll', 'build-info/mpv-commit.txt'],
    'manager': ['AnimeJaNaiManager.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll'],
    'updater': ['AnimeJaNaiUpdater.exe'],
}
for name, files in required.items():
    for relative in files:
        if not (paths[name] / relative).is_file():
            raise FileNotFoundError(paths[name] / relative)
actual_mpv = (paths['native'] / 'build-info/mpv-commit.txt').read_text().strip()
if actual_mpv != args.mpv_commit:
    raise ValueError('Native artifact is not built from the selected mpv fix commit')
manifest = json.loads((paths['base'] / 'manifest.json').read_text(encoding='utf-8-sig'))
if manifest['package_version'] != args.component_version:
    raise ValueError('Base tree and selected component release differ')

shutil.copytree(paths['base'], destination)
shutil.copytree(paths['native'], destination, dirs_exist_ok=True)
for source in paths['manager'].iterdir():
    if source.is_file() and source.suffix.lower() in ('.exe', '.dll'):
        shutil.copy2(source, destination / source.name)
shutil.copy2(paths['updater'] / 'AnimeJaNaiUpdater.exe', destination / 'AnimeJaNaiUpdater.exe')
shaders = Path(__file__).resolve().parents[1] / 'BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/portable_config/shaders'
for name in ('noise_static_luma.hook', 'noise_static_chroma.hook'):
    shutil.copy2(shaders / name, destination / 'portable_config/shaders' / name)

manifest['package_version'] = args.version
manifest['component_package_version'] = args.component_version
manifest['build_type'] = 'test'
manifest['deps']['mpvfork'] = args.mpv_repository + '@' + args.mpv_commit
(destination / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
(destination / 'version.txt').write_text(args.version + '\n', encoding='utf-8')

# The slim base includes DirectML. Leave TensorRT as an explicit Manager choice
# so first launch does not require downloading the optional NVIDIA runtime.
configuration = destination / 'animejanai/animejanai.conf'
text = configuration.read_text(encoding='utf-8-sig')
if text.count('backend=TensorRT') != 1:
    raise ValueError('Unexpected pristine base configuration')
configuration.write_text(text.replace('backend=TensorRT', 'backend=DirectML', 1), encoding='utf-8')

info = destination / 'build-info'
info.mkdir(exist_ok=True)
record = {
    'version': args.version, 'component_package_version': args.component_version,
    'main_commit': args.main_commit, 'mpv_commit': args.mpv_commit,
    'mpv_repository': args.mpv_repository,
    'manager_commit': args.manager_commit, 'native_run': args.native_run_url,
    'base_package': manifest['component_package_version'],
    'default_backend': 'DirectML',
    'sha256': {name: hashlib.sha256((destination / name).read_bytes()).hexdigest()
               for name in ('mpv.exe', 'mpv.com', 'libmpv-2.dll', 'AnimeJaNaiManager.exe',
                            'AnimeJaNaiUpdater.exe', 'manifest.json', 'version.txt',
                            'portable_config/shaders/noise_static_luma.hook',
                            'portable_config/shaders/noise_static_chroma.hook')},
}
(info / 'test-package.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
(destination / 'TEST-BUILD.txt').write_text(f'''AnimeJaNai {args.version} — Windows x64 portable test

Extract the whole folder to a new location and run mpvnet.exe (or mpv.exe).
This package has its own portable configuration. Keep it separate from your
working installation while testing. It is not the official stable release.

DirectML is selected initially because its runtime is included. Ctrl+E opens
Manager; TensorRT can be selected there after installing the optional components.
Those components are explicitly pinned to {args.component_version}, whose inference
runtime and models are unchanged in this test package.

Included fixes: installed-release component selection, failure-safe keybinding
migration, RIFE ensemble profile export/import, subtitle buffer-transfer
capability handling, failed texture preallocation handling and deadline units.
Both noise shaders also initialize unused channels so D3D11 compiles them.

The player and libass are rebuilt with MSYS2 UCRT64. Their dependency versions
and source commits are recorded in build-info. This build differs from Jeff's
cross-compiled distribution and needs ordinary playback/compatibility testing.

The broader review findings are not all fixed. In particular, upgrading an old
configuration by copying files can still require migration before its first
player launch. Start testing with this package's fresh portable configuration.

Source and build provenance: build-info/test-package.json
Native build: {args.native_run_url}
''', encoding='utf-8')
print(destination)
