using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

public sealed class PeripheralHealthCountTests
{
    [Fact]
    public void Metadata_receivers_are_excluded_but_an_offline_keyboard_remains_in_the_denominator()
    {
        using var studio = new StudioViewModel(new FakeConnection(new RecordingRgbService()), InlineDispatcher.Instance, _ => { });
        static DeviceStatusDto Device(string id, string state) => new(id, id, state, null, 0, 10, 0, 0, 0, null);
        var devices = new[] { Device("asusr:one:receiver", "Blocked"), Device("asusr:two:receiver", "Blocked"),
            Device("enedram:p0", "Blocked"), Device("asusr:two:3", "Active"), Device("asusr:wired:keyboard", "Active") };
        var status = new StatusDto(DateTimeOffset.UtcNow, "test", 0, 0, new(60, 60, 0, 0, 0, 0, 0), devices);
        studio.UpdateStatus(status); studio.Health.Should().Be("2 / 2 台灯光控制器运行中");
        devices[4] = devices[4] with { State = "Offline" };
        studio.UpdateStatus(status); studio.Health.Should().Be("1 / 2 台灯光控制器运行中");
    }
}
