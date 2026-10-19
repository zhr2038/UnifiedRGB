using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

public sealed class WorkerRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exited_worker_is_replaced_with_the_same_scene_and_repeated_failure_is_bounded(bool permanent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-worker-tests", Guid.NewGuid().ToString("N"));
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var probe = new FailingProbe(permanent);
        var opener = new FakeOpener();
        var device = ScreenDeviceInfo.FromPath(Devices.PathOf("0006", "audit-recovery"))!;
        var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false, TlLcd = new ScreenFamilyOptions { Output = true } };
        try
        {
            await using var manager = new ScreenManager(options, dir, new ScreenPlatform(new FakeEnumerator(device), opener, probe, new FakeOs(), null, time));
            var scene = new ScreenSceneDto([new(device.Id, Overlay: "none", Rotation: 180, Brightness: 0.4)]);
            await manager.SetScreenSceneAsync(scene);
            _ = await manager.ListScreensAsync();
            for (var i = 0; i < 100 && Volatile.Read(ref probe.Calls) == 0; i++) await Task.Delay(10);
            await Task.Delay(20); // let the exited thread finish its cleanup
            _ = await manager.ListScreensAsync();
            for (var i = 0; i < 6; i++)
            {
                time.Advance(TimeSpan.FromSeconds(10));
                _ = await manager.ListScreensAsync();
                await Task.Delay(35);
            }
            (await manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
            if (permanent)
            {
                probe.Calls.Should().Be(4, "one original worker and three bounded replacements");
                opener.Opens.Should().Be(0);
                (await manager.ListScreensAsync()).Single().State.Should().Be(ScreenStates.Error);
            }
            else
            {
                for (var i = 0; i < 100 && opener.Opens == 0; i++) { await Task.Delay(10); _ = await manager.ListScreensAsync(); }
                opener.Opens.Should().Be(1);
                (await manager.ListScreensAsync()).Single().State.Should().Be(ScreenStates.Running);
            }
            await manager.StopScreensAsync();
            var before = probe.Calls;
            time.Advance(TimeSpan.FromMinutes(1));
            _ = await manager.ListScreensAsync();
            probe.Calls.Should().Be(before, "a pause must not be bypassed by recovery");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class FailingProbe(bool permanent) : IScreenContentionProbe
    {
        public int Calls;
        public string? BlockedBy(ScreenFamily family)
        {
            var n = Interlocked.Increment(ref Calls);
            if (permanent || n == 1) throw new InvalidOperationException("injected transient scan failure");
            return null;
        }
    }
}
