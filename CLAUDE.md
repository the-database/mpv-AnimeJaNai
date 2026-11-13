# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Documentation map

This file is the orientation doc: what the repo is, how the runtime fits together, and the
conventions. The step-by-step procedures live next to it:

- **[`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)** — local dev loop: prerequisites, building
  and running the assembler, the env-var overrides that swap in a locally built component
  instead of downloading it, and the repo's gotchas.
- **[`docs/RELEASE.md`](docs/RELEASE.md)** — the cross-repo release runbook: which sibling
  workflow to dispatch in which order, which constant consumes each tag, and the full asset map.
- **[`addons/README.md`](addons/README.md)** — addon host/SDK, creator and user guides.
- **[`addons/RELEASE-INTEGRATION.md`](addons/RELEASE-INTEGRATION.md)** — optional Windows
  addon assembly, producer metadata, updater transaction, recovery and uninstall.
  `shared/AddonUpdateTransaction.cs` is compiled by the updater and host tests;
  `addons/sdk/csharp/ManagementClient.cs` is copied into Manager under a checked source pin.

Sibling components are documented in their own repos: `the-database/mpv` (`CLAUDE.md` +
`DOCS/animejanai-build-local.md` + `DOCS/animejanai-build-ci.md`), `the-database/libass`, `the-database/mpv-winbuild`,
`the-database/animejanai-inference` (`docs/BUILD-WINDOWS.md` + `docs/BUILD-LINUX.md`), and
`the-database/AnimeJaNaiManager`.

## What this repo is

This repo does **not** contain the mpv player, the `vf_animejanai` mpv filter, the `aji` inference
engine, or the AnimeJaNai Manager — those live in their own repos and are pulled in at build time.
It contains:

1. **`BuildMpvUpscale2xAnimeJaNai/`** — a C# console app (`Program.cs`, top-level statements) whose
   only job is to download the pieces and assemble them: mpv.net, a custom **libmpv fork** (carries
   the `vf_animejanai` filter; from mpv-winbuild's *dev* archive) plus the matching **standalone
   `mpv.exe`** (from the same release's *player* archive — a self-contained static build with the
   filter compiled in, shipped next to `mpvnet.exe` for users who prefer plain mpv; it auto-detects
   the same `portable_config/`), the **`aji` native inference shim** (`aji.dll` + `aji_trt.dll` /
   `aji_dml.dll`), the **TensorRT runtime + `trtexec`** (from NVIDIA's own redistributable download),
   **ONNX Runtime DirectML + `DirectML.dll`**, RIFE models, `yt-dlp`, the AnimeJaNai Manager, and
   the AnimeJaNaiUpdater — then layers the runtime files in
   `BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/` on top to produce the redistributable
   `mpv-upscale-2x_animejanai-v<version>/` directory.
2. **`BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/`** — the *runtime overlay*: committed
   ONNX models (`animejanai/onnx/`), the default `animejanai.conf`, the mpv config + Lua scripts
   (`portable_config/`), and the playback benchmark (`animejanai/benchmarks/`). **There is no
   VapourSynth, no embedded Python, and no `.vpy` shims** — upscaling and RIFE run entirely inside
   the libmpv fork's `vf_animejanai` filter (which loads `aji.dll`). `AnimeJaNaiManager.exe` and the
   inference binaries are **not** committed here — they're downloaded at build time.

A user-facing release is the C# app's output, not anything in source form.

**Two sibling repos drive the actual upscaling (bump together):**
- **`the-database/mpv`** — the `vf_animejanai` filter source. Shipped as a prebuilt **libmpv** via
  the `the-database/mpv-winbuild` GitHub Actions build (always its addon release; every Windows
  package carries addon support); pinned here by `MpvForkVersion` (+ `MpvForkBuildDate` /
  `MpvForkGitHash` in the archive filenames, and `MpvForkNativeMetadataSha256`, which pins the
  release's native metadata and through it every file taken from the archives).
- **`the-database/animejanai-inference`** — the `aji` engine: `aji.dll` (a thin dispatcher) forwards
  to `aji_trt.dll` / `aji_dml.dll` over the `aji.h` C ABI. Released as `aji-windows-x64.zip`; pinned
  by `AjiVersion`. **Built-in upscale presets (slots 1001–1003, 1010–1013) are hardcoded in its
  `src/aji_conf.cpp`** (`add_builtin_slots`), including the ONNX model filenames.

The filter↔engine ABI (`aji.h` `AJI_API_VERSION`) couples these two: when it changes, rebuild the
libmpv fork and the aji release and bump **both** `MpvForkVersion` and `AjiVersion`.

## Platform support (read before adding tooling)

**Both Windows and Linux ship.** The assembler takes `--target win-x64|linux-x64`
(`SelectTarget`, `Program.cs`), `deploy.yml` has a `deploy-linux` job, and releases since
3.5.0 carry `-linux-x64` assets plus an AppImage and a `tar.zst` alongside the Windows Setup
exe and 7z package. The per-platform names/paths are centralised in the `Platform` descriptor
at the bottom of `Program.cs`.

Windows-only pieces (no Linux equivalent installed): mpv.net, and the DirectML backend
(`aji_dml.dll`, ONNX Runtime DirectML, `DirectML.dll` — `HasDirectML = false` on Linux).
Linux gets its mpv bundle from a `the-database/mpv` release; both platforms fetch the
TensorRT runtime straight from NVIDIA (the Windows zip, the Linux `.deb`s).

When adding or changing build/runtime tooling, keep it portable:

- Prefer cross-platform languages/runtimes already in use (.NET cross-compiles to `linux-x64`;
  mpv/Lua run on Linux; the `aji` engine and the filter are portable C/C++). Do **not** introduce a
  parallel Windows-only + Linux-only implementation of the same logic (e.g. an `.exe` plus a
  duplicate `.sh`) — it will drift.
- Drive platform-specific names/paths (player executable, archive tool, exe suffix, etc.) from data
  like `manifest.json` rather than hardcoding `mpvnet.exe` / `7z.exe` / `.exe`. The updater
  (`AnimeJaNaiUpdater/`) already does this as the reference pattern.
- A new component must land on both legs. `PortConfigsForTarget()` rewrites the config paths
  for Linux (`aji.dll`→`libaji.so`, `trtexec.exe`→`trtexec`, `'Segoe UI'`→`'sans-serif'`); add
  to it rather than forking the config files.

## Shared runtime packs (store mode)

The TensorRT runtime and the per-GPU builder packs live in the per-user pack store that the other
libaji apps share (`%LOCALAPPDATA%\the-database\aji`, `~/.local/share/the-database/aji`; the
design is `plans/shared-runtime-packs.md` in the-database/animejan.ai). They come from the shared
runtime release (`RuntimeTag`, animejanai-inference `runtime-<n>`, pinned by its index SHA-256 in
`manifest.json` `runtime`), installed by `aji-runtime(.exe)` at the install root.

- **Updater:** store mode is on when the manifest pins `runtime`, `aji-runtime` is beside it, and it
  isn't running in the AppImage (whose AppRun manages its own ref). Its `trt-*` packs come from the store; RIFE
  stays this release's component pack. Every change is one `aji-runtime ensure`, which writes
  `animejanai/inference/aji-runtime.json`. An older install's TensorRT files are adopted by SHA-256,
  then deleted. Pack names and `--components --json` are unchanged.
- **Which store, whose ref (aji-runtime 0.3.0+):** aji-runtime uses the store `aji-runtime.conf`
  beside it names (`store = <folder>`, relative to the file), else the per-user one. A copy the setup
  installer made (`unins???.exe` beside the updater, or the `installed-by-setup.txt` the installer
  writes before it first runs the updater) keeps the plain ref id `mpv-animejanai` and the per-user
  store. Any other Windows copy (a portable zip) passes `--per-copy --app-path <launcher>` (ref
  `mpv-animejanai-<8 hex>`), and a fresh one writes `store = animejanai\runtime`: TensorRT inside its
  own folder, with relative paths in `aji-runtime.json`, so the folder can be moved or copied to
  another PC. TensorRT counts as installed only when the ref `show` reads names this copy's launcher
  and `aji-runtime.json` is there; nothing ever unrefs or shrinks another copy's ref. Linux installs
  keep the plain id. `aji-runtime.conf` and `animejanai/runtime` are in `user_preserve` and never
  shipped (the assembler refuses a tree that has them).
- **Components > Storage:** `AnimeJaNaiUpdater --storage --json` reports the choice (`inside`,
  `shared`, `custom`), and `--storage inside|shared|<folder>` switches it (`aji-runtime use`, which
  takes the packs along by hard link or copy). See `docs/DEVELOPMENT.md`.
- **Readers of `aji-runtime.json`:** libaji loads TensorRT from the store pack by full path and runs
  that pack's `trtexec` (ignoring the `trtexec=` in `mpv-animejanai.conf`).
  `animejanai_backend.lua` (`trt_runtime_installed`) and the Manager (`RuntimePacks.TrtInstalled`)
  check for TensorRT there. Any new "is TensorRT installed" check must read it too: without it,
  TensorRT looks missing and the DirectML fallback kicks in.
- **The AppImage** carries no TensorRT. Before mpv starts, AppRun has the image's `aji-runtime` install
  TensorRT and this PC's builder packs (ref `mpv-animejanai-appimage`, `app_path` = the AppImage file,
  once per version, stamped in `$DATA/.aji-runtime.stamp`). It writes `$DATA/animejanai/inference/
  aji-runtime.json`, which `AJI_RUNTIME_JSON` names for libaji.
- **Releases build with `--store`:** no TensorRT is downloaded or packed, and RIFE is the only component
  pack. Local builds without it keep TensorRT in the tree. `DirectML_LICENSE.txt` stays in the core.
- **Not in the store:** RIFE (its engines would land in the read-only pack: the video ABI needs an
  engine cache dir first), and DirectML (in the core).
- **Uninstall:** Inno `[UninstallRun]` and `installer/linux/uninstall.sh` run `aji-runtime unref`
  (in whatever store `aji-runtime.conf` names; a store inside the install goes with the folder).
- **A missing store folder:** AnimeJaNai Manager > Components > Storage moves the per-user store to
  another drive (`aji-runtime move`; the default path becomes a link, so `aji-runtime.json` stays
  valid), or points this copy at a folder the user picks. While the folder is missing (an unplugged
  drive; `location --json` says `available: false`), TensorRT only looks uninstalled:
  `animejanai_backend.lua` asks `aji-runtime location --json`, keeps `backend=TensorRT` and says so
  on the OSD instead of switching to DirectML, and the updater's store changes stop with the same
  message. Engine caches stay next to the ONNX in the install folder.

## Building and releasing

```powershell
# Build the assembler
dotnet publish BuildMpvUpscale2xAnimeJaNai/BuildMpvUpscale2xAnimeJaNai.csproj -c Release -o publish

# Assemble a full distribution (downloads several GB, takes minutes)
./publish/BuildMpvUpscale2xAnimeJaNai.exe <release_version>
# Output: ./publish/mpv-upscale-2x_animejanai-v<release_version>/
```

The `<release_version>` arg is required and is used only as the install-folder suffix (no semver
parsing). If the target folder exists it is wiped first (`Main()` in `Program.cs`). `Main()` also
writes `version.txt` + `manifest.json` into the install root — the **AnimeJaNaiUpdater** reads these
to decide overlay-vs-full updates (`overlay_paths` = files an in-place update overwrites;
`user_preserve` = what it keeps).

The download/version pins are constants at the top of `Program.cs` (`AjiVersion`, `MpvForkVersion`,
`TrtVersion`, `TrtCudaVersion`, `OrtDmlVersion`, `DirectMLVersion`, `RifeModelsVersion`, `MpvNetVersion`,
`ManagerVersion`). Bumping a component = bump its constant. Releases of libaji, the two mpv fork
builds and the Manager open that PR themselves (`bump-pins.yml`, `scripts/bump_pins.py`; see
`docs/RELEASE.md`).

All three csproj target **net10.0**, and `.github/workflows/deploy.yml` pins
`dotnet-version: '10.x'` in both legs — keep them in sync after a TFM bump. There is no
`global.json`.

There is no test suite and no linter configured, except `dotnet run --project tests/UpdaterGpu`
(the updater's TensorRT GPU rules, `AnimeJaNaiUpdater/GpuSupport.cs`).

See `docs/DEVELOPMENT.md` for the full assembler CLI (`--target`, `--packs`, `--packs-only`)
and the local-component override env vars, and `docs/RELEASE.md` for the release runbook.

## Benchmarks

`animejanai/benchmarks/benchmark.ps1` is a **playback** benchmark: it drives the real mpv pipeline
offscreen (`mpvnet.com --vo=null --untimed --load-scripts=no --cache=no`) across the bundled source
clips (`480x360.mp4` … `1920x1080.mp4`) for the built-in benchmark templates — **slot 1010
(Balanced)** and **slot 1011 (Performance)** — and reports the upscale fps (can the GPU beat real
time?). It warms up to build/load the TensorRT engine, then samples mpv's estimated-frame-number
over a short active-playback window. It runs from inside an *assembled* distribution (needs
`mpvnet.com` + the bundled clips), not the source tree.

```powershell
# From inside an assembled mpv-upscale-2x_animejanai-v<version>\animejanai\benchmarks\:
./animejanai_benchmark_all.bat        # the Manager's "Run Benchmarks" button calls this
# or directly: ./benchmark.ps1  (-Quick / -OnlyRes 1280x720 for partial runs)
```

The benchmark slots `1010`/`1011` are built-in templates defined in `aji_conf.cpp`
(`add_builtin_slots`); `1012`/`1013` are RIFE-order benchmark templates.

## Runtime architecture

There is **no VapourSynth/Python layer** — the whole pipeline is the libmpv fork's `vf_animejanai`
filter calling the `aji` engine. The chain when a user plays a video:

1. **mpv profile.** `portable_config/mpv-animejanai.conf` (managed; users override in `mpv.conf`,
   which `include`s it) defines `[upscale-on]` with
   `vf=@aji:animejanai:lib=…/animejanai/inference/aji.dll:conf=…/animejanai/animejanai.conf:model-dir=…/animejanai/onnx:rife-model-dir=…/animejanai/rife:trtexec=…/animejanai/inference/trtexec.exe:stats=…/animejanai/currentanimejanai.log:slot=1002`,
   `[upscale-off]` with `vf=""`, and `[default] profile=upscale-on`. Also sets `vo=gpu-next`,
   `gpu-api=vulkan,auto`, `hwdec=nvdec` (the TensorRT default).
2. **The filter** (`vf_animejanai`, in the libmpv fork) consumes the decoder's **GPU frames
   directly** — CUDA frames for TensorRT, D3D11 frames for DirectML — and loads `aji.dll`, the
   dispatcher that forwards to the backend lib (`aji_trt.dll` / `aji_dml.dll`) over the `aji.h` C ABI.
3. **Slot switching.** `portable_config/input-animejanai.conf` binds keys to
   `script-message aji-slot N`, which `scripts/animejanai_slot.lua` turns into `vf-command aji slot N`
   (it adds a refresh-seek only while paused, to avoid audio dropouts). Slot IDs:
   - `0` → off (bypass)
   - `1`–`9` → user-editable slots in `animejanai.conf` (`Ctrl+1`…`Ctrl+9`)
   - `1001`/`1002`/`1003` → built-in Quality/Balanced/Performance (`Shift+1/2/3`), in `aji_conf.cpp`
   - `1010`–`1013` → benchmark / RIFE-order templates (`aji_conf.cpp`)
4. **Chain selection.** The engine picks the first chain in the slot whose resolution
   (`min_px`…`max_px`) and fps range match the stream, then runs each model. The built-in presets use
   the **HD** model for 720p–1080p and the **SD** model below 720p.
5. **TensorRT path.** Engines are built **on first play** (per model + resolution) by shelling out to
   `trtexec`, on the player loop — the "first-play pause". `scripts/animejanai_engine_monitor.lua`
   pauses playback, narrates the build on the OSD, and resumes when ready (it watches for the
   "Building TensorRT engine" line the filter writes to the stats log). Engine cache files
   (`*.engine`) are written next to the ONNX in `animejanai/onnx/`; changing TRT settings invalidates
   them (the settings CRC is part of the engine filename).
6. **Backend** is selected by `[global] backend=` in `animejanai.conf`: `TensorRT` (default; CUDA
   frames, `hwdec=nvdec`) or `DirectML` (D3D11 frames, `hwdec=d3d11va`; AMD/Intel, or NVIDIA when
   forced). `scripts/animejanai_backend.lua` aligns mpv's `hwdec` + render API to the configured
   backend on startup. (`ncnn` is retired and treated as DirectML by the shim.)
   On Windows it also rewrites a `TensorRT` conf to `backend=DirectML` + `backend_auto_fallback=yes`
   when TensorRT can't run (no `nvcuda.dll` in System32, only NVIDIA GPUs too old for TensorRT, or
   no `nvinfer_11.dll`), the same keys the Manager writes, so the Manager switches back once
   TensorRT is usable. **TensorRT needs compute capability 7.5+** (GeForce RTX 20 / GTX 16 series
   and newer; the shipped TensorRT 11 on CUDA 13 and aji's kernels start at sm75, and `trt-ptx`
   can't JIT backwards): the script asks `nvidia-smi --query-gpu=uuid,compute_cap,name` (only while
   the backend is TensorRT) and treats a PC with only older NVIDIA GPUs like one without an NVIDIA
   GPU. On Linux, where there is nothing to fall back to, it takes the filter out with an OSD
   message. Next to a newer GPU, TensorRT is pointed at the newer one (`vulkan-device`). The same
   threshold is `TRT_MIN_COMPUTE` there, `GpuSupport.TrtMinComputeCapability` in the updater and
   `trt_min_compute` in `installer/linux/AppRun`; keep them together.
   **GPU choice:** the Manager writes `[global] gpu_tensorrt=<Vulkan device UUID>` and
   `gpu_directml=pci:<bus>:<device>.<function>` (the adapter's PCI slot, which needs the fork's
   `--d3d11-adapter=pci:...`; an adapter name when Windows reports no slot) (absent = Auto); the script sets `vulkan-device` (CUDA
   decoding, and so the filter, follows the Vulkan device) or `d3d11-adapter` (DirectML follows the
   D3D11 device) only while the user hasn't set it. DirectML Auto sets
   `d3d11-adapter-preference=high-performance` on player builds that have the option. The UUIDs
   come from `AnimeJaNaiUpdater --components --json` (`gpus`, via NVML).
7. **RIFE** interpolation runs after upscaling by default, or before it when a chain sets
   `rife_before_upscale`; RIFE model files live in `animejanai/rife/` (downloaded by `InstallRife()`).
8. **Subtitle rendering** has two managed presets in `portable_config/mpv-animejanai.conf`: the
   stable defaults inside `[animejanai]` (every GPU/threaded subtitle feature of the fork off —
   note `sub-ass-render-threads=1` and `sub-present-guard-ms=0` are the *off* values; `0` and `-1`
   mean auto/armed) and the opt-in `[subs-gpu]` profile (GPU glyph raster + blur, render-ahead
   worker, OSD render cap, persistent stats overlay). The Manager's "GPU Subtitle Rendering"
   checkbox writes `[global] sub_render_mode=gpu`, and `scripts/animejanai_backend.lua` applies
   the profile at startup. Keep the option list in the profile, not in the Lua or the Manager.
   The script applies each option **only while it still holds the managed default** (read from
   `profile-list`, falling back to `option-info/<key>/default-value`) — scripts run after the
   config is parsed, so a blanket `apply-profile` would override the user's own `mpv.conf` lines
   and break the "your settings win" contract that `mpv.conf` and `mpv-animejanai.conf` promise.
   Any future script-applied preset must follow the same rule.

### Config (`animejanai.conf`)

The `aji` engine parses `animejanai.conf` (the old Python `animejanai_config.read_config` is
**gone** — do not look for `animejanai/core/*.py`; it no longer exists):
- **Built-in slots are code, not config.** `1001`–`1003` (Quality/Balanced/Performance) and
  `1010`–`1013` (benchmark/RIFE-order) are hardcoded in `animejanai-inference/src/aji_conf.cpp`
  (`add_builtin_slots`), each a resolution-keyed chain referencing model filenames via the
  `HD_BAL` / `HD_PERF` / `SD` string constants. They ship as defaults and are not user-editable.
- **User slots `1`–`9`** are parsed from `animejanai.conf` (`[slot_N]`,
  `chain_<n>_model_<m>_<field>` keys, `[global]` settings like `backend=`). The AnimeJaNai Manager
  writes this file.
- **Player-only `[global]` keys.** `default_slot` and `sub_render_mode` are written by the Manager
  and read by the Lua scripts, not by the engine (`aji_conf.cpp` looks up only the keys it knows, so
  unknown ones are ignored). This is the pattern for any future "Manager configures mpv" setting:
  a `[global]` key here, the actual mpv options in a managed profile.
- **Chain feature keys (aji v0.17.0+).** `chain_<n>_temporal_fix_model` (+
  `_temporal_fix_scene_threshold`) makes the engine run a vs_temporalfix model after the chain's
  models; the Manager writes the newest bundled `temporalfix_s<k>_v*` for the chosen strength.
  `chain_<n>_upscale=no` skips the chain's models (they stay in the conf) so only temporal fix /
  RIFE run. `chain_<n>_temporal_fix_before_upscale=yes` (aji v0.17.1+; the Manager's default)
  runs temporal fix at the source resolution before the models instead, 2-3x cheaper. The models (vs_temporalfix, Apache-2.0) ship in `animejanai/onnx/` with their
  licence.

### Stats overlay

`Ctrl+J` invokes `portable_config/scripts/animejanaistats.lua`, which reads
`animejanai/currentanimejanai.log`. The **filter** rewrites that file on every (re)configure (the
`stats=` path in the `vf=` option, surfaced via `aji_current_log`) — it carries the active profile,
chain, models, and engine-build status.

The filter doesn't rewrite that file when it fails at runtime: mpv's filter chain logs `Disabling
filter aji because it has failed.` and plays on without it until the filter is rebuilt (the next
file, upscaling turned off and on, or a profile key: `animejanai_slot.lua` watches for the same
line and rebuilds `@aji` with the chosen slot instead of sending a `vf-command`; re-applying the
same `vf` or a seek doesn't re-enable it). `animejanaistats.lua` watches
the log for that line (`mp.enable_messages`), shows the filter's last error on the OSD, and
`Ctrl+J` shows it instead of the stale stats.

### `AnimeJaNaiManager.exe` (the editor)

Downloaded at build time by `InstallAnimeJaNaiManager()` from a release of
`github.com/the-database/AnimeJaNaiManager`, pinned via `ManagerVersion`. The flat asset
`AnimeJaNaiManager-portable-x64.zip` (the `.exe` + native Avalonia DLLs `libSkiaSharp.dll` /
`av_libglesv2.dll` / `libHarfBuzzSharp.dll`) lands at the **install root**, next to `mpvnet.exe`. It
edits `animejanai.conf` and is launched by mpv via `Ctrl+E`. It ships **inside the overlay**, so it
updates without a full re-download. It also carries default-profile definitions, so a model rename
must be reflected there too.

To ship a new editor build: run the editor repo's manual `Release` workflow, then merge the
`ManagerVersion` bump PR it opens here. Its binaries are **not** committed (they're `.gitignore`d as a guard).

## Conventions

- **ONNX model filenames are load-bearing.** They appear verbatim in
  `animejanai-inference/src/aji_conf.cpp` (the `HD_BAL` / `HD_PERF` / `SD` constants for the built-in
  presets), in the AnimeJaNai Manager's default profiles, and in `name=` fields of user
  `animejanai.conf` slots. Renaming a model means updating all of those. (There is **no**
  `animejanai_config.py` anymore — that Python layer was removed; ignore stale `__pycache__/*.pyc`.)
- **ONNX models are committed in `animejanai/onnx/`** (currently the V3.1 HD Balanced/Performance
  models, their `…V3.1Sharp1…` variants, and the SD beta). Bumping the model lineup = replace the
  files there and update the `aji_conf.cpp` constants (and the editor defaults).
- **DirectML opset ceiling.** The bundled ONNX Runtime DirectML EP (`OrtDmlVersion`) only registers
  `Conv`/`PReLU` kernels through **ONNX opset 21** — a model exported at opset ≥22 silently falls
  back to the CPU EP on the DirectML backend (~2000× slower per frame, looks like a hang on
  sub-720p/SD content). Keep ONNX models at **opset ≤21**. Dynamic input shapes are fine; the opset
  is what matters. Verify with `AJI_ORT_VERBOSE=1` (ORT logs node placement to stderr — no
  `Conv`/`PReLU` under `CPUExecutionProvider` = good).
- TensorRT engine cache files (`*.engine`) sit next to the ONNX in `animejanai/onnx/` and are NOT
  shipped in the release — they're built on first play per machine.
