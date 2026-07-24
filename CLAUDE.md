# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

This repo does **not** contain the mpv player, the `vf_animejanai` mpv filter, the `aji` inference
engine, or the AnimeJaNai Manager — those live in their own repos and are pulled in at build time.
It contains:

1. **`BuildMpvUpscale2xAnimeJaNai/`** — a C# console app (`Program.cs`, top-level statements) whose
   only job is to download the pieces and assemble them: mpv.net, a custom **libmpv fork** (carries
   the `vf_animejanai` filter), the **`aji` native inference shim** (`aji.dll` + `aji_trt.dll` /
   `aji_dml.dll`), the **TensorRT runtime + `trtexec`** (lifted from the vs-mlrt cuda archive),
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
  the `the-database/mpv-winbuild` GitHub Actions build; pinned here by `MpvForkVersion`
  (+ `MpvForkBuildDate` / `MpvForkGitHash` in the dev-archive filename).
- **`the-database/animejanai-inference`** — the `aji` engine: `aji.dll` (a thin dispatcher) forwards
  to `aji_trt.dll` / `aji_dml.dll` over the `aji.h` C ABI. Released as `aji-windows-x64.zip`; pinned
  by `AjiVersion`. **Built-in upscale presets (slots 1001–1003, 1010–1013) are hardcoded in its
  `src/aji_conf.cpp`** (`add_builtin_slots`), including the ONNX model filenames.

The filter↔engine ABI (`aji.h` `AJI_API_VERSION`) couples these two: when it changes, rebuild the
libmpv fork and the aji release and bump **both** `MpvForkVersion` and `AjiVersion`.

## Platform support (read before adding tooling)

The distribution is **Windows-only today** (it bundles mpv.net, a Windows libmpv fork, the
vsmlrt-cuda Windows binaries, and the Windows `aji` / ONNX-Runtime / DirectML DLLs). **Linux builds
are on the roadmap**, so when adding or changing build/runtime tooling, avoid baking in Windows-only
assumptions where keeping it portable is cheap:

- Prefer cross-platform languages/runtimes already in use (.NET cross-compiles to `linux-x64`;
  mpv/Lua run on Linux; the `aji` engine and the filter are portable C/C++). Do **not** introduce a
  parallel Windows-only + Linux-only implementation of the same logic (e.g. an `.exe` plus a
  duplicate `.sh`) — it will drift.
- Drive platform-specific names/paths (player executable, archive tool, exe suffix, etc.) from data
  like `manifest.json` rather than hardcoding `mpvnet.exe` / `7z.exe` / `.exe`. The updater
  (`AnimeJaNaiUpdater/`) already does this as the reference pattern.
- It's fine to ship Windows-only for now and defer the actual Linux build/packaging — just don't
  design something that *can't* extend to Linux without a rewrite.

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
`VsMlrtCudaVersion`, `OrtDmlVersion`, `DirectMLVersion`, `RifeModelsVersion`, `MpvNetVersion`,
`ManagerVersion`). Bumping a component = bump its constant.

The csproj targets **net10.0**, but `.github/workflows/deploy.yml` pins `dotnet-version: '8.x'` —
keep this in mind if the workflow fails after a TFM bump.

There is no test suite and no linter configured.

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

### Stats overlay

`Ctrl+J` invokes `portable_config/scripts/animejanaistats.lua`, which reads
`animejanai/currentanimejanai.log`. The **filter** rewrites that file on every (re)configure (the
`stats=` path in the `vf=` option, surfaced via `aji_current_log`) — it carries the active profile,
chain, models, and engine-build status.

### `AnimeJaNaiManager.exe` (the editor)

Downloaded at build time by `InstallAnimeJaNaiManager()` from a release of
`github.com/the-database/AnimeJaNaiManager`, pinned via `ManagerVersion`. The flat asset
`AnimeJaNaiManager-portable-x64.zip` (the `.exe` + native Avalonia DLLs `libSkiaSharp.dll` /
`av_libglesv2.dll` / `libHarfBuzzSharp.dll`) lands at the **install root**, next to `mpvnet.exe`. It
edits `animejanai.conf` and is launched by mpv via `Ctrl+E`. It ships **inside the overlay**, so it
updates without a full re-download. It also carries default-profile definitions, so a model rename
must be reflected there too.

To ship a new editor build: run the editor repo's manual `Release` workflow, then bump
`ManagerVersion` here. Its binaries are **not** committed (they're `.gitignore`d as a guard).

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
