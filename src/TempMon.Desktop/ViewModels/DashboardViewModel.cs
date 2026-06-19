using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TempMon.Core;
using Component = TempMon.Core.Component;   // disambiguate from System.ComponentModel.Component

namespace TempMon.Desktop.ViewModels;

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private static readonly string[] Order =
        { Component.Cpu, Component.Gpu, Component.Motherboard, Component.Storage };

    public DashboardViewModel(bool elevated, int pollSeconds)
    {
        Elevated = elevated;
        PollSeconds = pollSeconds;
        // AutoStartEnabled is filled in by App after startup (querying it shells schtasks, so it's
        // done off the UI thread rather than in this constructor).
    }

    public bool Elevated { get; }
    public int PollSeconds { get; }

    public ObservableCollection<SummaryTileVM> SummaryTiles { get; } = new();
    public ObservableCollection<ComponentCardVM> Components { get; } = new();

    public Visibility BannerVisibility => Elevated ? Visibility.Collapsed : Visibility.Visible;
    public string ElevationText => Elevated ? "Administrator" : "Standard user — sensors disabled";
    public Brush ElevationDot => Elevated ? Palette.Green : Palette.Amber;
    public string PollText => $"Polling every {PollSeconds}s · updated {_updatedAgo}";

    private string _endpoint = "starting…";
    public string Endpoint
    {
        get => _endpoint;
        set { _endpoint = value; Raise(nameof(Endpoint)); }
    }

    private string _updatedAgo = "just now";

    // Stable tray scalars, derived from the CPU summary on every poll. The flyout binds these
    // directly instead of SummaryTiles[0], which is .Clear()'d and rebuilt each tick (flickers/throws).
    private string _cpuTrayText = "—";
    public string CpuTrayText
    {
        get => _cpuTrayText;
        private set { _cpuTrayText = value; Raise(nameof(CpuTrayText)); }
    }

    private Brush _cpuTrayBrush = Palette.None;
    public Brush CpuTrayBrush
    {
        get => _cpuTrayBrush;
        private set { _cpuTrayBrush = value; Raise(nameof(CpuTrayBrush)); }
    }

    private string _trayTooltip = "TempMon";
    public string TrayTooltip
    {
        get => _trayTooltip;
        private set { _trayTooltip = value; Raise(nameof(TrayTooltip)); }
    }

    private bool _autoStartEnabled;
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set { _autoStartEnabled = value; Raise(nameof(AutoStartEnabled)); }
    }

    public void Update(Snapshot snap)
    {
        SummaryTiles.Clear();
        SummaryTiles.Add(Tile("CPU", snap.Summary.CpuC, Component.Cpu));
        SummaryTiles.Add(Tile("GPU", snap.Summary.GpuC, Component.Gpu));
        SummaryTiles.Add(Tile("Max drive", snap.Summary.MaxDriveC, Component.Storage));

        Components.Clear();
        foreach (var component in Order)
        {
            var group = snap.Sensors.Where(s => s.Component == component).ToList();
            if (group.Count > 0)
                Components.Add(BuildCard(component, group));
        }

        // Single source of truth: Summary.CpuC + Thresholds + Palette, plus a tooltip string.
        double? cpu = snap.Summary.CpuC;
        CpuTrayText = Fmt(cpu);
        CpuTrayBrush = Palette.ForLevel(Thresholds.Level(Component.Cpu, cpu));
        TrayTooltip = cpu is null ? "TempMon — CPU —" : $"TempMon — CPU {Fmt(cpu)}°C";

        _updatedAgo = "just now";
        Raise(nameof(PollText));
    }

    private static SummaryTileVM Tile(string label, double? value, string component) =>
        new(label, Fmt(value), Palette.ForLevel(Thresholds.Level(component, value)));

    private static ComponentCardVM BuildCard(string component, List<SensorReading> group)
    {
        string tag = component switch
        {
            Component.Cpu => "CPU",
            Component.Gpu => "GPU",
            Component.Motherboard => "MB",
            Component.Storage => "SSD",
            _ => "?",
        };

        string device;
        List<SensorVM> sensors;

        if (component == Component.Storage)
        {
            // Each drive is its own LHM device; label sensors by drive, summarise the card by count.
            int drives = group.Select(s => s.Device).Distinct().Count();
            device = drives == 1 ? "1 drive" : $"{drives} drives";
            sensors = group
                .Select(s => MakeSensor(component,
                    IsGeneric(s.Name) ? s.Device : $"{s.Device} · {s.Name}", s))
                .ToList();
        }
        else
        {
            device = group[0].Device;
            sensors = group.Select(s => MakeSensor(component, s.Name, s)).ToList();
        }

        return new ComponentCardVM(tag, component, device, $"{sensors.Count} sensors", sensors);
    }

    private static SensorVM MakeSensor(string component, string label, SensorReading s)
    {
        var brush = Palette.ForLevel(Thresholds.Level(component, s.Value));
        double bar = s.Value is null ? 0 : Math.Clamp(s.Value.Value / 95.0 * 100.0, 5, 100);
        return new SensorVM(
            label,
            Fmt(s.Value),
            brush,
            new GridLength(bar, GridUnitType.Star),
            new GridLength(Math.Max(0, 100 - bar), GridUnitType.Star),
            "min " + Fmt(s.Min),
            "max " + Fmt(s.Max));
    }

    private static bool IsGeneric(string name) =>
        name.StartsWith("Temperature", StringComparison.OrdinalIgnoreCase);

    private static string Fmt(double? v) =>
        v is null ? "—" : v.Value.ToString("0.0", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SummaryTileVM(string Label, string Value, Brush Color);

public sealed record SensorVM(
    string Name, string Value, Brush Color,
    GridLength BarStar, GridLength RestStar, string Min, string Max);

public sealed record ComponentCardVM(
    string Tag, string Name, string Device, string CountText, IReadOnlyList<SensorVM> Sensors);

internal static class Palette
{
    public static readonly Brush Green = Frozen("#4CC38A");
    public static readonly Brush Amber = Frozen("#EAB54E");
    public static readonly Brush Red = Frozen("#F0616D");
    public static readonly Brush None = Frozen("#6E6E73");

    public static Brush ForLevel(TempLevel level) => level switch
    {
        TempLevel.Green => Green,
        TempLevel.Amber => Amber,
        TempLevel.Red => Red,
        _ => None,
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
