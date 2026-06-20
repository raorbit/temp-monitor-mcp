using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempMon.Core;

/// <summary>
/// The full temperature snapshot. This is the JSON contract served verbatim at <c>GET /temps</c>
/// and returned by the MCP <c>get_temperatures</c> tool. Property names are pinned with
/// <see cref="JsonPropertyNameAttribute"/> so the wire shape never drifts with naming policy.
///
/// <para><see cref="SchemaVersion"/> is the contract version a consumer can branch on — the desktop
/// and the MCP server ship as separate exes and update independently, so this is their only wire
/// handshake. <see cref="SensorsAvailable"/> is <c>false</c> when the hardware reader never opened
/// (driver blocked, or the app is not elevated): the values are then unreadable, <b>not</b> a safe
/// "all clear" — the MCP summarising tools turn this into an explicit unavailable verdict.</para>
/// </summary>
public sealed record Snapshot(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("sensors_available")] bool SensorsAvailable,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("summary")] Summary Summary,
    [property: JsonPropertyName("sensors")] IReadOnlyList<SensorReading> Sensors)
{
    /// <summary>The current wire-contract version. Bump only on a breaking shape change.</summary>
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>The headline numbers shown in the dashboard strip and returned by <c>get_summary</c>.</summary>
public sealed record Summary(
    [property: JsonPropertyName("cpu_c")] double? CpuC,
    [property: JsonPropertyName("gpu_c")] double? GpuC,
    [property: JsonPropertyName("max_drive_c")] double? MaxDriveC);

/// <summary>One temperature sensor. <see cref="Value"/>/<see cref="Min"/>/<see cref="Max"/> are
/// <c>null</c> when the read failed or the process is not elevated.</summary>
public sealed record SensorReading(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("min")] double? Min,
    [property: JsonPropertyName("max")] double? Max);

/// <summary>The four component buckets the contract allows: <c>CPU | GPU | Motherboard | Storage</c>.</summary>
public static class Component
{
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Motherboard = "Motherboard";
    public const string Storage = "Storage";
}
