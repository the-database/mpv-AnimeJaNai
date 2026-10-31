import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, cp, writeFile, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import worker from "../benchmark-proxy/src/index.js";
import { measurementKind, outputResolutions, matchesMeasurement, measurementLabel } from "../site/measurement.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const base = { schema: 1, backend: "TensorRT", gpu: "NVIDIA GeForce RTX 4090", results: { Balanced: { "1920x1080": 89.5 } } };
const native = (resolution) => ({ ...base, schema: 2, measurement: {
  method: "mpv-output-trace-v2", mode: "native-display", output_resolution: resolution, display_hz: 143.9997,
} });
const pipeline = { ...base, schema: 2, measurement: { method: "mpv-output-trace-v2", mode: "pipeline" } };

test("ingestion preserves measured output context; legacy stays legacy", async (t) => {
  const records = [];
  // Exercise the real HTTP handler and stored payload with no network writes.
  t.mock.method(globalThis, "fetch", async (url, init) => {
    if (init.method === "PUT") {
      const body = JSON.parse(init.body);
      records.push(JSON.parse(Buffer.from(body.content, "base64").toString("utf8")));
    }
    return Response.json({ object: { sha: "fixture" }, html_url: "https://example.test/pr" });
  });
  const submit = (body) => worker.fetch(new Request("https://example.test/api/benchmarks", {
    method: "POST", body: JSON.stringify(body),
  }), { GITHUB_REPO: "fixture/repo", GITHUB_TOKEN: "fixture" });
  for (const s of [base, native("2560x1440"), native("3840x2160"), pipeline])
    assert.equal((await submit(s)).status, 201);
  assert.equal(records[1].schema, 2);
  assert.deepEqual(records[1].measurement, { method: "mpv-output-trace-v2", mode: "native-display", output_resolution: "2560x1440", display_hz: 144 });
  assert.equal(records[0].measurement, undefined);
  assert.equal(records[3].measurement.output_resolution, undefined);
  for (const s of [
    { ...native("2560x1440"), measurement: { method: "mpv-output-trace-v2", mode: "native-display" } },
    { ...base, measurement: native("2560x1440").measurement },
    { ...pipeline, measurement: { ...pipeline.measurement, output_resolution: "3840x2160" } },
  ]) assert.equal((await submit(s)).status, 400);
  assert.equal(records.length, 4, "Invalid metadata must not reach GitHub.");
});

test("catalog separates timing methods and native render resolutions before ranking", () => {
  const subs = [base, native("2560x1440"), native("3840x2160"), pipeline];
  assert.deepEqual(outputResolutions(subs), ["3840x2160", "2560x1440"]);
  assert.deepEqual(subs.filter(s => matchesMeasurement(s, "native-display", "2560x1440")), [subs[1]]);
  assert.deepEqual(subs.filter(s => matchesMeasurement(s, "pipeline", "3840x2160")), [pipeline]);
  assert.deepEqual(subs.filter(s => matchesMeasurement(s, "legacy", "")), [base]);
  assert.equal(measurementKind({ results: base.results }), "legacy"); // pre-v2 aggregate omitted schema
  assert.match(measurementLabel(subs[1]), /2560x1440/);
  assert.match(measurementLabel(base), /Legacy/);
});

test("aggregation retains v2 metadata and existing schema 1 submissions", async () => {
  const dir = await mkdtemp(path.join(tmpdir(), "ajn-catalog-"));
  try {
    for (const subdir of ["scripts", "benchmark-proxy/src", "data/benchmarks/submissions", "site"])
      await mkdir(path.join(dir, subdir), { recursive: true });
    for (const file of ["scripts/build-benchmarks.mjs", "benchmark-proxy/src/measurement.mjs"])
      await cp(path.join(root, file), path.join(dir, file));
    const records = [base, native("2560x1440"), pipeline, { ...base, schema: 2 }];
    for (const [i, s] of records.entries())
      await writeFile(path.join(dir, `data/benchmarks/submissions/${i}.json`), JSON.stringify(s));
    execFileSync(process.execPath, [path.join(dir, "scripts/build-benchmarks.mjs")], { stdio: "pipe" });
    const data = JSON.parse(await readFile(path.join(dir, "site/benchmarks.json"), "utf8"));
    assert.equal(data.count, 3);
    assert.equal(data.submissions.find(s => s.id === "1").measurement.output_resolution, "2560x1440");
    assert.equal(data.submissions.find(s => s.id === "0").schema, 1);
    assert.equal(data.submissions.find(s => s.id === "0").measurement, undefined);
  } finally {
    assert.equal(path.dirname(dir), path.resolve(tmpdir()));
    assert.ok(path.basename(dir).startsWith("ajn-catalog-"));
    await rm(dir, { recursive: true, force: true });
  }
});
