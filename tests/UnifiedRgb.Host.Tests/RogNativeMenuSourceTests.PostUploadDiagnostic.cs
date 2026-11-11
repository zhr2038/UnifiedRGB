using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData((byte)1, false)] [InlineData((byte)2, false)]
    [InlineData((byte)1, true)] [InlineData((byte)2, true)]
    public async Task Diagnostic_one_post_pixels_CAP_precedes_first_renewal_and_READY_is_independent_of_neutral(byte transport, bool neutral)
    {
        await using var rig = new Rig(transport); await rig.Actor.DisposeAsync();
        rig.Io.DedicatedPage = true; rig.Io.Mode = 10;
        var source = new RogNativeMenuSource(Policy(transport) with { DiagnoseCodexPostUploadCapability = true }, rig.Input, rig.Input, rig.Time, () => Cookie);
        source.RequestCodexEnabled(true);
        using var link = new PostUploadDiagnosticConnection(rig.Io) { Neutral = neutral };
        await using var actor = source.Attach(link);
        (await actor.StepAsync(Pixels, default)).Should().BeTrue();
        link.PostUploadCaps.Should().Be(1); link.Renewals.Should().Be(1);
        var packets = rig.Io.Packets;
        var lastPixel = packets.FindLastIndex(p => p[1] == 0x67 && p[2] == 1);
        packets[lastPixel + 1].Should().Equal(RogNativeMenuCodec.CapabilityRequest());
        packets[lastPixel + 2].Should().Equal(RogWiredOledProtocol.Renew());
        rig.Io.Rebuilt.Should().HaveCount(6656);
    }

    [Theory]
    [InlineData(false, (byte)10)] [InlineData(true, (byte)9)]
    public async Task Diagnostic_option_only_controls_logging_and_Mode10_always_has_the_commit_CAP(bool enabled, byte mode)
    {
        await using var rig = new Rig(1); await rig.Actor.DisposeAsync(); rig.Io.DedicatedPage = true; rig.Io.Mode = mode;
        var source = new RogNativeMenuSource(Policy() with { DiagnoseCodexPostUploadCapability = enabled }, rig.Input, rig.Input, rig.Time, () => Cookie);
        source.RequestCodexEnabled(true); source.RequestEnabled(true);
        using var link = new PostUploadDiagnosticConnection(rig.Io);
        await using var actor = source.Attach(link);
        (await actor.StepAsync(Pixels, default)).Should().BeTrue();
        link.PostUploadCaps.Should().Be(mode == 10 ? 1 : 0); link.Renewals.Should().Be(1);
        rig.Io.Packets.Count(p => p[1] == 0x12 && p[2] == 0x7D).Should().Be(mode == 10 ? 2 : 1);
    }

    [Theory]
    [InlineData("no-ready", false)] [InlineData("no-ready", true)] [InlineData("rx", false)] [InlineData("rx", true)]
    [InlineData("epoch", false)] [InlineData("epoch", true)] [InlineData("cookie", false)] [InlineData("cookie", true)]
    [InlineData("profile", false)] [InlineData("profile", true)] [InlineData("mode", false)] [InlineData("mode", true)]
    [InlineData("transport", false)] [InlineData("transport", true)] [InlineData("timeout", false)] [InlineData("timeout", true)]
    public async Task Diagnostic_invalid_post_upload_state_or_read_failure_retires_the_current_owner_before_any_renewal(string fault, bool verbose)
    {
        await using var rig = new Rig(1); await rig.Actor.DisposeAsync(); rig.Io.DedicatedPage = true; rig.Io.Mode = 10;
        var source = new RogNativeMenuSource(Policy() with { DiagnoseCodexPostUploadCapability = verbose }, rig.Input, rig.Input, rig.Time, () => Cookie);
        source.RequestCodexEnabled(true);
        using var link = new PostUploadDiagnosticConnection(rig.Io) { Fault = fault };
        await using var actor = source.Attach(link);
        var step = () => actor.StepAsync(Pixels, default);
        var transportTimeout = fault == "timeout";
        if (transportTimeout)
            await step.Should().ThrowAsync<RogNativeArtworkUploadRefusedException>()
                .WithMessage("*operation=post-frame-capability*old owner must be replaced*");
        else
            await step.Should().ThrowAsync<IOException>()
                .WithMessage("*operation=CodexFrameReady*terminalAfterPixelWrites*renewalNotSent*noRetry*");
        link.PostUploadCaps.Should().Be(1); link.Renewals.Should().Be(0);
        actor.RecoveryBlocked.Should().Be(!transportTimeout);
        actor.Armed.Should().BeFalse(); source.IsPresent.Should().BeFalse();
        var writes = rig.Io.Packets.Count;
        await step.Should().ThrowAsync<IOException>("the old owner cannot send anything after the failure");
        rig.Io.Packets.Should().HaveCount(writes);
    }

    [Theory]
    [InlineData((byte)1, false)] [InlineData((byte)1, true)] [InlineData((byte)2, false)] [InlineData((byte)2, true)]
    public async Task Diagnostic_commit_CAP_is_required_after_every_replacement_Codex_frame_without_extra_ARM(byte transport, bool verbose)
    {
        await using var rig = new Rig(transport); await rig.Actor.DisposeAsync(); rig.Io.DedicatedPage = true; rig.Io.Mode = 10;
        var source = new RogNativeMenuSource(Policy(transport) with { DiagnoseCodexPostUploadCapability = verbose }, rig.Input, rig.Input, rig.Time, () => Cookie);
        source.RequestCodexEnabled(true);
        using var link = new PostUploadDiagnosticConnection(rig.Io);
        await using var actor = source.Attach(link);
        (await actor.StepAsync(Pixels, default)).Should().BeTrue();
        link.PostUploadCaps.Should().Be(1);
        rig.Time.Advance(TimeSpan.FromMilliseconds(101));
        var second = Pixels(); second[0] ^= 1;
        (await actor.StepAsync(() => second, default)).Should().BeTrue();
        link.PostUploadCaps.Should().Be(2, "each complete frame must establish its own readiness before renewal, regardless of verbose logging");
        link.Renewals.Should().Be(2);
        rig.Io.Arms.Should().Be(1);
        rig.Io.Packets.Count(p => p[1] == 0x12 && p[2] == 0x7D).Should().Be(4, "two ordinary CAPs plus one post-upload commit CAP for each frame");
    }

    [Fact]
    public async Task Diagnostic_stable_snapshot_is_included_if_the_following_first_renewal_fails()
    {
        await using var rig = new Rig(1); await rig.Actor.DisposeAsync(); rig.Io.DedicatedPage = true; rig.Io.Mode = 10;
        var source = new RogNativeMenuSource(Policy() with { DiagnoseCodexPostUploadCapability = true }, rig.Input, rig.Input, rig.Time, () => Cookie);
        source.RequestCodexEnabled(true);
        using var link = new PostUploadDiagnosticConnection(rig.Io) { Fault = "renew" };
        await using var actor = source.Attach(link);
        await ((Func<Task>)(() => actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>()
            .WithMessage("*first renewal failed*armed=[mode=10*observed=[mode=10*ready=True*00FFAA*");
        link.PostUploadCaps.Should().Be(1); link.Renewals.Should().Be(1); actor.RecoveryBlocked.Should().BeTrue();
    }

    private sealed class PostUploadDiagnosticConnection(Connection inner) : IRogOledConnection
    {
        public bool Neutral { get; init; }
        public string? Fault { get; init; }
        public int PostUploadCaps { get; private set; }
        public int Renewals { get; private set; }
        public string DeviceKey => inner.DeviceKey;
        public string FirmwareHeader => inner.FirmwareHeader;
        public string ConnectionKind => inner.ConnectionKind;
        public int PixelPayloadCapacity => inner.PixelPayloadCapacity;
        public RogNativeControlBinding? NativeControlBinding => inner.NativeControlBinding;
        public bool SameDevice() => inner.SameDevice();
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) => inner.RequireFirmwareIdentity(identity, expectedHeader);
        public async Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
        {
            var previous = inner.Packets.LastOrDefault();
            var response = await inner.ExchangeAsync(request, zeroAck, ct);
            if (request[1] != 0x12 || request[2] != 0x7D || inner.Rebuilt.Length != 6656 || previous is not { Length: 65 }
                || previous[1] != 0x67 || previous[2] != 1) return response;
            PostUploadCaps++;
            if (Fault == "timeout") throw new RogOledTimeoutException("Injected post-upload CAP timeout", new TimeoutException());
            if (Neutral) { response[10] = 2; response[14] |= 2; }
            switch (Fault)
            {
                case "no-ready": response[14] &= 0xF7; break;
                case "rx": response[14] |= 4; break;
                case "epoch": response[15] ^= 8; break;
                case "cookie": response[19] ^= 8; break;
                case "profile": response[12] = 5; break;
                case "mode": response[11] = 9; break;
                case "transport": response[13] = 2; break;
            }
            return response;
        }
        public async Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct)
        {
            Renewals++;
            await inner.ExchangeAsync(RogWiredOledProtocol.Renew(), true, ct);
            if (Fault == "renew") throw new InvalidDataException("Injected 00FFAA0000 after stable post-upload CAP");
            renewed(); changed().Should().BeTrue();
        }
        public void Dispose() { }
    }
}
