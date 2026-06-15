# AnimeJaNai benchmark submit proxy

A tiny Cloudflare Worker that lets the **AnimeJaNai Manager** submit community
benchmark results **without the end user needing a GitHub account**. The Worker
holds the only GitHub credential, validates each submission, and opens a pull
request adding `data/benchmarks/submissions/<id>.json`. Merging the PR triggers
[`.github/workflows/benchmarks.yml`](../.github/workflows/benchmarks.yml), which
rebuilds the published catalog (GitHub Pages).

```
Manager  --POST animejan.ai/api/benchmarks-->  Worker  --opens PR-->  repo
                                                                         |
                                                  merge --> Action --> Pages catalog
```

## Why a proxy

Writing into a GitHub repo needs *someone's* credentials. Requiring end users to
have a GitHub account defeats the point of a one-click submission, so the write
goes through this maintainer-operated Worker instead. The GitHub token lives only
as a Worker secret — never in the desktop app and never in this repo.

## Endpoint

`POST /api/benchmarks` — JSON body (schema 1). The Manager builds this; see
`AnimeJaNaiManager` → `Services/BenchmarkSubmission.cs`.

```json
{
  "schema": 1,
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

`id` and `submitted_at` are stamped by the Worker; client values are ignored.
Responses: `201 {ok,id,pr_url}` on success, `400` invalid, `403` bad/absent
`X-Submit-Token` (if configured), `413` too large, `502` GitHub error.

## Deploy

```sh
cd benchmark-proxy
npm install
npx wrangler login
# Fine-grained PAT: repo = mpv-upscale-2x_animejanai only,
#   Contents: Read/Write, Pull requests: Read/Write
npx wrangler secret put GITHUB_TOKEN
npx wrangler secret put SUBMIT_TOKEN   # optional; must match the Manager
npx wrangler deploy
```

Then attach the route `animejan.ai/api/benchmarks` to this Worker (dashboard →
Workers Routes, or uncomment the `route` line in `wrangler.toml`).

## Abuse protection

Account-free submission means there is no per-user identity, so layer defenses:

1. **Cloudflare Rate Limiting rule** (dashboard → Security → WAF → Rate limiting)
   on `http.request.uri.path eq "/api/benchmarks"`, e.g. 5 requests / 10 min per
   client IP → block. This is the primary throttle and lives outside the code.
2. **Server-side validation** (in `src/index.js`): schema, backend allow-list,
   fps bounds, resolution pattern, payload + string length caps, HTML stripped.
3. **PR gate** — nothing publishes until the maintainer merges the PR. Spam shows
   up as closeable PRs, never as live catalog entries.
4. **Optional `SUBMIT_TOKEN`** — a shared header the Manager sends. It ships in
   the client so it is not a true secret, but it deters drive-by bots hitting the
   bare endpoint.

If submission volume and trust later make review unnecessary, the Worker can
commit straight to `data/benchmarks/submissions/` instead of opening a PR (drop
the branch+PR calls in `fileSubmission`, PUT directly onto `GITHUB_BASE`).
