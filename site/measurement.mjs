export function measurementKind(submission) {
  const m = submission.measurement;
  if (!m) return "legacy";
  return m.method === "mpv-output-trace-v2" ? m.mode : "unknown";
}

export function outputResolutions(submissions) {
  const pixels = (r) => r.split("x").reduce((a, b) => a * Number(b), 1);
  return [...new Set(submissions.filter((s) => measurementKind(s) === "native-display")
    .map((s) => s.measurement.output_resolution))].sort((a, b) => pixels(b) - pixels(a) || a.localeCompare(b));
}

export function matchesMeasurement(submission, mode, outputResolution) {
  return measurementKind(submission) === mode &&
    (mode !== "native-display" || submission.measurement.output_resolution === outputResolution);
}

export function measurementLabel(submission) {
  const kind = measurementKind(submission);
  if (kind === "legacy") return "Legacy / unversioned measurement";
  if (kind === "pipeline") return "Decode + upscale · no display rendering";
  if (kind !== "native-display") return "Unknown measurement method";
  const m = submission.measurement;
  return `Native display · ${m.output_resolution}` + (m.display_hz ? ` · ${m.display_hz} Hz` : "");
}

// Which engine ran a TensorRT result: native GPU kernels came in 3.12.0, and the runner records them
// from 3.12.2 (measurement.native_kernels). Older results all ran TensorRT engines; a 3.12.0/3.12.1
// result without the flag could be either, so it stays plain "TensorRT".
export function backendLabel(submission) {
  const backend = submission.backend || "";
  if (backend !== "TensorRT") return backend;
  const native = submission.measurement?.native_kernels;
  if (native === true) return "TensorRT backend · native GPU kernels";
  if (native === false) return "TensorRT engines";
  const [major, minor] = String(submission.app_version || "").split(".").map(Number);
  return Number.isFinite(major) && Number.isFinite(minor) && (major < 3 || (major === 3 && minor < 12))
    ? "TensorRT engines" : "TensorRT";
}
