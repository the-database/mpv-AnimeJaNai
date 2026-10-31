# Community benchmark submissions

Each file in `submissions/` is one hardware benchmark result submitted
from the **AnimeJaNai Manager**. They arrive as pull requests opened by the
[submit proxy](../../benchmark-proxy/); merging a PR publishes that result to the
[catalog](https://benchmarks.animejan.ai) via
[`.github/workflows/benchmarks.yml`](../../.github/workflows/benchmarks.yml).

These replace the old, manually-edited wiki page
(https://github.com/the-database/mpv-upscale-2x_animejanai/wiki/Benchmarks),
which is kept as an archive of pre-3.4.0 (VapourSynth-baseline) results.

## File shape

`submissions/<uuid>.json`:

```json
{
  "schema": 2,
  "measurement": {
    "method": "mpv-output-trace-v2",
    "mode": "native-display",
    "output_resolution": "2560x1440",
    "display_hz": 144
  },
  "id": "<uuid, set by the proxy>",
  "submitted_at": "<ISO timestamp, set by the proxy>",
  "app_version": "3.4.0",
  "backend": "TensorRT",
  "gpu": "NVIDIA GeForce RTX 4090",
  "cpu": "AMD Ryzen 9 7950X",
  "os": "Microsoft Windows 11 ...",
  "driver": "560.94",
  "results": {
    "Balanced":    { "1920x1080": 78.99, "1280x720": 210.3, "480x360": 1204.47 },
    "Performance": { "1920x1080": 150.2 }
  },
  "note": ""
}
```

Schema 2 requires `measurement.method` and `measurement.mode`. Native-display
measurements also require `output_resolution`, the actual fullscreen renderer
size recorded during the run; `display_hz` is optional. This is distinct from
the input resolutions in `results` and the model's 2× output. Pipeline-only
measurements use `mode: "pipeline"` and omit both display fields.

Schema 1 remains accepted for old benchmark files and contains no `measurement`.
The catalog lists legacy results separately; it does not infer an output screen
or upgrade their timing method. Native-display comparisons are filtered to one
render resolution at a time. See the [measurement method](../../AnimeJaNaiBenchmark/README.md).

## Maintainer notes

- Deploy the updated submission proxy and catalog before releasing the new
  runner. Publish the companion AnimeJaNaiManager metadata parser as **0.10.4**
  (Windows and Linux assets), matching `ManagerVersion` in the assembler, before
  packaging the app. Older Managers ignore the new header and would submit a
  legacy record. If a different Manager tag is chosen, update the pin together.

- To reject a submission, just close its PR.
- To remove a published result, delete its JSON file — the next catalog build
  drops it.
- The aggregator (`scripts/build-benchmarks.mjs`) skips any file that fails
  validation, so a malformed file won't break the catalog.
