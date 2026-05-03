# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

This repo does **not** contain the mpv player or the AnimeJaNaiConfEditor. It contains:

1. **`BuildMpvUpscale2xAnimeJaNai/`** — a C# console app whose only job is to download mpv.net, Portable VapourSynth, vs-mlrt (TensorRT/CUDA), RIFE models, yt-dlp, and various VS plugins, then layer the runtime files in `BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/` on top to produce the redistributable `mpv-upscale-2x_animejanai-v<version>/` directory.
2. **`BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/`** — the *runtime overlay*: the Python/VapourSynth scripts (`animejanai/core/`), ONNX models (`animejanai/onnx/`), per-slot `.vpy` shims (`animejanai/profiles/`), default `animejanai.conf`, mpv config (`portable_config/`), and the prebuilt `AnimeJaNaiConfEditor.exe` (its source lives in a separate repo).

A user-facing release is the C# app's output, not anything in source form.

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

Benchmark slots are hardcoded to `1010, 1011, 1012` (the Compact / UltraCompact / SuperUltraCompact templates defined in `animejanai_config.py`).

## Runtime architecture

The chain that runs when a user plays a video:

1. **mpv profile** (in `portable_config/mpv.conf`) swaps the `vf=` filter to `vapoursynth="~~/../animejanai/profiles/animejanai_<name>.vpy"`. Profiles are activated by mpv keybindings in `portable_config/input.conf` (`Shift+1/2/3` → quality/balanced/performance, `Ctrl+1`–`Ctrl+9` → user slots, `Ctrl+0` / `)` → off).
2. **`.vpy` shim** (`animejanai/profiles/animejanai_*.vpy`) is a 4-line script that calls `animejanai_core.run_animejanai_with_keybinding(video_in, container_fps, <slot_id>)`. Slot IDs map as:
   - `1`–`9` → user-editable slots in `animejanai.conf`
   - `1001`/`1002`/`1003` → built-in Quality/Balanced/Performance (defined as Python dicts in `animejanai_config.read_config`)
   - `1010`/`1011`/`1012` → built-in Compact/UltraCompact/SuperUltraCompact templates used by benchmarks
3. **`animejanai_core.run_animejanai_with_keybinding`** picks the first chain in the slot whose `min_px ≤ width*height ≤ max_px` and `min_fps ≤ fps ≤ max_fps`, then runs each model in the chain.
4. **`upscale2x` → `upscale2x_trt`** (TensorRT path) computes `engine_path = <onnx>.<crc32(trt_settings)>.engine`. If the engine file doesn't exist, it shells out to `vs-plugins/vsmlrt-cuda/trtexec` to build it on the fly — this is the "first-play pause" the README describes. **Changing `trt_engine_settings` in `animejanai.conf` invalidates all cached engines** because the CRC is part of the filename.
5. Backends are selected by `[global] backend=` in `animejanai.conf`: `TensorRT` (default), `DirectML` (`core.ort.Model` with `provider="DML"` for AMD/Intel), or `ncnn`.
6. Optional RIFE interpolation runs after upscaling via `rife_cuda.rife` (adapted from `MPV_lazy/k7sfunc.py`); model files live in `vs-plugins/models/rife/` and are downloaded by `InstallRife()` in `Program.cs`.

### Config parsing

`animejanai_config.read_config()` is the only consumer of `animejanai.conf`. It does two things:

- Hardcodes the built-in slots (`1001`–`1003`, `1010`–`1012`) as Python dicts.
- Parses the user's `.conf` with `configparser`, then *flattens* keys of the form `chain_<n>_model_<m>_<field>` into nested `{slot: {chain_n: {models: [...], ...}}}` dicts. Any new chain/model field must be read explicitly in `read_config_by_chain` / `read_config_by_chain_model` — there is no generic schema.

### Stats overlay

`Ctrl+J` in mpv invokes `portable_config/scripts/animejanaistats.lua`, which simply reads `animejanai/core/currentanimejanai.log`. That file is rewritten every run by `write_current_log()` in `animejanai_core.py` — append to `current_logger_info` / `current_logger_steps` to surface info to the user.

### `AnimeJaNaiConfEditor.exe`

Prebuilt binary checked in at `animejanai/AnimeJaNaiConfEditor.exe`. Source is in a separate repo. It edits `animejanai.conf` and is launched by mpv via `Ctrl+E`.

## Conventions

- **ONNX model filenames are load-bearing.** They appear verbatim in `animejanai_config.py` defaults and in `name=` fields of `animejanai.conf`. Renaming a model means updating both, plus any benchmark/profile references.
- TensorRT engine cache files (`*.engine`) sit next to the ONNX in `animejanai/onnx/` and are NOT shipped in the release — they're built on first play per machine.
- Default `vs.core.num_threads = 4` and `TOTAL_NUM_STREAMS = 4` are set at the top of `animejanai_core.py`; per-model `num_streams` is `TOTAL_NUM_STREAMS // len(models)` so chains with more models get fewer streams each.
