namespace TempMon.Core;

public enum TempLevel { None, Green, Amber, Red }

/// <summary>
/// Per-component temperature bands from the design. Below <c>Low</c> is green, below <c>High</c>
/// is amber, at/above <c>High</c> is red. A <c>null</c> reading is <see cref="TempLevel.None"/>.
/// The dashboard maps these levels to colours.
/// </summary>
public static class Thresholds
{
    private static readonly IReadOnlyDictionary<string, (double Low, double High)> Bands =
        new Dictionary<string, (double, double)>
        {
            [Component.Cpu] = (60, 80),
            [Component.Gpu] = (55, 75),
            [Component.Motherboard] = (50, 70),
            [Component.Storage] = (45, 60),
        };

    public static (double Low, double High)? For(string component) =>
        Bands.TryGetValue(component, out var band) ? band : null;

    public static TempLevel Level(string component, double? value)
    {
        if (value is null || For(component) is not { } band) return TempLevel.None;
        return value < band.Low ? TempLevel.Green
             : value < band.High ? TempLevel.Amber
             : TempLevel.Red;
    }
}
