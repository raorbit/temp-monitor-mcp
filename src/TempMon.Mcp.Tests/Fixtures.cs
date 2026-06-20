using System.Globalization;
using System.Text.Json;

namespace TempMon.Mcp.Tests;

/// <summary>
/// Hand-rolled snapshot JSON that mirrors the <c>GET /temps</c> wire contract. Built by hand (not by
/// referencing TempMon.Core) on purpose: these tests guard the exact bytes the tools raw-parse, and
/// the one-rule keeps the MCP side off a Core ProjectReference.
/// </summary>
internal static class Fixtures
{
    /// <summary>
    /// A representative snapshot: a CPU sensor over the default 80°C limit, a GPU sensor under its
    /// limit, a Storage sensor under its limit, and a hot Motherboard sensor (which check_thresholds
    /// must ignore). <paramref name="timestamp"/> is dropped in verbatim so a caller can age it.
    /// </summary>
    public static string Snapshot(string timestamp) => $$"""
        {
          "timestamp": "{{timestamp}}",
          "host": "TEST-PC",
          "summary": { "cpu_c": 82.5, "gpu_c": 60.0, "max_drive_c": 41.0 },
          "sensors": [
            { "component": "CPU", "device": "Core i9", "name": "CPU Package", "value": 82.5, "min": 30.0, "max": 90.0 },
            { "component": "GPU", "device": "RTX 4090", "name": "GPU Core", "value": 60.0, "min": 28.0, "max": 71.0 },
            { "component": "Storage", "device": "Samsung 990", "name": "Drive", "value": 41.0, "min": 30.0, "max": 55.0 },
            { "component": "Motherboard", "device": "Z790", "name": "VRM", "value": 95.0, "min": 40.0, "max": 99.0 }
          ]
        }
        """;

    /// <summary>A snapshot carrying <c>schema_version: 1</c> and <c>sensors_available: true</c>, so a
    /// test can assert the get_temperatures envelope echoes the version. The plain <see cref="Snapshot"/>
    /// builder omits both keys, which backs the "absent ⇒ 0" / missing-key-is-available cases.</summary>
    public static string VersionedSnapshot(string timestamp) => $$"""
        {
          "schema_version": 1,
          "sensors_available": true,
          "timestamp": "{{timestamp}}",
          "host": "TEST-PC",
          "summary": { "cpu_c": 82.5, "gpu_c": 60.0, "max_drive_c": 41.0 },
          "sensors": [
            { "component": "CPU", "device": "Core i9", "name": "CPU Package", "value": 82.5, "min": 30.0, "max": 90.0 }
          ]
        }
        """;

    /// <summary>A snapshot from a desktop whose sensor reader never opened: <c>sensors_available: false</c>,
    /// an all-null summary and no sensors. The summarising tools must turn this into an explicit
    /// unavailable verdict, never a false "nothing over limit".</summary>
    public static string UnavailableSnapshot() => $$"""
        {
          "schema_version": 1,
          "sensors_available": false,
          "timestamp": "{{TimestampSecondsAgo(0)}}",
          "host": "TEST-PC",
          "summary": { "cpu_c": null, "gpu_c": null, "max_drive_c": null },
          "sensors": []
        }
        """;

    /// <summary>An ISO-8601 (round-trip) UTC timestamp <paramref name="secondsAgo"/> in the past.</summary>
    public static string TimestampSecondsAgo(double secondsAgo) =>
        DateTimeOffset.UtcNow.AddSeconds(-secondsAgo).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>A fresh snapshot (timestamp = now), so MarkStaleIfNeeded passes it through verbatim.</summary>
    public static string FreshSnapshot() => Snapshot(TimestampSecondsAgo(0));

    /// <summary>A snapshot whose timestamp is well past the 30s staleness threshold.</summary>
    public static string StaleSnapshot(double ageSeconds = 120) => Snapshot(TimestampSecondsAgo(ageSeconds));

    /// <summary>Parses a tool's JSON return value into a JsonDocument for assertions.</summary>
    public static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}
