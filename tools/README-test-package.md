# Windows 3.6.1 test package assembly

`assemble-test-package.py` assembles the Windows test package from explicit,
already-built inputs. It does not replace the normal Windows/Linux release
assembler or publish a release. Python 3.9 or later is required.

Use a pristine extracted 3.6.0 core package as `--base`, the native test artifact
from mpv-winbuild as `--native`, and self-contained Windows publish directories
for the fixed Manager and updater. The destination must not exist and must be
separate from the inputs.

```text
python tools/assemble-test-package.py --base PATH --native PATH --manager PATH --updater PATH --output NEW_PATH --mpv-commit FULL_SHA --mpv-repository OWNER/mpv --main-commit FULL_SHA --manager-commit FULL_SHA --native-run-url RUN_URL
```

The default test version is `3.6.1-test.1`; the default component release is
`3.6.0`. Both can be specified explicitly with `--version` and
`--component-version`. A component pin is appropriate only when the inference
runtime/model dependencies are unchanged. The base manifest must match it.
Normal release manifests omit `component_package_version` and select their own
release's components.

The script checks the native artifact's recorded commit, copies the current noise
shaders, selects the included DirectML backend, and records source identifiers
and key file hashes in `build-info/test-package.json`. `--mpv-repository` defaults
to `the-database/mpv`; supply the fork name when testing a fork. The caller must
provide the actual source commits used to build Manager and the updater.

Keep the resulting directory separate from the user's working installation.
The MSYS2 UCRT64 native artifact uses different dependencies from the production
cross-build. Real desktop playback and GPU validation remain necessary.
