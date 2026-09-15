# Windows addon release integration

This is a preview. Public API ownership, representative application acceptance and support commitments still require maintainer decisions. Windows is implemented first; Linux assembly excludes this runtime. The Plex and bias-light applications and website/catalog distribution remain separate projects.

## Native build

Dispatch the ordinary `mpv-winbuild/mpv.yml` workflow with `build_target=64bit`, `compiler=clang`, the selected mpv/libass repositories and full source commits, `addon_metadata=true`, and `release=false` for tests. Default non-addon release behavior is retained. The producing job checks the source ABI declaration, actual build features and PE imports, then emits `mpv-addon-native-x86_64.json` before packaging.

Use the player and development archives from that same run. Flatten their recorded binaries into one native input directory and keep the license material. The assembler verifies every native file against the producer record; it cannot manufacture native capability evidence from arbitrary DLL hashes. Static UCRT FFmpeg needs no separate `avformat` DLL. Old sealed shared-FFmpeg previews retain their legacy detection only when the new record is absent. Invalid new metadata never falls back to an old marker.

Schema 1 allows up to 256 native files and 1 MiB of metadata. This accommodates the complete shared dependency closure (139 files in the tested MSYS2 build) while retaining bounded parsing and exact file checks. The producer, host and assembler enforce compatible limits. `test-build/test_native_metadata.py` in winbuild tests configuration, source pins, CRT and linkage rejection paths.

The null-output hardware-frame shortcut requires `--vo-null-accept-hwframes=yes`. The owned non-encoding media worker opts in. Ordinary benchmarks retain their previous default behavior. Compare both modes on the same machine and workload before changing a benchmark baseline.

## Managed builds and assembly

The API 1.7 assembler requires producer evidence for `privateProbeAbi`,
`privateMuxAbi` and `privateSubtitlesAbi`, in addition to the prior sample/output
ABIs. It rejects an older or partial native runtime instead of creating a full
streaming preview with missing capabilities. See `STREAMING-TESTS.md` for the
separate native and packaged-Wasm qualification gates.

Use PowerShell 7, .NET SDK 10 and the pinned tools from `addons/tools/bootstrap.ps1`. Run `addons/tools/test.ps1`, then `addons/tools/package.ps1 -OutputDirectory <new-host-bundle>`. Publish the updater normally with `dotnet publish AnimeJaNaiUpdater -c Release -r win-x64 -o <output>`. In the matching Manager checkout, run `tools/check-addon-client.ps1` before publishing `AnimeJaNaiConfEditor` for win-x64 with self-contained and single-file options. Tests can pass `-p:UsedAvaloniaProducts=` to suppress the dependency's telemetry task.

Host packaging requires a clean Git checkout; `-Git <git-executable>` selects Git when it is not on PATH. It copies tracked source files and emits `host-build.json` with the source commit, source tree identities, SDK version and all bundle file hashes. Full/support assembly checks those trees against the supplied main checkout and verifies the bundle inventory. Later changes outside the host source trees do not require recompiling an identical host. This producer record establishes build coherence, not a publisher signature.

The host bundle contains the runtime, examples, SDK, offline documentation, licenses and buildable source, including the transaction source shared with the updater.

`tools/assemble-addon-preview.py` requires Python 3.12+, Git, produced native metadata/binaries, the host bundle, Manager publish directory and updater executable. Supply clean committed checkouts with `--main-source`, `--manager-source`, `--mpv-source` and `--winbuild-source`. Its `--help` lists all inputs.

Omit `--core-archive` to create a support bundle zip. Supply a core archive with `--core-sha256` and `--sevenzip` to create a full preview. Choose a new `--output` and preview `--version`; repeat `--evidence` for completed JSON test evidence. The result includes `addon-package.json`, source archives, native provenance, evidence and the zip's checksum. Checksum, source pin and SDK mismatches stop packaging.

When a historical core contains a native file inventory, obsolete shared dependencies are removed only from the new extracted copy and only after verifying their hashes. Core build evidence is retained under `build-info/core`; the new preview's authoritative record is `build-info/addon-preview.json`.

The normal assembler accepts `--addons` only for Windows. `AJN_ADDON_BUNDLE` points to an extracted support bundle. It stages the host, launcher, runtime, Manager and matching native set, includes managed addon files and build provenance in overlays, and preserves `animejanai/addons`. The native metadata checksum participates in dependency comparisons; changed native builds require full updates.

Deployment's optional `addons` input takes a support bundle HTTPS URL and SHA-256. It verifies that download and enables `/DEnableAddons` in Inno Setup. The normal installer path remains available with this option off. Addon installers stage files before invoking the updater transaction.

## Updates and recovery

The updater validates the addon inventory before replacing files, and rejects an incoming build that would silently drop addon support. Close the player and Manager first. Persistent update intent blocks new activation; existing hosts drain their jobs. An exclusive installation lease spans replacement after those processes exit. The bounded legacy fallback stops only helpers whose full executable paths belong to this installation.

All replacement files and backups are staged before a prepared journal is written. The journal becomes committed only after every replacement succeeds. If interrupted, addon startup stays blocked. Running the installer again or `AnimeJaNaiUpdater --recover` restores the complete prior file set. Recovery verifies all backups first and can be retried. A failed recovery preserves its journal for repair.

Addon data/settings are preserved independently of the release's preservation list. **Remove addon** preserves them too. Full application uninstall retains the existing policy of deleting the app tree, including data inside it. It drains this installation and unregisters only Run values still pointing at its own quoted launcher/login command. External/shared data roots and startup entries now pointing at another installation remain untouched.

Component installation/removal uses the same transaction for its runtime/model files and `components.json`, so those records recover together. Close the player first; Manager may stay open for these operations. Existing addon work stops and can be started again afterwards.

## Lifecycle and native validation

Run the native test executable with `<built-root> <new-output> <dotnet> --updates-only` to exercise real host shutdown, independent installations, persisted state after moving an installation, a terminated transaction process, and replacement of the running updater itself. The old executable may remain in the transaction directory until the next updater process cleans it up; that cleanup does not change a successful commit into a failed update.

For the actual full archives, run `<built-root> <new-output> <dotnet> <previous-full.zip> <current-full.zip> --archive-roundtrip-only`. It extracts isolated copies, uses the packaged updater to upgrade and roll back, checks every installed inventory hash, preserves internal addon settings/storage, exercises renewed package approval and addon rollback, and verifies the older API 1.6 host rejects an installed addon requiring API 1.7. Use the sealed integration.1 archive as the previous build and the matching new full archive; this test does not modify either source archive or the user's installation.

`installer/tests/test-addons.ps1 -BuiltRoot <preview> -Compiler <Inno-6-ISCC.exe> -OutputDirectory <new-output> -Dotnet <dotnet>` compiles and runs a real installer lifecycle test. Its `/DInstallerTest` build has a separate application name and registration, disables optional component downloads, and runs with shortcuts and associations disabled. The harness refuses a production installer. It checks fresh install, reinstall with an active addon, preservation of settings and external data, uninstall, and ownership of login entries. These test installers must never be distributed as normal releases.

For GPU validation, run the native suite against the exact produced binaries; use the additional output, encoding, player-frame, remote-input, capacity and lifecycle modes as needed. `--null-benchmark` compares the native default with explicit hardware-frame acceptance using alternating 240-frame runs at 480x360 and 1920x1080 with DirectML slot 1002. It records startup-inclusive wall/CPU time and raw logs, discards one warmup per mode, and does not change the catalog benchmark or establish performance on other hardware. Run GPU measurements sequentially.

## Paths and capability limits

Deep persistent data paths are supported by relocating disposable compiler and worker files to a short uniquely named temporary directory. No directory is exposed to the guest. Executable paths of 260 characters or more, or a temporary folder too long for this policy, produce a readable startup diagnostic. Move the application/tools or choose a shorter Windows TEMP folder. Windows Run-command length is checked separately.

Relocated runtime workers use a temporary parent specific to their data root. After an interrupted host, its next exclusive data-directory lease recovers the known disposable worker modules from both normal and relocated paths. Unfamiliar contents and another data root's temporary workers are left alone.

Samples represent processed SDR images using DirectML/D3D11. CUDA/TensorRT, HDR, subtitles/OSD and final display composition require separate implementations. Encoded output retains the private UCRT descriptor handoff. Native test results apply only to the exact binaries, backends and hardware recorded for each preview; unit tests and metadata alone do not establish GPU support.
