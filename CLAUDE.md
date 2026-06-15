# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

This repo does **not** contain the mpv player or the AnimeJaNai Manager. It contains:

1. **`BuildMpvUpscale2xAnimeJaNai/`** — a C# console app whose only job is to download mpv.net, Portable VapourSynth, vs-mlrt (TensorRT/CUDA), RIFE models, yt-dlp, AnimeJaNai Manager, and various VS plugins, then layer the runtime files in `BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/` on top to produce the redistributable `mpv-upscale-2x_animejanai-v<version>/` directory.
2. **`BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/`** — the *runtime overlay*: the Python/VapourSynth scripts (`animejanai/core/`), ONNX models (`animejanai/onnx/`), per-slot `.vpy` shims (`animejanai/profiles/`), default `animejanai.conf`, and mpv config (`portable_config/`). `AnimeJaNaiManager.exe` is **not** in the overlay — it's downloaded at build time from its own repo's release (see below).

A user-facing release is the C# app's output, not anything in source form.

## Platform support (read before adding tooling)

The distribution is **Windows-only today** (it bundles mpv.net, a Windows libmpv fork, the
vsmlrt-cuda Windows binaries, and an embedded Windows Python). **Linux builds are on the roadmap**,
so when adding or changing build/runtime tooling, avoid baking in Windows-only assumptions where
keeping it portable is cheap:

- Prefer cross-platform languages/runtimes already in use (.NET cross-compiles to `linux-x64`;
  VapourSynth/mpv/Lua run on Linux). Do **not** introduce a parallel Windows-only + Linux-only
  implementation of the same logic (e.g. an `.exe` plus a duplicate `.sh`) — it will drift.
- Drive platform-specific names/paths (player executable, archive tool, exe suffix, etc.) from data
  like `manifest.json` rather than hardcoding `mpvnet.exe` / `7z.exe` / `.exe`. The updater
  (`AnimeJaNaiUpdater/`) already does this as the reference pattern.
- It's fine to ship Windows-only for now and defer the actual Linux build/packaging — just don't
  design something that *can't* extend to Linux without a rewrite.

## Building and releasing

```powershell
# Build the installer/assembler
dotnet publish BuildMpvUpscale2xAnimeJaNai/BuildMpvUpscale2xAnimeJaNai.csproj -c Release -o publish

# Assemble a full distribution (downloads ~several GB, takes minutes)
./publish/BuildMpvUpscale2xAnimeJaNai.exe <release_version>
# Output: ./publish/mpv-upscale-2x_animejanai-v<release_version>/
```

The `<release_version>` arg is required and is used only as the install-folder suffix (no semver parsing). If the target folder exists it is wiped first (`Main()` in `Program.cs`).

The csproj targets **net10.0**, but `.github/workflows/deploy.yml` pins `dotnet-version: '8.x'` — keep this in mind if the workflow fails after a TFM bump.

There is no test suite and no linter configured.

## Benchmarks

`animejanai/benchmarks/animejanai_benchmark_all.py` runs from inside an *assembled* distribution (it shells out to `..\vspipe.exe` and reads `animejanai.conf` via `animejanai_config.read_config`). It cannot be run from the source tree — it needs the full Python/VapourSynth environment that `BuildMpvUpscale2xAnimeJaNai.exe` produces.

```powershell
# From inside an assembled mpv-upscale-2x_animejanai-v<version>\animejanai\ directory:
..\python.exe ./benchmarks/animejanai_benchmark_all.py
# Or via the wrapper:
./benchmarks/animejanai_benchmark_all.bat
```

Benchmark slots are hardcoded to `1010, 1011` (the V3.1 Balanced / Performance templates defined in `animejanai_config.py`, matched by the `slots` list in `animejanai_benchmark_all.py`).

## Runtime architecture

The chain that runs when a user plays a video:

1. **mpv profile** (in `portable_config/mpv.conf`) swaps the `vf=` filter to `vapoursynth="~~/../animejanai/profiles/animejanai_<name>.vpy"`. Profiles are activated by mpv keybindings in `portable_config/input.conf` (`Shift+1/2/3` → quality/balanced/performance, `Ctrl+1`–`Ctrl+9` → user slots, `Ctrl+0` / `)` → off).
2. **`.vpy` shim** (`animejanai/profiles/animejanai_*.vpy`) is a 4-line script that calls `animejanai_core.run_animejanai_with_keybinding(video_in, container_fps, <slot_id>)`. Slot IDs map as:
   - `1`–`9` → user-editable slots in `animejanai.conf`
   - `1001`/`1002`/`1003` → built-in Quality/Balanced/Performance (defined as Python dicts in `animejanai_config.read_config`)
   - `1010`/`1011` → built-in V3.1 Balanced/Performance templates used by benchmarks
3. **`animejanai_core.run_animejanai_with_keybinding`** picks the first chain in the slot whose `min_px ≤ width*height ≤ max_px` and `min_fps ≤ fps ≤ max_fps`, then runs each model in the chain.
4. **`upscale2x` → `upscale2x_trt`** (TensorRT path) computes `engine_path = <onnx>.<crc32(trt_settings)>.engine`. If the engine file doesn't exist, it shells out to `vs-plugins/vsmlrt-cuda/trtexec` to build it on the fly — this is the "first-play pause" the README describes. **Changing `trt_engine_settings` in `animejanai.conf` invalidates all cached engines** because the CRC is part of the filename.
5. Backends are selected by `[global] backend=` in `animejanai.conf`: `TensorRT` (default), `DirectML` (`core.ort.Model` with `provider="DML"` for AMD/Intel), or `ncnn`.
6. Optional RIFE interpolation runs after upscaling via `rife_cuda.rife` (adapted from `MPV_lazy/k7sfunc.py`); model files live in `vs-plugins/models/rife/` and are downloaded by `InstallRife()` in `Program.cs`.

### Config parsing

`animejanai_config.read_config()` is the only consumer of `animejanai.conf`. It does two things:

- Hardcodes the built-in slots (`1001`–`1003`, `1010`–`1011`) as Python dicts.
- Parses the user's `.conf` with `configparser`, then *flattens* keys of the form `chain_<n>_model_<m>_<field>` into nested `{slot: {chain_n: {models: [...], ...}}}` dicts. Any new chain/model field must be read explicitly in `read_config_by_chain` / `read_config_by_chain_model` — there is no generic schema.
- Applies `[global]` migrations/overrides via `_migrate_global` (schema `config_version`, default-fingerprinting) and `_apply_default_preset`. The latter is per-profile: `[global] quality_preset` / `balanced_preset` / `performance_preset` (`standard | sharp`, absent ⇒ standard) independently switch the HD models in built-in slots `1001`/`1002`/`1003` from `…_HD_V3.1_…` to `…_HD_V3.1Sharp1_…`. The AnimeJaNai Manager writes these keys (per-profile Standard/Sharp toggle); keep the two in sync.

### Stats overlay

`Ctrl+J` in mpv invokes `portable_config/scripts/animejanaistats.lua`, which simply reads `animejanai/core/currentanimejanai.log`. That file is rewritten every run by `write_current_log()` in `animejanai_core.py` — append to `current_logger_info` / `current_logger_steps` to surface info to the user.

### `AnimeJaNaiManager.exe`

Downloaded at build time by `InstallAnimeJaNaiManager()` in `Program.cs` from a GitHub release of its source repo (`github.com/the-database/AnimeJaNaiManager`), pinned via the `ManagerVersion` constant. The release asset `AnimeJaNaiManager-portable-x64.zip` (the `.exe` + native Avalonia DLLs `libSkiaSharp.dll`/`av_libglesv2.dll`/`libHarfBuzzSharp.dll`) is extracted into `animejanai/`, so it ends up next to the overlay's own `animejanai.conf` and `onnx/`. It edits `animejanai.conf` and is launched by mpv via `Ctrl+E`.

To ship a new editor build: run the editor repo's manual `Release` workflow (Actions → Run workflow, enter the version) to cut a release, then bump `ManagerVersion` here to match. The binaries are **not** committed to this repo (they're `.gitignore`d as a guard).

## Conventions

- **ONNX model filenames are load-bearing.** They appear verbatim in `animejanai_config.py` defaults and in `name=` fields of `animejanai.conf`. Renaming a model means updating both, plus any benchmark/profile references.
- **ONNX models are committed in `animejanai/onnx/`** (currently the V3.1 HD models + the SD beta). Bumping the model lineup = replace the files in that directory and update the model `name`s in `animejanai_config.py` defaults (and the editor's `DEFAULT_PROFILES_CONF`).
- TensorRT engine cache files (`*.engine`) sit next to the ONNX in `animejanai/onnx/` and are NOT shipped in the release — they're built on first play per machine.
- Default `vs.core.num_threads = 4` and `TOTAL_NUM_STREAMS = 4` are set at the top of `animejanai_core.py`; per-model `num_streams` is `TOTAL_NUM_STREAMS // len(models)` so chains with more models get fewer streams each.
