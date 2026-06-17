// AnimeJaNai community benchmark submit proxy (Cloudflare Worker).
//
// Receives a benchmark result from the AnimeJaNai Manager and files it into
// the GitHub repo as a pull request. The GitHub credential lives ONLY here
// (a Worker secret) so end users need no GitHub account or token. A human
// merges the PR, which triggers the aggregation Action that rebuilds the
// catalog (.github/workflows/benchmarks.yml).
//
// Defenses against abuse (account-free submission is the whole point, so this
// matters): (1) a Cloudflare Rate Limiting rule on the route, configured in
// the dashboard - see README; (2) strict server-side validation below;
// (3) the PR gate - nothing is published until the maintainer merges. An
// optional shared SUBMIT_TOKEN raises the bar against drive-by bots; it is
// not a real secret (it ships in the client) and is not relied on for safety.
//
// Secrets (wrangler secret put <NAME>):
//   GITHUB_TOKEN   fine-grained PAT, Contents + Pull requests: write, on the
//                  one repo only.
//   SUBMIT_TOKEN   (optional) shared token the Manager sends; if set, requests
//                  without a matching X-Submit-Token header are rejected.
// Vars ([vars] in wrangler.toml):
//   GITHUB_REPO    e.g. "the-database/mpv-upscale-2x_animejanai"
//   GITHUB_BASE    base branch for PRs, e.g. "main"

const ROUTE = "/api/benchmarks";
const MAX_BODY = 16 * 1024; // 16 KB
const FPS_MIN = 0;
const FPS_MAX = 100000;
const SKIP_SENTINEL = -1; // "skipped, ran too slow to measure"; catalog renders "-"
const ALLOWED_BACKENDS = new Set(["TensorRT", "DirectML", "ncnn"]);
const RES_RE = /^\d{2,5}x\d{2,5}$/;
const STR_MAX = 200;
const NOTE_MAX = 280;
const SUBMISSION_DIR = "data/benchmarks/submissions";

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") return cors(new Response(null, { status: 204 }));

    const url = new URL(request.url);
    if (url.pathname !== ROUTE) return cors(json({ error: "not found" }, 404));
    if (request.method !== "POST") return cors(json({ error: "method not allowed" }, 405));

    if (env.SUBMIT_TOKEN && request.headers.get("X-Submit-Token") !== env.SUBMIT_TOKEN) {
      return cors(json({ error: "forbidden" }, 403));
    }

    const raw = await request.text();
    if (raw.length > MAX_BODY) return cors(json({ error: "payload too large" }, 413));

    let body;
    try {
      body = JSON.parse(raw);
    } catch {
      return cors(json({ error: "invalid JSON" }, 400));
    }

    const err = validate(body);
    if (err) return cors(json({ error: err }, 400));

    const record = normalize(body);
    try {
      const pr = await fileSubmission(env, record);
      return cors(json({ ok: true, id: record.id, pr_url: pr.html_url }, 201));
    } catch (e) {
      return cors(json({ error: "could not file submission", detail: String((e && e.message) || e) }, 502));
    }
  },
};

function validate(b) {
  if (b == null || typeof b !== "object") return "body must be an object";
  if (b.schema !== 1) return "unsupported schema";
  if (typeof b.backend !== "string" || !ALLOWED_BACKENDS.has(b.backend)) return "invalid backend";
  for (const k of ["app_version", "gpu", "cpu", "os", "driver", "submitted_by"]) {
    if (b[k] != null && (typeof b[k] !== "string" || b[k].length > STR_MAX)) return `invalid ${k}`;
  }
  if (b.backend === "TensorRT" && !isNvidiaGpuName(b.gpu)) {
    return "TensorRT submissions must identify the NVIDIA GPU";
  }
  if (b.note != null && (typeof b.note !== "string" || b.note.length > NOTE_MAX)) return "invalid note";
  if (b.vram_mb != null && (typeof b.vram_mb !== "number" || !isFinite(b.vram_mb) || b.vram_mb < 0 || b.vram_mb > 4194304)) return "invalid vram_mb";
  if (b.gpu_power_w != null && (typeof b.gpu_power_w !== "number" || !isFinite(b.gpu_power_w) || b.gpu_power_w < 0 || b.gpu_power_w > 10000)) return "invalid gpu_power_w";
  if (b.ram_mb != null && (typeof b.ram_mb !== "number" || !isFinite(b.ram_mb) || b.ram_mb < 0 || b.ram_mb > 8388608)) return "invalid ram_mb";
  if (b.ram_speed_mhz != null && (typeof b.ram_speed_mhz !== "number" || !isFinite(b.ram_speed_mhz) || b.ram_speed_mhz < 0 || b.ram_speed_mhz > 20000)) return "invalid ram_speed_mhz";
  if (b.cpu_mhz != null && (typeof b.cpu_mhz !== "number" || !isFinite(b.cpu_mhz) || b.cpu_mhz < 0 || b.cpu_mhz > 20000)) return "invalid cpu_mhz";
  if (b.cpu_cores != null && (typeof b.cpu_cores !== "number" || !isFinite(b.cpu_cores) || b.cpu_cores < 0 || b.cpu_cores > 1024)) return "invalid cpu_cores";
  if (b.cpu_threads != null && (typeof b.cpu_threads !== "number" || !isFinite(b.cpu_threads) || b.cpu_threads < 0 || b.cpu_threads > 2048)) return "invalid cpu_threads";

  const results = b.results;
  if (results == null || typeof results !== "object" || Array.isArray(results)) return "invalid results";
  const templates = Object.keys(results);
  if (templates.length === 0 || templates.length > 16) return "invalid results";
  let cells = 0;
  let measured = 0;
  for (const t of templates) {
    if (typeof t !== "string" || t.length > 40) return "invalid template name";
    const row = results[t];
    if (row == null || typeof row !== "object" || Array.isArray(row)) return "invalid results row";
    for (const res of Object.keys(row)) {
      if (!RES_RE.test(res)) return `invalid resolution: ${res}`;
      const fps = row[res];
      // -1 is the "skipped, too slow to measure" sentinel; any other value must
      // be a real, positive fps.
      if (typeof fps !== "number" || !isFinite(fps)) return `invalid fps for ${res}`;
      if (fps !== SKIP_SENTINEL && (fps <= FPS_MIN || fps > FPS_MAX)) return `invalid fps for ${res}`;
      if (fps > 0) measured++;
      if (++cells > 256) return "too many results";
    }
  }
  if (measured === 0) return "no results"; // nothing actually ran; sentinels only
  return null;
}

function isNvidiaGpuName(value) {
  return /nvidia|geforce|\brtx\b|\bgtx\b|quadro|titan/i.test(String(value || ""));
}

// Replace control characters and angle brackets with spaces, then collapse
// whitespace. Done by char code so the source stays clean ASCII.
function sanitize(value, max) {
  const s = String(value == null ? "" : value);
  let out = "";
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    const ch = s[i];
    out += c < 0x20 || c === 0x7f || ch === "<" || ch === ">" ? " " : ch;
  }
  return out.replace(/\s+/g, " ").trim().slice(0, max);
}

// Build the server-authoritative record: the client never sets id/submitted_at.
function normalize(b) {
  const results = {};
  for (const t of Object.keys(b.results)) {
    const row = {};
    for (const res of Object.keys(b.results[t])) {
      row[res] = Math.round(b.results[t][res] * 100) / 100;
    }
    results[sanitize(t, 40)] = row;
  }
  return {
    schema: 1,
    id: crypto.randomUUID(),
    submitted_at: new Date().toISOString(),
    app_version: sanitize(b.app_version, STR_MAX),
    backend: b.backend,
    gpu: sanitize(b.gpu, STR_MAX),
    vram_mb: typeof b.vram_mb === "number" ? Math.round(b.vram_mb) : undefined,
    gpu_power_w: typeof b.gpu_power_w === "number" ? Math.round(b.gpu_power_w) : undefined,
    cpu: sanitize(b.cpu, STR_MAX),
    cpu_mhz: typeof b.cpu_mhz === "number" ? Math.round(b.cpu_mhz) : undefined,
    cpu_cores: typeof b.cpu_cores === "number" ? Math.round(b.cpu_cores) : undefined,
    cpu_threads: typeof b.cpu_threads === "number" ? Math.round(b.cpu_threads) : undefined,
    ram_mb: typeof b.ram_mb === "number" ? Math.round(b.ram_mb) : undefined,
    ram_speed_mhz: typeof b.ram_speed_mhz === "number" ? Math.round(b.ram_speed_mhz) : undefined,
    os: sanitize(b.os, STR_MAX),
    driver: sanitize(b.driver, STR_MAX),
    results,
    submitted_by: sanitize(b.submitted_by, 80),
    note: sanitize(b.note, NOTE_MAX),
  };
}

async function fileSubmission(env, record) {
  const repo = env.GITHUB_REPO;
  const base = env.GITHUB_BASE || "main";
  const branch = `benchmark/${record.id}`;
  const path = `${SUBMISSION_DIR}/${record.id}.json`;
  const content = b64(JSON.stringify(record, null, 2) + "\n");
  const label = record.gpu || "unknown GPU";

  const baseRef = await gh(env, "GET", `/repos/${repo}/git/ref/heads/${base}`);
  await gh(env, "POST", `/repos/${repo}/git/refs`, {
    ref: `refs/heads/${branch}`,
    sha: baseRef.object.sha,
  });
  await gh(env, "PUT", `/repos/${repo}/contents/${encodeURI(path)}`, {
    message: `benchmark: ${label} (${record.backend})`,
    content,
    branch,
  });
  return gh(env, "POST", `/repos/${repo}/pulls`, {
    title: `Benchmark: ${label} (${record.backend})`,
    head: branch,
    base,
    body:
      "Automated community benchmark submission.\n\n" +
      `- GPU: ${record.gpu || "(unknown)"}\n` +
      `- Backend: ${record.backend}\n` +
      `- App: ${record.app_version || "(unknown)"}\n` +
      `- OS: ${record.os || "(unknown)"}\n` +
      `- Submitted by: ${record.submitted_by || "(anonymous)"}\n\n` +
      (record.note ? `Note: ${record.note}\n\n` : "") +
      "Merging publishes it to the catalog.",
  });
}

async function gh(env, method, path, payload) {
  const resp = await fetch(`https://api.github.com${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${env.GITHUB_TOKEN}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      "User-Agent": "animejanai-benchmark-proxy",
      "Content-Type": "application/json",
    },
    body: payload ? JSON.stringify(payload) : undefined,
  });
  const text = await resp.text();
  if (!resp.ok) throw new Error(`GitHub ${method} ${path} -> ${resp.status}: ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : {};
}

// UTF-8 safe base64 for the GitHub Contents API.
function b64(str) {
  const bytes = new TextEncoder().encode(str);
  let bin = "";
  for (const byte of bytes) bin += String.fromCharCode(byte);
  return btoa(bin);
}

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function cors(resp) {
  resp.headers.set("Access-Control-Allow-Origin", "*");
  resp.headers.set("Access-Control-Allow-Methods", "POST, OPTIONS");
  resp.headers.set("Access-Control-Allow-Headers", "Content-Type, X-Submit-Token");
  return resp;
}
