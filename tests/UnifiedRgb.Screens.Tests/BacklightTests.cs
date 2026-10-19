using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class BacklightTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(170)]
    [InlineData(255)]
    public void Native_backlight_packet_is_only_the_pinned_volatile_command_and_never_an_image_or_flash_write(int level)
    {
        var packet = TurzxBacklightProtocol.Build((byte)level, 0xfedcba98);
        packet.Should().HaveCount(512);
        var h = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet);
        h[0].Should().Be(14); h[8].Should().Be((byte)level);
        BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)).Should().Be(0xfedcba98);
        h.AsSpan(9).ToArray().Should().OnlyContain(v => v == 0);
        var ack = Ack(packet);
        TurzxBacklightProtocol.MatchesReply(ack, 0xfedcba98).Should().BeTrue();
        TurzxBacklightProtocol.MatchesReply(ack, 0xfedcba99).Should().BeFalse();
        ScreenProtocol.AllowedCommands(ScreenFamily.Turzx).Should().NotContain((byte)14);
    }

    [Fact]
    public void Recovery_latch_preserves_the_captured_wake_target_and_refuses_corrupt_state()
    {
        var dir = NewDirectory();
        try
        {
            var latch = new ScreenBacklightLatch(dir, Devices.Turzx.Id, 190); latch.Save(true);
            var reloaded = new ScreenBacklightLatch(dir, Devices.Turzx.Id, 100);
            reloaded.Off.Should().BeTrue(); reloaded.RestoreLevel.Should().Be(190);
            reloaded.Save(false);
            new ScreenBacklightLatch(dir, Devices.Turzx.Id, 100).RestoreLevel.Should().Be(100);
            File.WriteAllBytes(Directory.GetFiles(dir).Single(), [1, 1]);
            var bad = () => new ScreenBacklightLatch(dir, Devices.Turzx.Id, 190);
            bad.Should().Throw<InvalidDataException>();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blackout_closes_backlight_stops_frames_and_wakes_only_after_a_fresh_image(bool nack)
    {
        var dir = NewDirectory();
        var dark = false; var reject = nack;
        var opener = new FakeOpener { Configure = c => c.Reply = p => reject && Header(p)[0] == 14 ? [] : Ack(p) };
        var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false, TurzxRestoreBacklight = 190 };
        options.Turzx.Output = true;
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.Turzx), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using var manager = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => Volatile.Read(ref dark));
            var scene = new ScreenSceneDto([new(Devices.Turzx.Id, BackgroundColor: "#203040")]);
            await manager.SetScreenSceneAsync(scene);
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().FramesWritten > 0);
            var bytes = File.ReadAllBytes(Path.Combine(dir, "screens", "scene.json"));
            Volatile.Write(ref dark, true);
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == (nack ? ScreenStates.Error : ScreenStates.Sleeping));
            var controls = opener.Channels.SelectMany(c => c.Writes).Where(p => Header(p)[0] == 14).ToArray();
            controls.Should().ContainSingle(); Header(controls[0])[8].Should().Be(0);
            var written = opener.Channels.Sum(c => c.Writes.Count);
            await Task.Delay(300);
            opener.Channels.Sum(c => c.Writes.Count).Should().Be(written, "a sleeping or failed backlight must not keep streaming");
            reject = false; Volatile.Write(ref dark, false);
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == ScreenStates.Running);
            var wake = opener.Last.Writes.ToArray();
            Header(wake[0])[0].Should().Be(102, "the new picture precedes the native wake");
            Header(wake[1])[0].Should().Be(14); Header(wake[1])[8].Should().Be(190);
            File.ReadAllBytes(Path.Combine(dir, "screens", "scene.json")).Should().Equal(bytes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Restart_recovers_a_pending_wake_after_a_fresh_picture_without_modifying_scene_or_settings()
    {
        var dir = NewDirectory();
        var opener = new FakeOpener { Configure = c => c.Reply = Ack };
        var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false, TurzxRestoreBacklight = 190 }; options.Turzx.Output = true;
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.Turzx), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using (var sleeping = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => true))
            {
                await sleeping.SetScreenSceneAsync(new([new(Devices.Turzx.Id)]));
                await UntilAsync(async () => (await sleeping.ListScreensAsync()).Single().State == ScreenStates.Sleeping);
            }
            var oldWrites = opener.Channels.SelectMany(c => c.Writes).ToArray();
            oldWrites.Should().ContainSingle(); Header(oldWrites[0])[8].Should().Be(0, "disposing while asleep never wakes the screen");
            options.TurzxRestoreBacklight = 100;
            await using var awake = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => false);
            awake.Start();
            await UntilAsync(async () => (await awake.ListScreensAsync()).Single().State == ScreenStates.Running);
            var writes = opener.Last.Writes.ToArray(); Header(writes[0])[0].Should().Be(102); Header(writes[1])[8].Should().Be(190);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_failed_wake_state_save_does_not_repeatedly_issue_hardware_controls()
    {
        var dir = NewDirectory(); var dark = true;
        var opener = new FakeOpener { Configure = c => c.Reply = Ack };
        var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false }; options.Turzx.Output = true;
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.Turzx), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using var manager = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => Volatile.Read(ref dark));
            await manager.SetScreenSceneAsync(new([new(Devices.Turzx.Id)]));
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == ScreenStates.Sleeping);
            using var locked = new FileStream(Directory.GetFiles(Path.Combine(dir, "screens", "backlight")).Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
            Volatile.Write(ref dark, false);
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == ScreenStates.Error);
            var controls = opener.Channels.SelectMany(c => c.Writes).Count(p => Header(p)[0] == 14);
            controls.Should().Be(2, "one off and one acknowledged wake, whose state could not be saved");
            await Task.Delay(300);
            opener.Channels.SelectMany(c => c.Writes).Count(p => Header(p)[0] == 14).Should().Be(controls);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string NewDirectory() { var dir = Path.Combine(Path.GetTempPath(), "urgb-backlight-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    private static byte[] Header(byte[] packet) => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
    private static byte[] Ack(byte[] packet)
    {
        var h = Header(packet); var reply = new byte[512]; reply[0] = h[0]; reply[1] = 200;
        h.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 150; i++) { if (await condition()) return; await Task.Delay(20); }
        (await condition()).Should().BeTrue();
    }
}
