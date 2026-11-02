using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task LocalImageAbort_BLACK_first_Up_keeps_owner_and_popup_then_ACKs_latest_caption_and_services_RGB()
    {
        var data = Path.Combine(Path.GetTempPath(), "local-image-abort-" + Guid.NewGuid().ToString("N"));
        var time = new SourceClock(); using var input = new Input(time, 2); using var control = new Connection(time, 2) { Input = input };
        var factory = new ArtworkRefusalFactory(control, input, time, "chunk") { SoftwareOn = true };
        var rgb = SceneCycleRgbProxy.Create(); var state = (SceneCycleRgbProxy)rgb;
        state.Scene = new([new([new("live", 0)], "StaticColor", ["#000000"])]);
        state.Profiles.Add("A red", new("A red", new([new([new("live", 0)], "StaticColor", ["#FF0000"])])));
        state.Profiles.Add("B blue", new("B blue", new([new([new("live", 0)], "StaticColor", ["#0000FF"])])));
        var packages = DispatchProxy.Create<IScenePackageService, LocalImagePackages>(); var list = (LocalImagePackages)packages;
        var options = new UnifiedRgbOptions { DataDirectory = data, KeyboardDisplay = new() { WirelessControlVerified = true,
            WirelessLightingVerified = true, WirelessReceiverKey = control.DeviceKey, WirelessFirmwareHeader = control.FirmwareHeader,
            DeviceShowsNativeEntryLogo = true } };
        ulong cookie = Cookie; var source = new RogNativeMenuSource(Policy(2), input, input, time, () => cookie++);
        var overlay = new KeyboardSceneCycleOverlay(); var bridge = new RogWiredLightingBridge();
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var gate = new PeripheralConfigurationGate(new(false, null));
        await using var cycle = new KeyboardSceneCycleBridge(options, () => rgb, () => packages, overlay, cycleOptions: new(TimeSpan.Zero));
        using var owner = new KeyboardDisplayService(options, rgb, screens, new NoOled(), new MutationClearGuard(), bridge,
            wirelessFactory: factory, configurationGate: gate, nativeMenu: source,
            nativePresentation: new DeclaredPresentation(KeyboardNativePresentationSupport.Available), nativeFrameOverlay: overlay.Apply,
            nativeFrameAcknowledged: overlay.Acknowledge, nativeFrameOverlayRevision: () => overlay.Revision);
        await using var menu = new NativeMenuService(options, rgb, screens, source, owner, time: time, sceneCycle: cycle);
        byte[]? journal = null, failedPixels = null;
        factory.OnRefusal = () =>
        {
            journal = File.ReadAllBytes(Path.Combine(data, "peripherals", "keyboard-display-session.json"));
            var presentation = typeof(KeyboardDisplayService).GetField("_presentation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            failedPixels = ((byte[])presentation.GetType().GetField("_chosen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presentation)!).ToArray();
            control.Epoch += 2; typeof(Connection).GetField("_cookie", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, 0UL);
            list.Release.TrySetResult();
        };
        await menu.StartAsync(default); await menu.SetNativeMenuEnabledAsync(true); await owner.StartAsync(default);
        try
        {
            await Until(() => control.Arms == 1 && bridge.Available);
            (await owner.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("机箱已熄灯"); control.ImageBegins.Should().Be(0);
            input.Send(Notice(1, 0, 3, 2)); input.Send(Notice(2, 1, 1, 2));
            await Until(() => menu.Trace.Contains("browse #2 Previous accepted"));
            await Until(() => control.ImageCommits == 1 && overlay.Current?.Name.Length > 0);
            control.Rebuilt.Should().Equal(overlay.Apply(owner.ArtworkPixels.ToArray())).And.NotEqual(failedPixels!);
            factory.Channels.Should().ContainSingle(); factory.Channels[0].Disposed.Should().BeFalse();
            control.Arms.Should().Be(2); control.Disarms.Should().Be(0); control.ImageBegins.Should().Be(2);
            var status = await owner.GetKeyboardDisplayStatusAsync();
            var raw = (UnifiedRgb.Contracts.Dto.KeyboardDisplayStatusDto)typeof(KeyboardDisplayService)
                .GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            status.Detail.Should().Contain("累计发生 1 次画面重试");
            status.State.Should().Be(raw.State); status.LastError.Should().Be(raw.LastError);
            control.ArmWhileInputReading.Should().BeFalse(); input.Opens.Should().Be(2); input.LiveHandles.Should().Be(1);
            var packets = factory.Channels[0].Requests;
            var after = packets.SkipWhile(p => !(p[1] == 0x67 && p[2] == 1 && p[3] == 25)).Skip(1).ToArray();
            after.Should().NotContain(p => new byte[] { 0x61, 0x68, 0x74, 0x51 }.Contains(p[1]));
            after.TakeWhile(p => !(p[1] == 0x67 && p[2] == 0)).Should().NotContain(p => p[1] == 0x67 && p[2] == 2,
                "a notREADY native context must not receive an OLED lease renewal");
            File.ReadAllBytes(Path.Combine(data, "peripherals", "keyboard-display-session.json")).Should().Equal(journal!);
            File.Exists(Path.Combine(data, "peripherals", KeyboardDisplayAnimation.FileName)).Should().BeFalse();
            var colour = Enumerable.Range(0, 85).Select(i => new RogKeyColor((byte)i, new((byte)i, 7, 9))).ToArray();
            await bridge.WriteKeysAsync(colour, RogPerKeyLighting.WirelessTransport, default).WaitAsync(Patience);
            packets.Where(p => p[1] == 0xC0).Select(Convert.ToHexString).Should().Equal(RogProtocol.KeyboardFrame(2, colour).Select(Convert.ToHexString));
            state.Applied.Should().BeEmpty(); menu.Trace.Should().NotContain(t => t.StartsWith("press #", StringComparison.Ordinal));
            // The new input scope has not invented neutral. A real neutral/Up pair works, while old queued edges were retired.
            foreach (var p in new[] { Notice(1, 0, 3, 2), Notice(2, 1, 1, 2) })
            { BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), control.Epoch); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), Cookie + 1); input.Send(p); }
            await Until(() => menu.Trace.Count(t => t == "browse #2 Previous accepted") == 2);
            factory.Channels[0].MaximumReaders.Should().Be(1);
        }
        finally { await owner.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Fact]
    public async Task LocalImageAbort_generic_FFAA_stays_poisoned_and_original_RunAsync_ABI_is_unchanged()
    {
        foreach (var request in new[] { RogNativeMenuCodec.CapabilityRequest(), RogWiredOledProtocol.Brightness(25), RogWiredOledProtocol.Pack(Pixels(), 59)[1] })
        {
            using var io = new ImageNegativeOnly(); using var actual = new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE,
                new Dictionary<byte, UnifiedRgb.Transports.Hid.HidCollectionInfo>()), io, "0800070025000100", () => true);
            await ((Func<Task>)(() => actual.ExchangeAsync(request, false, default))).Should().ThrowAsync<RogOledEnvelopeRefusedException>();
            await ((Func<Task>)(() => actual.ExchangeAsync(request, false, default))).Should().ThrowAsync<IOException>();
            io.Writes.Should().Be(1);
        }
        typeof(RogWiredOledSession).GetMethods().Where(m => m.Name == nameof(RogWiredOledSession.RunAsync))
            .Select(m => m.GetParameters().Length).Should().BeEquivalentTo([13, 14]);
    }

    private sealed class ImageNegativeOnly : UnifiedRgb.Transports.Hid.IHidReportChannel
    {
        public int Writes { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) { Writes++; return ValueTask.CompletedTask; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { buffer.Span.Clear(); buffer.Span[0] = 2; buffer.Span[1] = 0xFF; buffer.Span[2] = 0xAA; return ValueTask.FromResult(64); }
        public void Dispose() { }
    }
}

public class LocalImagePackages : DispatchProxy
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(IScenePackageService.ListScenePackagesAsync) && args is [CancellationToken ct]
            ? List(ct) : throw new InvalidOperationException("Unexpected local-image fake package request.");
    private async Task<IReadOnlyList<UnifiedRgb.Contracts.Dto.ScenePackageInfoDto>> List(CancellationToken ct)
    { await Release.Task.WaitAsync(ct); return []; }
}
