using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace TempMon.Core;

/// <summary>
/// The single hardware reader. Owns one <see cref="Computer"/>, serialises every read behind a
/// lock (LHM drives the WinRing0 driver under a global lock — only one reader may exist), and
/// publishes the result as an immutable <see cref="Snapshot"/> via a volatile reference swap.
///
/// <para>The HTTP layer reads <see cref="Latest"/> (lock-free) and never calls <see cref="Poll"/>,
/// so an MCP request can never trigger a hardware read.</para>
/// </summary>
public sealed class SensorPoller : IDisposable
{
    private readonly object _gate = new();
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private volatile Snapshot _latest;
    private bool _opened;
    private bool _disposed;

    public SensorPoller()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,          // covers NVIDIA, AMD and Intel in current LHM
            IsMotherboardEnabled = true,  // motherboard temps arrive via SuperIO sub-hardware
            IsStorageEnabled = true,
        };
        _latest = Empty();

        try
        {
            _computer.Open();
            _opened = true;
        }
        catch
        {
            // Driver load fails when not elevated — leave the empty snapshot in place; Poll() is a
            // no-op until a (re)start with rights. The desktop surfaces this via the elevation banner.
            _opened = false;
        }
    }

    /// <summary>The last published snapshot. Lock-free — returns whatever reference Poll last swapped in.</summary>
    public Snapshot Latest => _latest;

    /// <summary>
    /// Refreshes every sensor once under the lock and publishes a fresh snapshot. Called only by
    /// the owning elevated process (e.g. on a timer); never re-entered from the HTTP path.
    /// </summary>
    public Snapshot Poll()
    {
        lock (_gate)
        {
            if (_disposed || !_opened) return _latest;

            _computer.Accept(_visitor);
            var snapshot = Build();
            _latest = snapshot;   // volatile reference swap — readers see all-or-nothing
            return snapshot;
        }
    }

    private Snapshot Build()
    {
        var sensors = new List<SensorReading>();
        foreach (IHardware hw in _computer.Hardware)
            Collect(hw, sensors, component: null, device: null);

        var summary = new Summary(
            CpuC: RepresentativeCpu(sensors),
            GpuC: RepresentativeGpu(sensors),
            MaxDriveC: sensors.Where(s => s.Component == Component.Storage).Max(s => s.Value));

        return new Snapshot(Now(), Environment.MachineName, summary, sensors);
    }

    /// <summary>Recursively gathers temperature sensors, carrying the top-level component/device
    /// label down to sub-hardware (e.g. a motherboard's SuperIO chip).</summary>
    private static void Collect(IHardware hw, List<SensorReading> output, string? component, string? device)
    {
        component ??= MapComponent(hw.HardwareType);
        device ??= hw.Name;

        if (component is not null)
        {
            foreach (ISensor sensor in hw.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;
                output.Add(new SensorReading(
                    component, device, sensor.Name,
                    Round(sensor.Value), Round(sensor.Min), Round(sensor.Max)));
            }
        }

        foreach (IHardware sub in hw.SubHardware)
            Collect(sub, output, component, device);
    }

    private static string? MapComponent(HardwareType type) => type switch
    {
        HardwareType.Cpu => Component.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => Component.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO => Component.Motherboard,
        HardwareType.Storage => Component.Storage,
        _ => null,
    };

    private static double? RepresentativeCpu(IReadOnlyList<SensorReading> s)
    {
        static bool Cpu(SensorReading x) => x.Component == Component.Cpu && x.Value is not null;
        return s.FirstOrDefault(x => Cpu(x) && Has(x, "Tctl"))?.Value
            ?? s.FirstOrDefault(x => Cpu(x) && Has(x, "Package"))?.Value
            ?? s.FirstOrDefault(x => Cpu(x) && Has(x, "Core"))?.Value
            ?? s.FirstOrDefault(Cpu)?.Value;
    }

    private static double? RepresentativeGpu(IReadOnlyList<SensorReading> s)
    {
        static bool Gpu(SensorReading x) => x.Component == Component.Gpu && x.Value is not null;
        return s.FirstOrDefault(x => Gpu(x) && Has(x, "Core") && !Has(x, "Hot"))?.Value
            ?? s.FirstOrDefault(x => Gpu(x) && Has(x, "Core"))?.Value
            ?? s.FirstOrDefault(Gpu)?.Value;
    }

    private static bool Has(SensorReading s, string token) =>
        s.Name.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static double? Round(float? v) => v is null ? null : Math.Round((double)v.Value, 1);

    private static string Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static Snapshot Empty() =>
        new(Now(), Environment.MachineName, new Summary(null, null, null), Array.Empty<SensorReading>());

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_opened) _computer.Close();
        }
    }
}
