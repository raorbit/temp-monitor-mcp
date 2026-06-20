using TempMon.Core;
using Xunit;

namespace TempMon.Core.Tests;

/// <summary>
/// Locks the per-component temperature bands and the green/amber/red/none classification.
/// Bands (design): CPU 60/80, GPU 55/75, Motherboard 50/70, Storage 45/60. Below Low = Green,
/// below High = Amber, at/above High = Red, null reading = None.
/// </summary>
public class ThresholdsTests
{
    [Theory]
    [InlineData(Component.Cpu, 60, 80)]
    [InlineData(Component.Gpu, 55, 75)]
    [InlineData(Component.Motherboard, 50, 70)]
    [InlineData(Component.Storage, 45, 60)]
    public void For_returns_the_design_bands(string component, double low, double high)
    {
        var band = Thresholds.For(component);

        Assert.NotNull(band);
        Assert.Equal(low, band!.Value.Low);
        Assert.Equal(high, band.Value.High);
    }

    [Fact]
    public void For_unknown_component_is_null()
    {
        Assert.Null(Thresholds.For("Unknown"));
    }

    [Fact]
    public void Level_below_low_is_green()
    {
        Assert.Equal(TempLevel.Green, Thresholds.Level(Component.Cpu, 59.9));
    }

    [Fact]
    public void Level_at_low_is_amber()
    {
        // value < Low is green; value == Low is NOT < Low, so it is amber.
        Assert.Equal(TempLevel.Amber, Thresholds.Level(Component.Cpu, 60.0));
    }

    [Fact]
    public void Level_between_low_and_high_is_amber()
    {
        Assert.Equal(TempLevel.Amber, Thresholds.Level(Component.Cpu, 70.0));
    }

    [Fact]
    public void Level_just_below_high_is_amber()
    {
        Assert.Equal(TempLevel.Amber, Thresholds.Level(Component.Cpu, 79.9));
    }

    [Fact]
    public void Level_at_high_is_red()
    {
        // at/above High is red.
        Assert.Equal(TempLevel.Red, Thresholds.Level(Component.Cpu, 80.0));
    }

    [Fact]
    public void Level_above_high_is_red()
    {
        Assert.Equal(TempLevel.Red, Thresholds.Level(Component.Cpu, 95.0));
    }

    [Fact]
    public void Level_null_value_is_none()
    {
        Assert.Equal(TempLevel.None, Thresholds.Level(Component.Cpu, null));
    }

    [Fact]
    public void Level_unknown_component_is_none()
    {
        Assert.Equal(TempLevel.None, Thresholds.Level("Unknown", 100.0));
    }

    [Theory]
    [InlineData(Component.Gpu, 54.9, TempLevel.Green)]
    [InlineData(Component.Gpu, 55.0, TempLevel.Amber)]
    [InlineData(Component.Gpu, 74.9, TempLevel.Amber)]
    [InlineData(Component.Gpu, 75.0, TempLevel.Red)]
    [InlineData(Component.Storage, 44.9, TempLevel.Green)]
    [InlineData(Component.Storage, 45.0, TempLevel.Amber)]
    [InlineData(Component.Storage, 60.0, TempLevel.Red)]
    [InlineData(Component.Motherboard, 49.9, TempLevel.Green)]
    [InlineData(Component.Motherboard, 70.0, TempLevel.Red)]
    public void Level_classifies_each_component_at_its_band_edges(
        string component, double value, TempLevel expected)
    {
        Assert.Equal(expected, Thresholds.Level(component, value));
    }
}
