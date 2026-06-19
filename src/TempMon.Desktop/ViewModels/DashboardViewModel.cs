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
