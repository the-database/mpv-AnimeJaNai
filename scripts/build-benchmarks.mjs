// Aggregate data/benchmarks/submissions/*.json into site/benchmarks.json for
// the GitHub Pages catalog. Pure Node built-ins, no dependencies. Malformed or
// invalid files are skipped (and logged) so one bad submission can't break the
// catalog. Run by .github/workflows/benchmarks.yml before deploying ./site.

import { readdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SUBMISSIONS_DIR = path.join(repoRoot, "data", "benchmarks", "submissions");
const OUT = path.join(repoRoot, "site", "benchmarks.json");

const ALLOWED_BACKENDS = new Set(["TensorRT", "DirectML", "ncnn"]);
const RES_RE = /^\d{2,5}x\d{2,5}$/;

function valid(s) {
  if (!s || typeof s !== "object") return false;
  if (s.schema !== 1) return false;
  if (!ALLOWED_BACKENDS.has(s.backend)) return false;
  if (!s.results || typeof s.results !== "object") return false;
  let measured = 0;
  for (const template of Object.keys(s.results)) {
    const row = s.results[template];
    if (!row || typeof row !== "object") return false;
    for (const res of Object.keys(row)) {
      if (!RES_RE.test(res)) return false;
      const fps = row[res];
      if (typeof fps !== "number" || !isFinite(fps)) return false;
      // -1 = "skipped, too slow to measure" sentinel; otherwise a positive fps.
      if (fps !== -1 && fps <= 0) return false;
      if (fps > 0) measured++;
    }
  }
  return measured > 0; // require at least one real measurement (sentinels alone don't count)
}

const files = (await readdir(SUBMISSIONS_DIR)).filter((f) => f.endsWith(".json"));
const submissions = [];
let skipped = 0;

for (const file of files.sort()) {
  let parsed;
  try {
    parsed = JSON.parse(await readFile(path.join(SUBMISSIONS_DIR, file), "utf8"));
  } catch (e) {
    console.warn(`skip ${file}: not valid JSON (${e.message})`);
    skipped++;
    continue;
  }
  if (!valid(parsed)) {
    console.warn(`skip ${file}: failed validation`);
    skipped++;
    continue;
  }
  submissions.push({
    id: parsed.id ?? path.basename(file, ".json"),
    submitted_at: parsed.submitted_at ?? null,
    app_version: parsed.app_version ?? "",
    backend: parsed.backend,
    gpu: parsed.gpu ?? "",
    vram_mb: typeof parsed.vram_mb === "number" ? parsed.vram_mb : null,
    gpu_power_w: typeof parsed.gpu_power_w === "number" ? parsed.gpu_power_w : null,
    cpu: parsed.cpu ?? "",
    cpu_mhz: typeof parsed.cpu_mhz === "number" ? parsed.cpu_mhz : null,
    cpu_cores: typeof parsed.cpu_cores === "number" ? parsed.cpu_cores : null,
    cpu_threads: typeof parsed.cpu_threads === "number" ? parsed.cpu_threads : null,
    ram_mb: typeof parsed.ram_mb === "number" ? parsed.ram_mb : null,
    ram_speed_mhz: typeof parsed.ram_speed_mhz === "number" ? parsed.ram_speed_mhz : null,
    os: parsed.os ?? "",
    driver: parsed.driver ?? "",
    results: parsed.results,
    submitted_by: parsed.submitted_by ?? "",
    note: parsed.note ?? "",
  });
}

submissions.sort((a, b) =>
  a.gpu.localeCompare(b.gpu) || a.backend.localeCompare(b.backend) ||
  String(b.submitted_at).localeCompare(String(a.submitted_at)));

const dataset = {
  generated_at: new Date().toISOString(),
  count: submissions.length,
  submissions,
};

await writeFile(OUT, JSON.stringify(dataset, null, 2) + "\n");
console.log(`Wrote ${OUT}: ${submissions.length} submissions (${skipped} skipped).`);
