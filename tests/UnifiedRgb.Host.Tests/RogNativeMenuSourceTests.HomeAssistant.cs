using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData((byte)1, false)] [InlineData((byte)2, false)]
    [InlineData((byte)1, true)] [InlineData((byte)2, true)]
    public async Task HomeAssistant_startup_reopens_after_readonly_scope_refusal_and_stops_after_mutation(byte transport, bool postWrite)
    {
        var time = new SourceClock();
        using var input = new Input(time, transport);
        using var first = new CodexWireChannel(transport, 11)
        {
            RefuseBasicInfoAt = postWrite ? 0 : 4,
            RefuseBrightnessReadback = postWrite,
        };
        using var replacement = new CodexWireChannel(transport, 11);
        var policy = Policy(transport);
        var factory = new CodexActualTransportFactory(policy, first, time, reopen: () =>
        {
            first.Disposed.Should().BeTrue();
            return replacement;
        });
        var data = Path.Combine(Path.GetTempPath(), "ha-startup-" + Guid.NewGuid().ToString("N"));
        var display = transport == 1
            ? new KeyboardDisplayOptions { ControlVerified = true, LightingVerified = true, DeviceKey = policy.DeviceKey, FirmwareHeader = policy.FirmwareHeader }
            : new KeyboardDisplayOptions { WirelessControlVerified = true, WirelessLightingVerified = true, WirelessReceiverKey = policy.DeviceKey, WirelessFirmwareHeader = policy.FirmwareHeader };
        var source = new RogNativeMenuSource(policy, input, input, time, () => Cookie, homeAssistantInput: (_, _, _) => { });
        var rgb = SceneCycleRgbProxy.Create();
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var lighting = new RogWiredLightingBridge();
        using var owner = new KeyboardDisplayService(new() { DataDirectory = data, KeyboardDisplay = display },
            rgb, screens, factory, new MutationClearGuard(), lighting, wirelessFactory: factory, configurationGate: gate,
            nativeMenu: source, time: time, homeAssistantFrame: Pixels);
        try
        {
            await owner.StartAsync(default);
            await Until(() => first.Disposed);
            var expectedState = postWrite ? "Error" : "Waiting";
            using var statusDeadline = new CancellationTokenSource(Patience);
            while ((await owner.GetKeyboardDisplayStatusAsync(statusDeadline.Token)).State != expectedState)
                await Task.Delay(20, statusDeadline.Token);
            if (postWrite)
            {
                first.Requests.Count(p => p[1] == 0x68).Should().Be(1);
                factory.Opens.Should().Be(1);
            }
            else
            {
                first.Requests.Where(p => p[1] is 0x51 or 0x61 or 0x68 or 0x74 or 0x71 or 0x67).Should().BeEmpty();
                await Until(() => factory.Opens == 2 && replacement.Arms == 1 && lighting.Available);
                time.Advance(TimeSpan.FromMilliseconds(1201));
                await Until(() => replacement.Renewals == 1);
                replacement.Images.Should().ContainSingle().Which.Should().Equal(Pixels());
                (await owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
            }
        }
        finally { await owner.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Fact]
    public void HomeAssistant_linked_firmware_vectors_match_Host_CAP_ARM_and_lever_admission()
    {
        using var stream = typeof(RogNativeMenuSourceTests).Assembly.GetManifestResourceStream("HA-LINKED-VECTORS.json")!;
        using var document = JsonDocument.Parse(stream);
        document.RootElement.GetProperty("CandidateSha256").GetString()
            .Should().Be("36fa6b3deb3450d3ad214bc65ce0491930b5bda639aee0ddb14e7f362266a87d");
        var cases = document.RootElement.GetProperty("Cases").EnumerateArray().ToArray();
        cases.Should().HaveCount(36);
        foreach (var item in cases)
        {
            var transport = item.GetProperty("Transport").GetByte();
            var mode = item.GetProperty("Mode").GetByte();
            byte[] Bytes(string property) => Convert.FromHexString(item.GetProperty(property).GetString()!);
            var cap = RogNativeMenuCodec.ParseCapability(Bytes("CapabilityNormalized65"), 0x12, transport);
            var arm = Bytes("ArmRequestNormalized65");
            var cookie = BinaryPrimitives.ReadUInt64LittleEndian(arm.AsSpan(11));
            cap.Mode.Should().Be(mode);
            cap.Profile.Should().Be(item.GetProperty("Profile").GetByte());
            RogNativeMenuCodec.ArmRequest(cap, cookie).Should().Equal(arm);
            var state = new RogNativeMenuState();
            state.Arm(cap, RogNativeMenuCodec.ParseCapability(Bytes("ArmReplyNormalized65"), 0x71, transport), cookie);
            foreach (var data in item.GetProperty("NoticesNative21").EnumerateArray())
            {
                var kind = RogNativeMenuCodec.ParseNotice(Convert.FromHexString(data.GetString()!), transport, out var notice);
                kind.Should().Be(RogNativeNoticeKind.Tagged);
                state.Accept(kind, notice).Should().Be(mode == 9 && notice.Mask == 2 ? RogNativeDecision.Toggle : RogNativeDecision.Ignore);
            }
            state.Neutral.Should().BeTrue();
            state.Sequence.Should().Be(7);
        }
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task HomeAssistant_has_its_own_artwork_and_only_delivers_fresh_neutral_armed_edges(byte transport)
    {
        var inputs = new List<(byte Mask, byte[]? Artwork, Func<bool> Admitted)>();
        await using var rig = new RealTransportCodexRig(transport, (mask, artwork, admitted) => inputs.Add((mask, artwork, admitted)));
        var pixels = Pixels();
        (await rig.Actor.StepAsync(() => pixels, default)).Should().BeTrue();
        rig.Wire.Images.Should().ContainSingle().Which.Should().Equal(pixels);
        rig.Wire.Requests.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(transport == 1 ? 111 : 113);
        rig.Wire.Operations.TakeLast(2).Should().Equal("CAP_READY", "RENEW");
        rig.Wire.Requests.Should().OnlyContain(p => p.Length == (transport == 1 ? 65 : 64) && p[0] == (transport == 1 ? 0 : 2));
        await rig.SendAsync(1, 2); // held before the first physical neutral
        await rig.SendAsync(2, 0, 3);
        await rig.SendAsync(3, 1);
        await rig.SendAsync(4, 1); // a held lever is not another edge
        await rig.SendAsync(5, 0);
        await rig.SendAsync(6, 4);
        await rig.SendAsync(7, 0);
        await rig.SendAsync(8, 2);
        await rig.SendAsync(9, 0);
        await rig.Actor.StepAsync(() => pixels, default);
        inputs.Select(i => i.Mask).Should().Equal(1, 4, 2);
        inputs[2].Artwork.Should().Equal(pixels);
        inputs[2].Admitted().Should().BeTrue("a valid release does not invalidate an admitted edge");
        rig.Browses.Should().BeEmpty();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuSceneBrowse>().Should().BeEmpty();
        await rig.Actor.StopAsync(default);
        inputs[2].Admitted().Should().BeFalse();
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task HomeAssistant_missing_READY_retires_the_image_and_does_not_renew(byte transport)
    {
        await using var rig = new RealTransportCodexRig(transport, (_, _, _) => throw new InvalidOperationException());
        rig.Wire.CommitFault = "ready";
        Func<Task> upload = () => rig.Actor.StepAsync(Pixels, default);
        await upload.Should().ThrowAsync<IOException>().WithMessage("*HomeAssistantFrameReady*renewalNotSent*");
        rig.Wire.Renewals.Should().Be(0);
        rig.Actor.RecoveryBlocked.Should().BeTrue();
    }
}
