using TempMon.Core;
using Xunit;

namespace TempMon.Core.Tests;

/// <summary>
/// Locks the headline-number selection rules in <see cref="SensorPoller"/>
/// (<c>RepresentativeCpu</c> / <c>RepresentativeGpu</c> / <c>MaxDrive</c>) without standing up a
/// real <c>Computer</c>. These helpers are <c>internal static</c> and reached via
/// <c>InternalsVisibleTo("TempMon.Core.Tests")</c>.
/// </summary>
public class SummarySelectionTests
{
    private static SensorReading Cpu(string name, double? value) =>
        new(Component.Cpu, "AMD Ryzen 9 7950X", name, value, null, null);

    private static SensorReading Gpu(string name, double? value) =>
        new(Component.Gpu, "NVIDIA GeForce RTX 4090", name, value, null, null);

    private static SensorReading Drive(string name, double? value) =>
        new(Component.Storage, "Samsung 990 Pro", name, value, null, null);

    private static SensorReading Mb(string name, double? value) =>
        new(Component.Motherboard, "Nuvoton NCT6686D", name, value, null, null);

    // --- RepresentativeCpu ---------------------------------------------------------------------

    [Fact]
    public void RepresentativeCpu_prefers_Tctl_over_everything()
    {
        var sensors = new[]
        {
            Cpu("Core #1", 50.0),
            Cpu("CPU Package", 60.0),
            Cpu("Core (Tctl/Tdie)", 62.5),
        };

        Assert.Equal(62.5, SensorPoller.RepresentativeCpu(sensors));
    }

    [Fact]
    public void RepresentativeCpu_falls_back_to_Package_when_no_Tctl()
    {
        var sensors = new[]
        {
            Cpu("Core #1", 50.0),
            Cpu("Core #2", 51.0),
            Cpu("CPU Package", 60.0),
        };

        Assert.Equal(60.0, SensorPoller.RepresentativeCpu(sensors));
    }

    [Fact]
    public void RepresentativeCpu_falls_back_to_Core_when_no_Tctl_or_Package()
    {
        var sensors = new[]
        {
            Cpu("CPU CCD1 (Tdie)", 48.0),   // not Tctl/Package/Core
            Cpu("Core #3", 55.0),
        };

        Assert.Equal(55.0, SensorPoller.RepresentativeCpu(sensors));
    }

    [Fact]
    public void RepresentativeCpu_falls_back_to_first_cpu_sensor_when_no_named_match()
    {
        var sensors = new[]
        {
            Cpu("CPU CCD1 (Tdie)", 48.0),
            Cpu("CPU CCD2 (Tdie)", 49.0),
        };

        Assert.Equal(48.0, SensorPoller.RepresentativeCpu(sensors));
    }

    [Fact]
    public void RepresentativeCpu_skips_null_valued_sensors_at_each_priority()
    {
        var sensors = new[]
        {
            Cpu("Core (Tctl/Tdie)", null),  // highest priority but no reading -> skipped
            Cpu("CPU Package", 60.0),
        };

        Assert.Equal(60.0, SensorPoller.RepresentativeCpu(sensors));
    }

    [Fact]
    public void RepresentativeCpu_ignores_non_cpu_components()
    {
        var sensors = new[]
        {
            Gpu("Core (Tctl/Tdie)", 70.0),  // GPU named like a CPU sensor -> ignored
            Drive("Temperature", 40.0),
        };

        Assert.Null(SensorPoller.RepresentativeCpu(sensors));
    }

    [Fact]
    public void RepresentativeCpu_is_null_when_no_cpu_sensors()
    {
        var sensors = new[] { Gpu("GPU Core", 70.0), Drive("Temperature", 40.0) };

        Assert.Null(SensorPoller.RepresentativeCpu(sensors));
    }

    // --- RepresentativeGpu ---------------------------------------------------------------------

    [Fact]
    public void RepresentativeGpu_prefers_Core_excluding_Hot_Spot()
    {
        var sensors = new[]
        {
            Gpu("GPU Hot Spot", 85.0),
            Gpu("GPU Core", 70.0),
        };

        Assert.Equal(70.0, SensorPoller.RepresentativeGpu(sensors));
    }

    [Fact]
    public void RepresentativeGpu_falls_back_to_Core_with_Hot_when_only_hot_core_present()
    {
        // Only a "Core" sensor that also says "Hot" exists -> the non-Hot rule finds nothing, so the
        // second rule (any Core) selects it.
        var sensors = new[]
        {
            Gpu("GPU Hot Spot (Core)", 88.0),
            Gpu("GPU Memory", 60.0),
        };

        Assert.Equal(88.0, SensorPoller.RepresentativeGpu(sensors));
    }

    [Fact]
    public void RepresentativeGpu_falls_back_to_first_gpu_sensor_when_no_Core()
    {
        var sensors = new[]
        {
            Gpu("GPU Memory Junction", 65.0),
            Gpu("GPU Hot Spot", 80.0),
        };

        Assert.Equal(65.0, SensorPoller.RepresentativeGpu(sensors));
    }

    [Fact]
    public void RepresentativeGpu_skips_null_valued_core_then_takes_next()
    {
        var sensors = new[]
        {
            Gpu("GPU Core", null),       // preferred but no reading -> skipped
            Gpu("GPU Memory", 60.0),
        };

        Assert.Equal(60.0, SensorPoller.RepresentativeGpu(sensors));
    }

    [Fact]
    public void RepresentativeGpu_ignores_non_gpu_components()
    {
        var sensors = new[] { Cpu("GPU Core", 70.0), Mb("Core", 40.0) };

        Assert.Null(SensorPoller.RepresentativeGpu(sensors));
    }

    [Fact]
    public void RepresentativeGpu_is_null_when_no_gpu_sensors()
    {
        var sensors = new[] { Cpu("Core (Tctl/Tdie)", 60.0), Drive("Temperature", 40.0) };

        Assert.Null(SensorPoller.RepresentativeGpu(sensors));
    }

    // --- MaxDrive ------------------------------------------------------------------------------

    [Fact]
    public void MaxDrive_returns_hottest_storage_value()
    {
        var sensors = new[]
        {
            Drive("Temperature", 40.0),
            Drive("Temperature 2", 47.0),
            Drive("Temperature 3", 44.0),
        };

        Assert.Equal(47.0, SensorPoller.MaxDrive(sensors));
    }

    [Fact]
    public void MaxDrive_ignores_non_storage_components()
    {
        var sensors = new[]
        {
            Cpu("Core (Tctl/Tdie)", 90.0),  // hotter, but not storage
            Drive("Temperature", 44.0),
        };

        Assert.Equal(44.0, SensorPoller.MaxDrive(sensors));
    }

    [Fact]
    public void MaxDrive_skips_null_drive_readings()
    {
        var sensors = new[]
        {
            Drive("Temperature", null),
            Drive("Temperature 2", 44.0),
        };

        Assert.Equal(44.0, SensorPoller.MaxDrive(sensors));
    }

    /// <summary>
    /// Regression lock: an empty storage set must NOT throw. The nullable
    /// <c>Max(s =&gt; s.Value)</c> overload returns <c>null</c> for an empty/all-null sequence — this
    /// is intended behaviour, not a bug to "fix".
    /// </summary>
    [Fact]
    public void MaxDrive_is_null_when_no_storage_sensors_and_does_not_throw()
    {
        var sensors = new[] { Cpu("Core (Tctl/Tdie)", 60.0), Gpu("GPU Core", 70.0) };

        var result = SensorPoller.MaxDrive(sensors);

        Assert.Null(result);
    }

    [Fact]
    public void MaxDrive_is_null_when_all_storage_readings_are_null()
    {
        var sensors = new[]
        {
            Drive("Temperature", null),
            Drive("Temperature 2", null),
        };

        Assert.Null(SensorPoller.MaxDrive(sensors));
    }

    [Fact]
    public void MaxDrive_is_null_on_empty_sensor_list()
    {
        Assert.Null(SensorPoller.MaxDrive(System.Array.Empty<SensorReading>()));
    }
}
