using System.Globalization;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

// The Core test project reaches the internal sensor-selection helpers (RepresentativeCpu /
// RepresentativeGpu / MaxDrive) to lock the summary rules without standing up a real Computer.
[assembly: InternalsVisibleTo("TempMon.Core.Tests")]

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
    private readonly bool _elevated;
    private bool _opened;
    private bool _disposed;

    // The desktop already knows whether it's elevated (ElevationHelper) and hands it in, so Core stays
    // free of a System.Security.Principal dependency and behaves deterministically on un-elevated CI.
    public SensorPoller(bool elevated)
    {
        _elevated = elevated;
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

        // CPU is privilege-gated: un-elevated, its WinRing0-backed sensors read a real-looking 0 rather
        // than null, so report cpu_c as null (not a misleading 0) and flag the headline as unreadable.
        var summary = new Summary(
            CpuC: _elevated ? RepresentativeCpu(sensors) : null,
            GpuC: RepresentativeGpu(sensors),
            MaxDriveC: MaxDrive(sensors),
            CpuReadable: _elevated);

        return new Snapshot(Snapshot.CurrentSchemaVersion, _opened, Now(), Environment.MachineName, summary, sensors);
    }

    /// <summary>Recursively gathers temperature sensors, carrying the top-level component/device
    /// label down to sub-hardware (e.g. a motherboard's SuperIO chip). Instance (not static) so it can
    /// stamp each reading's <c>readable</c> flag from <see cref="_elevated"/>.</summary>
    private void Collect(IHardware hw, List<SensorReading> output, string? component, string? device)
    {
        component ??= MapComponent(hw.HardwareType);
        device ??= hw.Name;

        if (component is not null)
        {
            // A privilege-gated subsystem (CPU/Motherboard) read while un-elevated is untrustworthy —
            // mark it so the MCP layer reports "unreadable" instead of trusting a real-looking 0.
            bool readable = _elevated || !IsPrivilegeGated(component);
            foreach (ISensor sensor in hw.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;
                output.Add(new SensorReading(
                    component, device, sensor.Name,
                    Round(sensor.Value), Round(sensor.Min), Round(sensor.Max), readable));
            }
        }

        foreach (IHardware sub in hw.SubHardware)
            Collect(sub, output, component, device);
    }

    /// <summary>CPU and Motherboard temps arrive over the WinRing0/SuperIO path, which only loads when
    /// elevated; GPU (NVML) and Storage (SMART) read fine un-elevated. So only these two can go dark —
    /// and silently return a plausible 0 — without administrator rights.</summary>
    internal static bool IsPrivilegeGated(string component) =>
        component is Component.Cpu or Component.Motherboard;

    private static string? MapComponent(HardwareType type) => type switch
    {
        HardwareType.Cpu => Component.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => Component.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO => Component.Motherboard,
        HardwareType.Storage => Component.Storage,
        _ => null,
    };

    // internal (not private) so the Core test project can lock the sensor-selection rules without
    // standing up a real Computer — see the InternalsVisibleTo("TempMon.Core.Tests") at file top.
    internal static double? RepresentativeCpu(IReadOnlyList<SensorReading> s)
    {
        static bool Cpu(SensorReading x) => x.Component == Component.Cpu && x.Value is not null;
        return s.FirstOrDefault(x => Cpu(x) && Has(x, "Tctl"))?.Value
            ?? s.FirstOrDefault(x => Cpu(x) && Has(x, "Package"))?.Value
            ?? s.FirstOrDefault(x => Cpu(x) && Has(x, "Core"))?.Value
            ?? s.FirstOrDefault(Cpu)?.Value;
    }

    internal static double? RepresentativeGpu(IReadOnlyList<SensorReading> s)
    {
        static bool Gpu(SensorReading x) => x.Component == Component.Gpu && x.Value is not null;
        return s.FirstOrDefault(x => Gpu(x) && Has(x, "Core") && !Has(x, "Hot"))?.Value
            ?? s.FirstOrDefault(x => Gpu(x) && Has(x, "Core"))?.Value
            ?? s.FirstOrDefault(Gpu)?.Value;
    }

    /// <summary>The hottest storage reading, or <c>null</c> when no drive reported a value. The
    /// nullable <see cref="Enumerable.Max(IEnumerable{double?})"/> overload skips null entries and
    /// returns <c>null</c> for an empty sequence — so this never throws on a box with no storage
    /// sensors.</summary>
    internal static double? MaxDrive(IReadOnlyList<SensorReading> s) =>
        s.Where(x => x.Component == Component.Storage).Max(x => x.Value);

    private static bool Has(SensorReading s, string token) =>
        s.Name.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static double? Round(float? v) => v is null ? null : Math.Round((double)v.Value, 1);

    private static string Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // Instance (not static) so it can stamp SensorsAvailable from _opened: the ctor calls Empty()
    // before _computer.Open() while _opened is still false, and Poll() early-returns on !_opened, so
    // only a healthy Build() ever publishes a snapshot with SensorsAvailable == true.
    private Snapshot Empty() =>
        new(Snapshot.CurrentSchemaVersion, _opened, Now(), Environment.MachineName, new Summary(null, null, null, _elevated), Array.Empty<SensorReading>());

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
