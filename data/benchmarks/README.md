# Community benchmark submissions

Each file in `submissions/` is one hardware benchmark result (schema 1) submitted
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
  "schema": 1,
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

## Maintainer notes

- To reject a submission, just close its PR.
- To remove a published result, delete its JSON file — the next catalog build
  drops it.
- The aggregator (`scripts/build-benchmarks.mjs`) skips any file that fails
  validation, so a malformed file won't break the catalog.
