using System.Text.Json;
using TempMon.Core;
using Xunit;

namespace TempMon.Core.Tests;

/// <summary>
/// Pins the JSON wire contract served verbatim at <c>GET /temps</c> and returned by the MCP
/// <c>get_temperatures</c> tool. Property names are locked here so the wire shape never drifts:
/// the MCP server raw-parses these names (<c>summary</c>, <c>sensors</c>, <c>component</c>,
/// <c>value</c>, <c>timestamp</c>) and the no-ProjectReference rule means nothing else guards them.
/// </summary>
public class SnapshotContractTests
{
    private static Snapshot Sample() => new(
        SchemaVersion: Snapshot.CurrentSchemaVersion,
        SensorsAvailable: true,
        Timestamp: "2026-06-18T14:32:05Z",
        Host: "DESKTOP-XYZ",
        Summary: new Summary(CpuC: 62.5, GpuC: 51.0, MaxDriveC: 44.0),
        Sensors: new[]
        {
            new SensorReading(Component.Cpu, "AMD Ryzen 9 7950X", "Core (Tctl/Tdie)", 62.5, 35.0, 78.2),
        });

    [Fact]
    public void Snapshot_serializes_with_pinned_top_level_property_names()
    {
        using var doc = JsonDocument.Parse(Sample().ToJson());
        var root = doc.RootElement;

        Assert.Equal(Snapshot.CurrentSchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.True(root.GetProperty("sensors_available").GetBoolean());
        Assert.Equal("2026-06-18T14:32:05Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("DESKTOP-XYZ", root.GetProperty("host").GetString());
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("sensors", out var sensors));
        Assert.Equal(JsonValueKind.Array, sensors.ValueKind);
    }

    [Fact]
    public void Schema_version_is_one_and_pinned()
    {
        // The /health endpoint echoes this value; this is its only automated coverage.
        Assert.Equal(1, Snapshot.CurrentSchemaVersion);
    }

    [Fact]
    public void Sensors_available_flag_round_trips_false()
    {
        var snapshot = new Snapshot(
            Snapshot.CurrentSchemaVersion, SensorsAvailable: false,
            "2026-06-18T14:32:05Z", "HOST",
            new Summary(null, null, null), Array.Empty<SensorReading>());

        using var doc = JsonDocument.Parse(snapshot.ToJson());
        Assert.False(doc.RootElement.GetProperty("sensors_available").GetBoolean());
    }

    [Fact]
    public void Summary_serializes_with_snake_case_pinned_names()
    {
        using var doc = JsonDocument.Parse(Sample().ToJson());
        var summary = doc.RootElement.GetProperty("summary");

        Assert.Equal(62.5, summary.GetProperty("cpu_c").GetDouble());
        Assert.Equal(51.0, summary.GetProperty("gpu_c").GetDouble());
        Assert.Equal(44.0, summary.GetProperty("max_drive_c").GetDouble());
    }

    [Fact]
    public void SensorReading_serializes_with_pinned_names()
    {
        using var doc = JsonDocument.Parse(Sample().ToJson());
        var sensor = doc.RootElement.GetProperty("sensors")[0];

        Assert.Equal("CPU", sensor.GetProperty("component").GetString());
        Assert.Equal("AMD Ryzen 9 7950X", sensor.GetProperty("device").GetString());
        Assert.Equal("Core (Tctl/Tdie)", sensor.GetProperty("name").GetString());
        Assert.Equal(62.5, sensor.GetProperty("value").GetDouble());
        Assert.Equal(35.0, sensor.GetProperty("min").GetDouble());
        Assert.Equal(78.2, sensor.GetProperty("max").GetDouble());
    }

    [Fact]
    public void Null_values_are_emitted_as_json_null_not_omitted()
    {
        // DefaultIgnoreCondition = Never: the contract says "any value null if the read failed",
        // so the keys must still be present with a null value (the MCP layer checks ValueKind).
        var snapshot = new Snapshot(
            Snapshot.CurrentSchemaVersion, SensorsAvailable: true,
            "2026-06-18T14:32:05Z", "HOST",
            new Summary(null, null, null),
            new[] { new SensorReading(Component.Gpu, "GPU", "Core", null, null, null) });

        using var doc = JsonDocument.Parse(snapshot.ToJson());

        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("cpu_c").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("gpu_c").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("max_drive_c").ValueKind);

        var sensor = doc.RootElement.GetProperty("sensors")[0];
        Assert.Equal(JsonValueKind.Null, sensor.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, sensor.GetProperty("min").ValueKind);
        Assert.Equal(JsonValueKind.Null, sensor.GetProperty("max").ValueKind);
    }

    [Fact]
    public void ToJson_is_compact_single_line()
    {
        // WriteIndented = false: the wire payload is a single line (no pretty-printing).
        var json = Sample().ToJson();

        Assert.DoesNotContain('\n', json);
        Assert.StartsWith("{\"schema_version\":", json);
    }

    [Fact]
    public void Component_constants_match_the_contract_vocabulary()
    {
        Assert.Equal("CPU", Component.Cpu);
        Assert.Equal("GPU", Component.Gpu);
        Assert.Equal("Motherboard", Component.Motherboard);
        Assert.Equal("Storage", Component.Storage);
    }
}
