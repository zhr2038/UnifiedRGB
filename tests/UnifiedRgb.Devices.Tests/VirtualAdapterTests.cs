using UnifiedRgb.Devices.Virtual;

namespace UnifiedRgb.Devices.Tests;

public class VirtualAdapterTests
{
    [Fact]
    public void Catalog_matches_the_plan()
    {
        var c = VirtualCatalog.Default;
        c.Should().HaveCount(9);
        c.Count(d => d.Zones.Count == 1 && d.Zones[0].Layout.Kind == LayoutKind.Ring).Should().Be(4);
        c.Should().ContainSingle(d => d.DeviceType == DeviceType.Ledstrip);
        c.Single(d => d.DeviceType == DeviceType.Gpu).Zones.Single().Layout.Should().Be(ZoneLayout.Point);
        c.Where(d => d.DeviceType == DeviceType.Dram).Should().HaveCount(2).And.OnlyContain(d => d.Zones.Single().Layout.LedCount == 8);
        c.Single(d => d.DeviceType == DeviceType.Motherboard).Zones.Select(z => z.Layout.LedCount).Should().Equal(2, 20, 20, 20);
        c.Should().OnlyContain(d => d.Class == DeviceClass.Virtual && d.Capabilities.MaxFps == 60);
    }

    [Fact]
    public async Task Writes_need_open_and_exact_length()
    {
        var a = new VirtualAdapter();
        var gpu = new ControllerId("virtual:gpu");
        var early = () => a.WriteFrame(gpu, new ZoneId(0), [Rgb.White]);
        early.Should().Throw<InvalidOperationException>();
        await a.OpenAsync(gpu, CancellationToken.None);
        var wrong = () => a.WriteFrame(gpu, new ZoneId(0), [Rgb.White, Rgb.White]);
        wrong.Should().Throw<ArgumentException>();
        a.WriteFrame(gpu, new ZoneId(0), [new Rgb(1, 2, 3)]);
        a.Snapshot(gpu, new ZoneId(0)).Should().Equal(new Rgb(1, 2, 3));
        a.WriteCount(gpu).Should().Be(1);
        var unknown = () => a.OpenAsync(new ControllerId("virtual:nope"), CancellationToken.None);
        await unknown.Should().ThrowAsync<ArgumentException>();
    }
}
