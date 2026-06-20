using System.Collections.Generic;
using System.Linq;
using TempMon.Core;
using TempMon.Desktop.ViewModels;
using Xunit;

namespace TempMon.Desktop.Tests;

/// <summary>
/// Storage renders one card per physical drive, each listing all of that drive's temperature sensors
/// (an NVMe drive reports a composite + Temperature 1/2) — so a multi-sensor drive is a single card,
/// not several look-alikes. Other components stay one card per sensor.
/// </summary>
public sealed class DashboardCardTests
{
    private static SensorReading Drive(string device, string name, double v) =>
        new(Component.Storage, device, name, v, v, v);

    [Fact]
    public void Storage_renders_one_card_per_drive_with_all_its_readings()
    {
        var group = new List<SensorReading>
        {
            Drive("Samsung SSD 990 EVO 2TB", "Temperature", 54),
            Drive("Samsung SSD 990 EVO 2TB", "Temperature 1", 68),
            Drive("Samsung SSD 990 EVO 2TB", "Temperature 2", 54),
            Drive("ST4000VN000-1H4168", "Temperature", 37),
        };

        var card = DashboardViewModel.BuildCard(Component.Storage, group);
        var drives = card.Items.OfType<DriveCardVM>().ToList();

        Assert.Equal(2, drives.Count);              // one card per physical drive
        Assert.Equal("2 drives", card.Device);
        Assert.Equal("4 sensors", card.CountText);  // header still counts every sensor

        var nvme = drives.Single(d => d.Device == "Samsung SSD 990 EVO 2TB");
        Assert.Equal(new[] { "Temperature", "Temperature 1", "Temperature 2" },
            nvme.Readings.Select(r => r.Name).ToArray());   // all three live in the one card
        Assert.Equal("68.0", nvme.Headline);                // headline = the hottest reading

        // A single-sensor drive needs no breakdown list — the headline already shows its one reading.
        var hdd = drives.Single(d => d.Device == "ST4000VN000-1H4168");
        Assert.Empty(hdd.Readings);
        Assert.Equal("37.0", hdd.Headline);
    }

    [Fact]
    public void Non_storage_components_render_one_sensor_card_each()
    {
        var group = new List<SensorReading>
        {
            new(Component.Cpu, "Ryzen 9", "Tctl/Tdie", 45, 30, 80),
            new(Component.Cpu, "Ryzen 9", "CCD1", 40, 30, 80),
        };

        var card = DashboardViewModel.BuildCard(Component.Cpu, group);

        Assert.Equal(2, card.Items.OfType<SensorVM>().Count());
        Assert.Empty(card.Items.OfType<DriveCardVM>());
    }
}
