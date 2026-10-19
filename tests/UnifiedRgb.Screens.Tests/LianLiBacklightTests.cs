using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class LianLiBacklightTests
{
    [Theory]
    [InlineData(ScreenFamily.TlLcd, 0)]
    [InlineData(ScreenFamily.TlLcd, 100)]
    [InlineData(ScreenFamily.H2, 0)]
    [InlineData(ScreenFamily.H2, 100)]
    public void Native_LianLi_control_has_family_specific_padding_and_no_other_parameters(ScreenFamily family, int level)
    {
        var packet = LianLiBacklightProtocol.Build(family, (byte)level, 0xfedcba98);
        packet.Should().HaveCount(512);
        var h = ScreenProtocol.DecryptHeader(family, packet);
        h[0].Should().Be(14); h[1].Should().Be(0); h[2].Should().Be(0x1a); h[3].Should().Be(0x6d);
        BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)).Should().Be(0xfedcba98);
        h[8].Should().Be((byte)level); h.AsSpan(9).ToArray().Should().OnlyContain(v => v == 0);
        ScreenProtocol.AllowedCommands(family).Should().NotContain((byte)14);
    }

    [Fact]
    public void LianLi_rejects_wrong_family_or_percentage_instead_of_clamping_or_touching_other_controls()
    {
        var invalidFamily = () => LianLiBacklightProtocol.Build(ScreenFamily.Turzx, 0, 1);
        var invalidLevel = () => LianLiBacklightProtocol.Build(ScreenFamily.H2, 101, 1);
        invalidFamily.Should().Throw<ArgumentOutOfRangeException>(); invalidLevel.Should().Throw<ArgumentOutOfRangeException>();
        new ScreensOptions { H2RestoreBacklight = 101 }.Invoking(o => o.RestoreBacklightOf(ScreenFamily.H2)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(ScreenFamily.TlLcd, false)]
    [InlineData(ScreenFamily.H2, false)]
    public async Task LCD_sleep_survives_restart_and_wakes_after_new_content_with_captured_target(ScreenFamily family, bool silent)
    {
        var device = family == ScreenFamily.TlLcd ? Devices.Tl1 : Devices.H2;
        var dir = NewDirectory();
        var opener = new FakeOpener { Configure = c => { c.ConsumeReply = true; c.Reply = p => silent ? [] : Ack(family, p); } };
        var options = Options(); options.H2RestoreBacklight = 78; options.TlLcdRestoreBacklight = 78;
        var platform = new ScreenPlatform(new FakeEnumerator(device), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using (var sleeping = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => true))
            {
                await sleeping.SetScreenSceneAsync(new([new(device.Id)]));
                await UntilAsync(async () => (await sleeping.ListScreensAsync()).Single().State == ScreenStates.Sleeping);
                var writes = opener.Channels.SelectMany(c => c.Writes).ToArray(); writes.Should().ContainSingle();
                Header(family, writes[0])[8].Should().Be(0);
                await Task.Delay(300); opener.Channels.Sum(c => c.Writes.Count).Should().Be(1);
            }
            options.H2RestoreBacklight = 40; options.TlLcdRestoreBacklight = 40;
            await using var awake = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => false);
            awake.Start();
            await UntilAsync(async () => (await awake.ListScreensAsync()).Single().State == ScreenStates.Running);
            var wake = opener.Last.Writes.ToArray();
            if (family == ScreenFamily.H2) Header(family, wake[0])[0].Should().Be(102, "clear the independent PNG layer while still dark");
            Header(family, wake[^2])[0].Should().Be(101); Header(family, wake[^1])[0].Should().Be(14);
            Header(family, wake[^1])[8].Should().Be(78);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_or_stale_TL_control_reply_is_an_error_without_repeated_writes(bool stale)
    {
        var dir = NewDirectory();
        var opener = new FakeOpener { Configure = c => { c.ConsumeReply = true; c.Reply = p =>
        {
            if (!stale) return [];
            var reply = Ack(ScreenFamily.TlLcd, p); reply[2] ^= 1; return reply;
        }; } };
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.Tl1), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using var manager = new ScreenManager(Options(), dir, platform, wholeCaseBlackout: () => true);
            await manager.SetScreenSceneAsync(new([new(Devices.Tl1.Id)]));
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == ScreenStates.Error);
            var writes = opener.Channels.Sum(c => c.Writes.Count); writes.Should().Be(1);
            await Task.Delay(300); opener.Channels.Sum(c => c.Writes.Count).Should().Be(writes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TL_control_consumes_stale_image_status_but_never_accepts_an_unbounded_reply_queue(bool busy)
    {
        var dir = NewDirectory();
        var opener = new FakeOpener { Configure = c =>
        {
            var reads = 0;
            c.Reply = p =>
            {
                reads++;
                if (!busy && reads > 2) return [];
                var reply = Ack(ScreenFamily.TlLcd, p);
                if (busy || reads == 1) reply[0] = 101; // old content reply, not a brightness response
                return reply;
            };
        } };
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.Tl1), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using var manager = new ScreenManager(Options(), dir, platform, wholeCaseBlackout: () => true);
            await manager.SetScreenSceneAsync(new([new(Devices.Tl1.Id)]));
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == (busy ? ScreenStates.Error : ScreenStates.Sleeping));
            opener.Channels.SelectMany(c => c.Writes).Should().ContainSingle();
            opener.Last.Reads.Should().BeLessThanOrEqualTo(65, "reply cleanup is bounded and never resends the control");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_missing_fresh_image_ACK_never_wakes_H2()
    {
        var dir = NewDirectory(); var dark = true;
        var opener = new FakeOpener { Configure = c => c.Reply = p => Header(ScreenFamily.H2, p)[0] == 14 ? Ack(ScreenFamily.H2, p) : [] };
        var options = Options();
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.H2), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using var manager = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => Volatile.Read(ref dark));
            await manager.SetScreenSceneAsync(new([new(Devices.H2.Id)]));
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == ScreenStates.Sleeping);
            Volatile.Write(ref dark, false);
            await UntilAsync(async () => (await manager.ListScreensAsync()).Single().State == ScreenStates.Error);
            opener.Channels.SelectMany(c => c.Writes).Where(p => Header(ScreenFamily.H2, p)[0] == 14).Should().ContainSingle();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Output_gates_prevent_even_blackout_from_opening_disabled_families()
    {
        var dir = NewDirectory(); var options = Options(); options.TlLcd.Output = false; options.H2.Output = false;
        var opener = new FakeOpener();
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.Tl1, Devices.H2), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        try
        {
            await using var manager = new ScreenManager(options, dir, platform, wholeCaseBlackout: () => true);
            await manager.SetScreenSceneAsync(new([new(Devices.Tl1.Id), new(Devices.H2.Id)]));
            await Task.Delay(350); opener.Opens.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static ScreensOptions Options()
    {
        var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false }; options.TlLcd.Output = true; options.H2.Output = true; return options;
    }
    private static string NewDirectory() { var dir = Path.Combine(Path.GetTempPath(), "urgb-lianli-backlight-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    private static byte[] Header(ScreenFamily family, byte[] packet) => ScreenProtocol.DecryptHeader(family, packet.AsSpan(0, 512));
    private static byte[] Ack(ScreenFamily family, byte[] packet)
    {
        var h = Header(family, packet); var reply = new byte[512]; reply[0] = h[0]; reply[1] = 200; h.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 150; i++) { if (await condition()) return; await Task.Delay(20); }
        (await condition()).Should().BeTrue();
    }
}
