const RESOLUTION = /^[1-9][0-9]{1,4}x[1-9][0-9]{1,4}$/;

// Shared by ingestion and catalog aggregation. Legacy records remain readable,
// but cannot silently acquire v2 semantics without the measured output context.
export function measurementError(schema, measurement) {
  if (schema === 1) return measurement == null ? null : "schema 1 cannot contain v2 measurement metadata";
  if (schema !== 2) return "unsupported schema";
  if (!measurement || typeof measurement !== "object" || Array.isArray(measurement)) return "missing measurement";
  if (measurement.method !== "mpv-output-trace-v2") return "unsupported measurement method";
  if (!["native-display", "pipeline"].includes(measurement.mode)) return "invalid measurement mode";
  if (measurement.mode === "native-display") {
    if (typeof measurement.output_resolution !== "string" || !RESOLUTION.test(measurement.output_resolution))
      return "native-display requires an output resolution";
  } else if (measurement.output_resolution != null || measurement.display_hz != null) {
    return "pipeline measurements have no output display";
  }
  if (measurement.display_hz != null && (typeof measurement.display_hz !== "number" ||
      !Number.isFinite(measurement.display_hz) || measurement.display_hz <= 0 || measurement.display_hz > 1000))
    return "invalid display refresh rate";
  return null;
}

export function normalizeMeasurement(measurement) {
  if (!measurement) return undefined;
  return {
    method: measurement.method,
    mode: measurement.mode,
    ...(measurement.output_resolution != null ? { output_resolution: measurement.output_resolution } : {}),
    ...(measurement.display_hz != null ? { display_hz: Math.round(measurement.display_hz * 1000) / 1000 } : {}),
  };
}
