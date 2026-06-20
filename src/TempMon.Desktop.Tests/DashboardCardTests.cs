using System.Collections.Generic;
using System.Linq;
using TempMon.Core;
using TempMon.Desktop.ViewModels;
using Xunit;

namespace TempMon.Desktop.Tests;

/// <summary>
/// Storage cards label each sensor by its drive. A drive that exposes several temperature sensors
/// (an NVMe composite + Temperature 1/2) must keep the sensor name so its readings don't render as
/// indistinguishable duplicate drives — while a drive with a single generic sensor stays clean.
/// </summary>
public sealed class DashboardCardTests
{
    private static SensorReading Drive(string device, string name, double v) =>
        new(Component.Storage, device, name, v, v, v);

    [Fact]
    public void Multi_sensor_drive_keeps_sensor_names_single_sensor_drive_stays_clean()
    {
        var group = new List<SensorReading>
        {
            Drive("Samsung SSD 990 EVO 2TB", "Temperature", 54),
            Drive("Samsung SSD 990 EVO 2TB", "Temperature 1", 68),
            Drive("Samsung SSD 990 EVO 2TB", "Temperature 2", 54),
            Drive("ST4000VN000-1H4168", "Temperature", 37),
        };

        var card = DashboardViewModel.BuildCard(Component.Storage, group);
        var labels = card.Sensors.Select(s => s.Name).ToList();

        // The NVMe drive's three sensors are now distinguishable instead of three identical cards.
        Assert.Contains("Samsung SSD 990 EVO 2TB · Temperature", labels);
        Assert.Contains("Samsung SSD 990 EVO 2TB · Temperature 1", labels);
        Assert.Contains("Samsung SSD 990 EVO 2TB · Temperature 2", labels);

        // A drive with a single generic sensor keeps the clean drive-only label.
        Assert.Contains("ST4000VN000-1H4168", labels);
        Assert.DoesNotContain("ST4000VN000-1H4168 · Temperature", labels);

        Assert.Equal("2 drives", card.Device);
        Assert.Equal("4 sensors", card.CountText);
    }
}
