# Release runbook

A release of this repo is an assembly of five other repos' outputs. Nothing here builds mpv,
libass, or the inference engine — the assembler downloads them all by **pinned version
constant**, so a release is: get each upstream artifact published, bump its constant, then
dispatch `Deploy`.

Local dev loop: [`DEVELOPMENT.md`](DEVELOPMENT.md). Orientation: [`../CLAUDE.md`](../CLAUDE.md).

## The dependency graph

```
the-database/libass  ──┐
the-database/mpv  ─────┼──► mpv-winbuild "MPV"          → mpv-x86_64-<date>-git-<sha>.7z
                       │                                  mpv-dev-x86_64-<date>-git-<sha>.7z
                       └──► mpv "Build Linux"            → mpv-linux-x64-<tag>.tar.zst

animejanai-inference ──────► "Build Linux (aji)"         → aji-linux-x64.tar.zst
                     └─────► LOCAL Windows build          → aji-windows-x64.zip

AnimeJaNaiManager  ────────► "Release"                   → AnimeJaNaiManager-portable-x64.zip
                                                           AnimeJaNaiManager-portable-linux-x64.tar.zst
                                    │
                                    ▼
              mpv-AnimeJaNai "Deploy"  → draft release with the full asset set
```

## Version pins

Every pin is a `const string` near the top of `BuildMpvUpscale2xAnimeJaNai/Program.cs`.
Values below are the current ones; read the file for truth.

| Constant | Current | Source of the value |
|---|---|---|
| `AjiVersion` | `v0.8.0` | `the-database/animejanai-inference` release tag |
| `RifeModelsVersion` | `models-rife-fp16-1` | an animejanai-inference release tag (fp16 conversions) |
| `MpvForkVersion` | `2026-07-23-7fc08d90c7` | `the-database/mpv-winbuild` release tag |
| `MpvForkBuildDate` | `20260723` | the date **inside the archive filename** |
| `MpvForkGitHash` | `7fc08d90c7` | the short hash **inside the archive filename** |
| `MpvForkLinuxVersion` | `2026-07-23-7fc08d9` | `the-database/mpv` release tag (Linux bundle) |
| `ManagerVersion` | `0.5.0` | `the-database/AnimeJaNaiManager` release tag |
| `VsMlrtCudaVersion` | `v16.1.test1` | `AmusementClub/vs-mlrt` release tag (TensorRT runtime + `trtexec`) |
| `OrtDmlVersion` | `1.24.4` | `Microsoft.ML.OnnxRuntime.DirectML` on NuGet |
| `DirectMLVersion` | `1.15.4` | `Microsoft.AI.DirectML` on NuGet |
| `MpvNetVersion` | `v7.1.2.0` | `mpvnet-player/mpv.net` release tag |
| `SevenZipVersion` | `2602` | 7-zip.org "extra" / linux-x64 console |

**Three constants describe one mpv-winbuild build.** `MpvForkVersion` is the release tag, while
`MpvForkBuildDate` and `MpvForkGitHash` are the date and short hash embedded in the asset
*filenames*. All three must be bumped together or the download 404s.

**The filter↔engine ABI couples mpv and aji.** `aji.h`'s `AJI_API_VERSION` is shared by
`vf_animejanai` (in the mpv fork) and the engine. When it changes, rebuild **both** and bump
**both** `MpvForkVersion`/`MpvForkLinuxVersion` and `AjiVersion`. `Program.cs` notes the filter
lives on the mpv fork's `master` (aji ABI v8) and that the old standalone `vf-animejanai`
branch is stale (ABI v4) and must not be used.

`VsMlrtCudaVersion` and `AjiVersion` must agree on the TensorRT major.minor — the comment in
`Program.cs` states `v16.1.x == TensorRT 11.1 / CUDA 13.3`, and flags that `v16.1.test1` is a
vs-mlrt **pre-release**, so recheck for a stable v16 tag before cutting a package release.

## Order of operations

### 1. libass (only if the subtitle work changed)

Nothing to dispatch — `the-database/libass` publishes no artifact and its CI is unmodified
upstream. Just land the work on its `master`; the two mpv builds clone it by ref.

### 2. mpv, Windows — `the-database/mpv-winbuild`

```bash
gh workflow run MPV -R the-database/mpv-winbuild \
  -f mpv_ref=master -f libass_ref=master \
  -f build_target=64bit -f compiler=clang -f release=true
```

`workflow_dispatch` only. It resolves both fork refs to SHAs and redirects the toolchain's
`packages/{mpv,libass}.cmake` at them (adding `-Dthreads=enabled` for libass). Tag format is
`$(date +%Y-%m-%d)-$(head -c 10 <<< $sha)`. See that repo's `CLAUDE.md`.

→ bump `MpvForkVersion`, `MpvForkBuildDate`, `MpvForkGitHash`.

### 3. mpv, Linux — `the-database/mpv`

```bash
gh workflow run "Build Linux (portable mpv + vf_animejanai)" -R the-database/mpv \
  --ref master -f release_tag=2026-07-23-7fc08d9
```

`release_tag` is **required** on dispatch and becomes both the release tag and part of the
asset name (`mpv-linux-x64-<tag>.tar.zst`).

> **Dispatch from `master`, not `linux-support`.** The workflow's `push` trigger targets
> `linux-support`, but that branch is hundreds of commits behind `master`, so a push-triggered
> run would build stale code. See the mpv fork's `DOCS/animejanai-build-ci.md`.

→ bump `MpvForkLinuxVersion`.

### 4. aji engine — `the-database/animejanai-inference`

**Linux** is CI:

```bash
gh workflow run "Build Linux (aji)" -R the-database/animejanai-inference \
  --ref main -f release_tag=v0.8.0
```

**Windows has no CI.** `aji-windows-x64.zip` is built and uploaded by hand from a local
working area (written `<aji-win>` here — see that repo's `docs/BUILD-WINDOWS.md` for the full
dependency setup):

```
<aji-win>\build-aji-release.bat
pwsh <aji-win>\package-aji-release.ps1 -Tag v0.8.0
gh release upload v0.8.0 <aji-win>\dist\v0.8.0\aji-windows-x64.zip -R the-database/animejanai-inference
```

Full recipe and dependency setup: that repo's `docs/BUILD-WINDOWS.md`. Both assets must land on
the **same tag**, because the assembler derives both URLs from `AjiVersion`.

→ bump `AjiVersion`.

### 5. Manager — `the-database/AnimeJaNaiManager`

```bash
gh workflow run Release -R the-database/AnimeJaNaiManager -f release_version=0.5.0
```

The Windows leg is gated on `github.event_name == 'workflow_dispatch'`; a push to
`linux-support` runs only the Linux leg.

> **The Manager's release must be published, not draft.** Its `release.yml` sets `draft: false`
> with the comment: "Published (not draft) so the mpv build can fetch the asset over the public
> `releases/download/<tag>/` URL — draft assets 404 for unauthenticated requests." The
> assembler downloads it unauthenticated, so a draft breaks `InstallAnimeJaNaiManager`.

→ bump `ManagerVersion`.

### 6. Assemble — this repo

```bash
gh workflow run Deploy -R the-database/mpv-AnimeJaNai -f release_version=3.6.0
# Linux artifacts only (e.g. adding Linux to an already-published release):
gh workflow run Deploy -R the-database/mpv-AnimeJaNai -f release_version=3.6.0 -f linux_only=true
```

Commit the constant bumps first — `Deploy` builds from the checked-out ref.

## What `Deploy` does

Two jobs, one release tag.

### `deploy` (Windows, `windows-latest`)

Skipped when `linux_only == 'true'`.

1. `dotnet publish BuildMpvUpscale2xAnimeJaNai/... -c Release -o publish` (dotnet `10.x`)
2. `./publish/BuildMpvUpscale2xAnimeJaNai.exe <ver> --packs`
3. publish `AnimeJaNaiUpdater` and copy `AnimeJaNaiUpdater.exe` into the tree
4. `choco install 7zip -y`
5. `choco install innosetup -y`, then
   `ISCC.exe /DAppVersion=<ver> /DSourceDir=<resolved tree> /O<cwd> installer/animejanai.iss`
6. `7z a -t7z -mx=9 -v1900m mpv-upscale-2x_animejanai-full-package-<ver>.7z ./publish/...`
   — note **`-v1900m` volumes**, hence the `.7z.001` asset
7. overlay archive: read `overlay_paths` out of the tree's `manifest.json`, then
   `7z a -t7z -mx=9 <out> @paths` from inside the tree so paths are install-root-relative
8. `ncipollo/release-action@v1` with `draft: true`

### `deploy-linux` (`ubuntu-latest`, in the build container)

Runs in `ghcr.io/the-database/animejanai-linux-build:ubuntu2204` (GHCR login via
`github.actor` + `GITHUB_TOKEN`) with `shell: bash` forced, because the container's default
shell is dash. Env: `TRT_LINUX_ROOT=/usr`, `CUDA_LINUX_LIB=/usr/local/cuda/lib64`.

1. publish + run the assembler with `--target linux-x64 --packs`
2. publish the updater self-contained single-file `linux-x64`
3. publish `AnimeJaNaiBenchmark` **into** `animejanai/benchmarks/` in the tree, then `chmod +x`
4. `installer/linux/package-tarball.sh <tree> <updater> <ver> .` → `...-linux-x64.tar.zst`
5. AppImage (`continue-on-error: true`): copy the tree, extract **only**
   `component-trt-runtime-linux-x64.7z` into it with `7zz x -snl` (the `-snl` restores the
   deduplicated symlinks), then `installer/linux/build-appimage.sh`. RIFE and the per-SM kernel
   packs are fetched on demand by `AppRun`, keeping the AppImage small and SM-agnostic.
6. overlay + RID-suffixed manifest (`continue-on-error: true`), using `jq` on `overlay_paths`
7. `ncipollo/release-action@v1` with `omitDraftDuringUpdate`, `omitNameDuringUpdate`,
   `omitBodyDuringUpdate` — the Linux leg only **adds** its assets to the release the Windows
   leg owns, and must never flip an already-published release back to draft or rewrite its
   notes.

The Manager is **not** built in either leg: the assembler downloads it
(`InstallAnimeJaNaiManager`) from its own repo's release on both platforms.

## Asset map

The release comes out as a **draft** — review, then publish.

| Asset | Produced by |
|---|---|
| `mpv-AnimeJaNai-Setup-<ver>.exe` | Inno Setup, `installer/animejanai.iss` |
| `mpv-upscale-2x_animejanai-full-package-<ver>.7z.001` | `7z ... -v1900m` (slim core tree) |
| `mpv-upscale-2x_animejanai-overlay-<ver>.7z` | `overlay_paths` subset, install-root-relative |
| `manifest.json` | copied out of the tree so the updater can compare deps before downloading |
| `component-{rife,trt-ptx,trt-runtime,trt-sm75,trt-sm80,trt-sm86,trt-sm89,trt-sm90,trt-sm100,trt-sm120}.7z` | `EmitComponentPacks()` |
| `packs.json` | the component index the updater/Manager reads |
| `mpv-upscale-2x_animejanai-v<ver>-linux-x64.tar.zst` | `package-tarball.sh` |
| `mpv-AnimeJaNai-x86_64-v<ver>.AppImage` | `build-appimage.sh` |
| `...-overlay-<ver>-linux-x64.7z`, `manifest-linux-x64.json`, `component-*-linux-x64.7z`, `packs-linux-x64.json` | the Linux leg |

Component packs are emitted to `publish/packs-v<ver>/` as
`component-<name><PackSuffix>.7z`, where `PackSuffix` is `""` on Windows and `"-linux-x64"` on
Linux (`Platform.PackSuffix`). The per-SM packs are discovered, not hardcoded: every
`*builder_resource_*` file in the inference dir is matched against
`builder_resource_([a-z0-9]+)[._]` and becomes `trt-sm<NNN>`. Packs are built with
`7z a -spf2 -snl -mx=3`.

The installer is per-user by design: `PrivilegesRequired=lowest`,
`DefaultDirName={localappdata}\Programs\mpv-AnimeJaNai`,
`OutputBaseFilename=mpv-AnimeJaNai-Setup-{#AppVersion}`. It hard-errors without
`/DSourceDir=` (`#error Define SourceDir (the built slim tree) with /DSourceDir=...`).

## The Linux build image

`build/Dockerfile.ubuntu2204` → `ghcr.io/the-database/animejanai-linux-build:ubuntu2204`, built
by `.github/workflows/build-image.yml` (`workflow_dispatch`, or a push touching the Dockerfile
or the workflow).

**Three repos depend on this image**: this repo's `deploy-linux`, `the-database/mpv`'s
`build-linux.yml`, and `the-database/animejanai-inference`'s `build-linux.yml`. Rebuilding it
changes all three, so re-run it only when the Dockerfile changes.

It exists to hold a low glibc floor (Ubuntu 22.04 / glibc 2.35) while pinning CUDA 13.3 +
TensorRT 11.1.0.106 exactly (`libnvinfer11=11.1.0.106-1+cuda13.3` and its siblings), plus
meson/cmake via pip, the .NET 10 SDK, `7zz`, and luajit.

## After publishing

The updater polls
`https://api.github.com/repos/the-database/mpv-upscale-2x_animejanai/releases/latest`, so
nothing reaches users until the draft is published. While it is still a draft, test the
component flow with `ANIMEJANAI_PACKS_DIR` pointed at a local `packs-v<ver>/` directory —
draft assets 404 for the unauthenticated API.
