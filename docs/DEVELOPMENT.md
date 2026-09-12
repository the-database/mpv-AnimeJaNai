# Local development

Orientation and runtime architecture live in [`../CLAUDE.md`](../CLAUDE.md); the release
runbook lives in [`RELEASE.md`](RELEASE.md). This file is the local loop.

Repo: `the-database/mpv-AnimeJaNai`. Note the **directory name and the repo name differ** —
the working copy is usually `mpv-upscale-2x_animejanai` while `origin` is `mpv-AnimeJaNai`.
Code inside the repo consistently uses the old name (e.g. `AnimeJaNaiUpdater/Program.cs`:
`const string Repo = "the-database/mpv-upscale-2x_animejanai"`), and the GitHub redirect makes
both work.

## Prerequisites

| Need | Why (evidence) |
|---|---|
| **.NET 10 SDK** | all three csproj target `net10.0`; `deploy.yml` pins `dotnet-version: '10.x'` in both legs. No `global.json`. |
| **Node 20** | `.github/workflows/benchmarks.yml` pins `node-version: "20"` for the catalog build |
| **7-Zip** + **Inno Setup 6** | Windows release only; CI installs them with `choco install 7zip -y` / `choco install innosetup -y` |
| **`wrangler`** | only to deploy `benchmark-proxy/` (devDependency `wrangler ^3.80.0`) |
| **An NVIDIA GPU + driver** | only to *run* the assembled package on the TensorRT backend, not to build |

Nothing else: **there is no test suite, no linter, and no formatter config** in this repo — no
`.editorconfig`, `Directory.Build.props`, `dotnet-tools.json`, eslint, or pre-commit. Do not go
looking for a `dotnet test` target.

## The assembler

`BuildMpvUpscale2xAnimeJaNai/` is a C# console app (top-level statements in `Program.cs`) that
downloads every component by pinned version and lays out the redistributable tree.

```powershell
dotnet publish BuildMpvUpscale2xAnimeJaNai/BuildMpvUpscale2xAnimeJaNai.csproj -c Release -o publish
./publish/BuildMpvUpscale2xAnimeJaNai.exe 3.6.0
# -> ./publish/mpv-upscale-2x_animejanai-v3.6.0/
```

On Linux the same binary is invoked without the extension:
`./publish/BuildMpvUpscale2xAnimeJaNai 3.6.0 --target linux-x64`.

### CLI

```
BuildMpvUpscale2xAnimeJaNai <version> [--target win-x64|linux-x64] [--packs] [--packs-only [dir]]
```

- **`<version>`** — required, must be the **first** positional arg. `Program.cs` throws
  `"Version is required (first positional arg)."` when
  `args.Length < 1 || args[0].StartsWith("--")`. It is used only as the install-folder suffix;
  there is no semver parsing.
- **`--target`** — `win-x64` or `linux-x64`; defaults to the host RID. Anything else throws
  `Unsupported --target '<rid>' (use win-x64 or linux-x64)`.
- **`--packs`** — after assembling, emit the component packs (`EmitComponentPacks()`) and then
  **slim the core tree** (`SlimInstallTree(packFiles)`), removing from the install tree what now
  ships as a downloadable pack. This is what CI uses.
- **`--packs-only [dir]`** — emit packs from an **already-built** tree and exit, skipping the
  whole download/assemble path. If `dir` is given and exists, it overrides the version-derived
  install directory. **Use this when iterating on packaging** — it is the only cheap mode.

> **The install directory is deleted first.** `Main()` opens with
> `if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, true);`
> A full run downloads several GB and takes minutes.

### What `Main()` does, in order

`InstallSevenZip` → `InstallInferenceRuntime` → `InstallAji` → `InstallOrtDml` *(Windows only)*
→ `InstallRife` → *(Windows:* `InstallMpvnet`, `InstallCustomLibmpv`, `InstallCustomMpvExe`*;
Linux:* `InstallLinuxMpv`*)* → `InstallYtDlp` → `InstallAnimeJaNaiCore` →
`PortConfigsForTarget` → `GenerateInputConf` → `InstallAnimeJaNaiManager` →
`WriteThirdPartyNotices` → `WriteLinuxLauncher` → `WriteVersionAndManifest` →
*(if `--packs`)* `EmitComponentPacks` + `SlimInstallTree`.

That order is where to intervene: to test a change to one component, find its `Install*`
function and use the matching override below rather than editing the function.

## Testing a locally built component instead of downloading it

These env vars are the actual dev hooks. All are read in
`BuildMpvUpscale2xAnimeJaNai/Program.cs` unless noted.

| Env var | Effect |
|---|---|
| `AJI_LOCAL_BUILD` | **Linux only** (guarded on non-Windows). Copies `libaji.so` / `libaji_trt.so` + tools from a local CMake build dir instead of downloading the release. |
| `AJI_LOCAL_ZIP` | Use a local built aji archive instead of downloading. On Windows this is how you test a fresh `aji-windows-x64.zip` — point it at the zip that `package-aji-release.ps1` emitted (`<aji-win>\dist\<tag>\aji-windows-x64.zip`; see that repo's `docs/BUILD-WINDOWS.md`). |
| `MPV_LINUX_LOCAL` | Use a local meson build dir for `mpv` + `libmpv.so*` (e.g. `~/src/mpv/build`) instead of the `the-database/mpv` release asset. |
| `MPV_LINUX_EXTRA_LIBS` | `:`-separated dirs; globs `libplacebo.so*` into `mpv/`. Pair with `MPV_LINUX_LOCAL` when your local mpv links a libplacebo the bundle does not carry. |
| `MANAGER_LOCAL` | A local directory (copied) **or** a zip / `tar.zst` (extracted) for the AnimeJaNai Manager, instead of its release asset. |
| `TRT_LINUX_ROOT` | Linux TensorRT source root; default `/usr`. Reads `$root/lib/x86_64-linux-gnu` and `$root/bin/trtexec`. |
| `CUDA_LINUX_LIB` | Source dir for `libcudart.so*`; default `/usr/local/cuda-13.3/lib64`. CI overrides it to `/usr/local/cuda/lib64`. |
| `ANIMEJANAI_PACKS_DIR` | **Updater**, not the assembler (`AnimeJaNaiUpdater/Program.cs:857`). Point at a local dir holding `packs.json` + the pack archives so `--components` / `--install` / `--recommend` work against **unpublished** packs. Essential while a release is still a draft: the public releases API cannot see draft assets. |
| `ANIMEJANAI_ROOT` | **Benchmark tool** fallback for the install root when `--install-root` is not given. |
| `AJI_ORT_VERBOSE=1` | Runtime, not build: makes ONNX Runtime log node placement to stderr. Use it to confirm no `Conv`/`PReLU` landed on `CPUExecutionProvider` on the DirectML backend. |

## The other three projects

All are published into the assembled tree by CI, not by the assembler.

### `AnimeJaNaiUpdater/`

Ships at the install root. Shares source with the assembler via
`<Compile Include="..\BuildMpvUpscale2xAnimeJaNai\Downloader.cs" Link="Downloader.cs" />`, so a
change to `Downloader.cs` affects both.

```
AnimeJaNaiUpdater.exe [--check|--apply|--components|--install <pack>|--remove <pack>|--auto|--recommend]
```

`--check` prints `UPDATE_AVAILABLE <ver>` or `UP_TO_DATE <ver>` (consumed by
`scripts/animejanai_update.lua`). `--components` also accepts `--json`. `--recommend` is what
the Inno Setup wizard shells out to for the GPU-aware component picker, parsing `NVIDIA=`,
`GPU=`, `TRT_PACKS=`, `RIFE=` lines back out of stdout.

```powershell
dotnet publish AnimeJaNaiUpdater/AnimeJaNaiUpdater.csproj -c Release -o updater-publish
```

### `AnimeJaNaiBenchmark/`

The cross-platform replacement for `benchmark.ps1`; drives mpv over JSON IPC and samples
estimated-frame-number.

```
AnimeJaNaiBenchmark [--install-root DIR] [--slots 1010,1011] [--fps-floor N] [--out results.json]
```

Defaults from source: `--fps-floor` 8, `--slots` `1010,1011`; the install root falls back to
`ANIMEJANAI_ROOT` then `AppContext.BaseDirectory`. Player path is `mpvnet.com` on Windows,
`mpv/mpv` on Linux. The csproj is deliberately RID-agnostic — RID and `--self-contained` come
from the publish command.

> **Gotcha:** `AnimeJaNaiBenchmark/bin` and `obj` are **tracked in git**, unlike
> `BuildMpvUpscale2xAnimeJaNai` and `AnimeJaNaiUpdater`, which are both `.gitignore`d. A
> `dotnet build` of this project shows build output as modified tracked files. Whether that is
> intentional is not recorded anywhere in the repo.

### `benchmark-proxy/`

Cloudflare Worker that turns a benchmark submission into a PR. `npm install`, then
`npx wrangler dev` locally or `npx wrangler deploy`. Secrets are set out-of-band:
`npx wrangler secret put GITHUB_TOKEN` (a fine-grained PAT scoped to this repo with Contents +
Pull requests write) and the optional `SUBMIT_TOKEN`, which must match the Manager's
`X-Submit-Token`. Route and vars live in `wrangler.toml`.

## Editing the runtime overlay

`BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/` is copied verbatim into the install
tree by `InstallAnimeJaNaiCore()`. The rules that are easy to get wrong:

- **`portable_config/mpv-animejanai.conf` is managed** — its header says "DO NOT EDIT. Replaced
  on every update." This is where AnimeJaNai's mpv options belong, including the `[subs-gpu]`
  profile. Putting those options in Lua or in the Manager instead is the anti-pattern.
- **`portable_config/input.conf` is generated**, not committed. `GenerateInputConf()` writes a
  header, then the whole of `input-animejanai.conf` between
  `#@ANIMEJANAI-MANAGED-BEGIN` / `#@ANIMEJANAI-MANAGED-END`, then a user section. The updater's
  regenerator matches those markers by prefix (`AnimeJaNaiUpdater/Program.cs:325-326`), so **the
  marker text must stay byte-identical** in both places. Edit `input-animejanai.conf`, never a
  generated `input.conf`.
- **Never-overwritten user files** (`user_preserve` in `WriteVersionAndManifest()`):
  `animejanai/animejanai.conf`, `animejanai/currentanimejanai.log`, `portable_config/mpv.conf`,
  `portable_config/input.conf`, `portable_config/saved-props.json`,
  `portable_config/settings.xml`, `portable_config/screenshots`.
  `installer/animejanai.iss` installs the same set with `Flags: onlyifdoesntexist` — **keep the
  two lists in sync**; the `.iss` says so in a comment.
- **`overlay_paths`** decides what a lightweight in-place update overwrites: `version.txt`,
  `manifest.json`, the updater exe, `animejanai/onnx`, `animejanai/benchmarks`, plus the
  platform's aji libs and tools. A new file only reaches existing installs via a full update
  unless it is added here.

## Benchmark catalog and site

```bash
node scripts/build-benchmarks.mjs     # data/benchmarks/submissions/*.json -> site/benchmarks.json
```

Pure Node built-ins, no dependencies. Validation: `schema === 1`, backend in
`{TensorRT, DirectML, ncnn}`, resolution matching `/^\d{2,5}x\d{2,5}$/`, numeric fps with `-1`
as the "too slow" sentinel, and at least one real measurement.

`benchmarks.yml` runs this on pushes to `main` touching `data/benchmarks/submissions/**`,
`site/**`, or the script, then deploys `site/` to GitHub Pages (`benchmarks.animejan.ai`).
To reject a submission, close its PR; to unpublish a result, delete its JSON — the next catalog
build drops it.

> The committed `site/benchmarks.json` is a placeholder (`"count": 0`, empty `submissions`,
> `generated_at` 2026-06-13). It is regenerated in CI and never committed back, so do not read
> the checked-in copy as the live catalog.

## Known rough edges

- **`build/Dockerfile.ubuntu2204` references a `build-stack.sh`** that exists in no repo on this
  machine (a `find` across every checkout finds nothing). How the from-source
  wayland/FFmpeg/libplacebo stack inside that image is built is therefore not documented
  anywhere here. The mpv fork's `ci/build-linux-portable.sh` does the equivalent work for the
  mpv bundle.
- **`README.md` documents `portable_config/mpv-user.conf` and `input-user.conf`.** Neither file
  exists in the overlay, and `manifest.json`'s `user_preserve` names `portable_config/mpv.conf`
  and `portable_config/input.conf` instead. Treat the README's filenames as unverified.
- **`.gitignore` still lists `animejanai/core/currentanimejanai.log` and `*.pyc`** — leftovers
  from the removed Python layer. `animejanai/core/` does not exist; the log now lives at
  `animejanai/currentanimejanai.log`.
- **No Windows publish step for `AnimeJaNaiBenchmark`.** `deploy.yml` publishes it only in the
  Linux leg; the Windows tree still relies on `benchmark.ps1`.
