using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData("proto1")] [InlineData("build48")] [InlineData("missing32")]
    [InlineData("unknownflag")] [InlineData("transport")] [InlineData("profile6")]
    [InlineData("cookie")] [InlineData("sequence")] [InlineData("epoch0")]
    [InlineData("size")] [InlineData("padding")]
    public void Native49_codec_negatives_use_actual_compiled_producer_CAP(string fault)
    {
        using var stream = typeof(RogNativeMenuSourceTests).Assembly.GetManifestResourceStream("Actual25Producer.json")!;
        using var data = JsonDocument.Parse(stream);
        data.RootElement.GetProperty("build").GetString().Should().Be("0x25314D49");
        data.RootElement.GetProperty("sourceSha256").GetString().Should().Be("e86a58a91f09aa326f25900ee48d549b6d7a474a1d33e5f87902758b893c8e3e");
        var bytes = Convert.FromHexString(data.RootElement.GetProperty("cases")[0].GetProperty("capability65").GetString()!);
        RogNativeMenuCodec.SupportsInEpochArtwork(RogNativeMenuCodec.ParseCapability(bytes, 0x12, 1)).Should().BeTrue();
        switch (fault)
        {
            case "proto1": bytes[9] = 1; break;
            case "build48": bytes[29] = 0x48; break;
            case "missing32": bytes[14] &= unchecked((byte)~32); break;
            case "unknownflag": bytes[14] |= 64; break;
            case "transport": bytes[13] = 2; break;
            case "profile6": bytes[12] = 6; break;
            case "cookie": bytes[19] = 1; break;
            case "sequence": bytes[27] = bytes[28] = 0xFF; break;
            case "epoch0": Array.Clear(bytes, 15, 4); break;
            case "size": bytes[33] = 207; break;
            case "padding": bytes[^1] = 1; break;
        }
        Action parse = () => RogNativeMenuCodec.ParseCapability(bytes, 0x12, 1);
        if (fault is "build48" or "unknownflag")
        {
            parse.Should().NotThrow("build tags and unknown optional capability bits are diagnostic");
            RogNativeMenuCodec.SupportsInEpochArtwork(RogNativeMenuCodec.ParseCapability(bytes, 0x12, 1)).Should().BeTrue();
        }
        else parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("default")] [InlineData("off")] [InlineData("notifications")]
    [InlineData("d79")] [InlineData("dcf")] [InlineData("contract")]
    [InlineData("device")] [InlineData("header24")] [InlineData("kind")]
    public void Native49_capability_is_pure_exact_local_policy_and_scope(string fault)
    {
        var policy = Policy(); var scope = new KeyboardNativePresentationScope(policy.DeviceKey, policy.FirmwareHeader, policy.ConnectionKind);
        new RogNative49PresentationCapability(policy).Evaluate(scope).Should().Be(KeyboardNativePresentationSupport.Available);
        policy = fault switch
        {
            "default" => new(), "off" => policy with { Enabled = false },
            "notifications" => policy with { SourceControlNotifications = false },
            "d79" => policy with { CandidateSha256 = "d79e8be2a6430e2ee62624898089376a58ce815614a8b5fd9f8213277abba4d0" },
            "dcf" => policy with { CandidateSha256 = "dcfcc1af245a8624dab977cbf18ad9aaba3efbe69393f53b6ebcd158ade4e679" },
            "contract" => policy with { ContractSha256 = new string('a', 64) },
            "device" => policy with { DeviceKey = new string('b', 64) },
            "header24" => policy with { FirmwareHeader = "001200000024000100" },
            _ => policy with { ConnectionKind = "bluetooth" },
        };
        var unchanged = policy;
        var expected = fault is "d79" or "dcf" or "contract" or "header24"
            ? KeyboardNativePresentationSupport.Available : KeyboardNativePresentationSupport.Unavailable;
        for (var n = 0; n < 3; n++) new RogNative49PresentationCapability(policy).Evaluate(scope).Should().Be(expected);
        policy.Should().BeSameAs(unchanged, "pure evaluation supplies no timestamps, I/O or caller-supplied expected hashes");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Native49_replacement_preserves_queued_neutral_press_and_commits_both_frames(byte transport)
    {
        await using var rig = new Rig(transport); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var second = Pixels(); second[0] ^= 1;
        rig.Io.ReplacementChunk = () => { rig.Input.Send(Notice(1, 0, 3, transport)); rig.Input.Send(Notice(2, 2, 1, transport)); };
        (await rig.Actor.StepAsync(() => second, default)).Should().BeTrue();
        await rig.Input.WaitReads(2); await rig.Actor.StepAsync(() => second, default);
        rig.Io.Arms.Should().Be(1); rig.Input.Opens.Should().Be(1); rig.Io.ImageBegins.Should().Be(2); rig.Io.ImageCommits.Should().Be(2);
        rig.Events.OfType<NativeMenuContextBegun>().Should().ContainSingle(); rig.Events.OfType<NativeMenuContextEnded>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Should()
            .Match<NativeMenuCaseIntent>(p => p.Sequence == 2 && p.NeutralArmSequence == 1 && p.ContextGeneration == 2 && p.Action == DesiredAction.Toggle);
    }

    [Theory]
    [InlineData("renew")] [InlineData("renew-read")]
    public async Task Native49_unknown_or_malformed_commit_freezes_without_acting_or_retry(string fault)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true); rig.Io.FailStage = fault;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<Exception>();
        var count = rig.Io.Writes.Count;
        rig.Actor.RecoveryBlocked.Should().BeTrue(); rig.Source.IsPresent.Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty(); rig.Io.ImageCommits.Should().Be(0);
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>();
        rig.Io.Writes.Should().HaveCount(count); rig.Io.Arms.Should().Be(1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native49_final_chunk_disable_or_observed_exit_prevents_renewal_and_frame_commit(bool exit)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        rig.Io.FinalChunk = () =>
        {
            if (exit) { rig.Io.Mode = 7; rig.Actor.Retire("observed native mode exit at final ACK", false); }
            else rig.Source.RequestEnabled(false);
        };
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.ImageCommits.Should().Be(0); rig.Io.Packets.Should().NotContain(p => p[1] == 0x67 && p[2] == 2);
        rig.Actor.Armed.Should().BeFalse(); rig.Input.Pending.Should().Be(0); rig.Input.LiveHandles.Should().Be(0);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
    }

    [Fact]
    public async Task Native49_normal_DI_actual_center_presses_reach_two_serialized_toggles()
    {
        var time = new SourceClock(); using var input = new Input(time, 1); using var control = new Connection(time, 1) { Input = input };
        var data = Path.Combine(Path.GetTempPath(), "SolNative49Composition-" + Guid.NewGuid().ToString("N"));
        var host = new MenuHostFake(new([new([new("lianli:1", 0)], "Rainbow", ["#FF0000"])]));
        var options = new UnifiedRgbOptions { UseVirtualAdapter = false, DataDirectory = data, KeyboardNativeMenu = Policy() };
        var services = new ServiceCollection().AddLogging().AddUnifiedRgb(options, enableIpc: false);
        services.AddSingleton(host.Connection.Rgb); services.AddSingleton(host.Connection.Screens!);
        services.AddSingleton<IWriterGuard>(new MutationClearGuard()); services.AddSingleton<IRogWiredOledFactory>(new NoOled());
        services.AddSingleton<IRogWirelessOledFactory>(new NoWirelessOled());
        services.AddSingleton<IKeyboardOledMedia>(new FakeKeyboardOledMedia()); services.AddSingleton<IScreenTimeline>(new FakeSharedTimeline());
        services.AddSingleton<IHidInputReportDevices>(input); services.AddSingleton<IHidContainerResolver>(input); services.AddSingleton<TimeProvider>(time);
        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<RogNativeMenuSource>();
        provider.GetRequiredService<INativeMenuSource>().Should().BeSameAs(source);
        var menu = provider.GetRequiredService<NativeMenuService>();
        provider.GetRequiredService<INativeMenuService>().Should().BeSameAs(menu);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IKeyboardNativePresentationCapability));
        await using var actor = source.Attach(control); input.Admitted = () => actor.ReceivedNotifications;
        try
        {
            input.Opens.Should().Be(0); await menu.StartAsync(default); await menu.SetNativeMenuEnabledAsync(true);
            await actor.StepAsync(Pixels, default);
            // The normal composition supplied the real source/cookie, not an injected handwritten event source.
            var arm = control.Packets.Single(p => p[1] == 0x71 && p[10] == 1);
            var cookie = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(arm.AsSpan(11));
            input.Send(ScopedNotice(1, 0, 3, control.Epoch, cookie)); input.Send(ScopedNotice(2, 2, 1, control.Epoch, cookie));
            await input.WaitReads(2); await actor.StepAsync(Pixels, default);
            await Until(() => menu.Trace.Any(t => t.StartsWith("artwork #2:", StringComparison.Ordinal)));
            host.Applied.Should().ContainSingle();
            var feedback = provider.GetRequiredService<KeyboardDisplayService>().ArtworkPixels.ToArray();
            await actor.StepAsync(() => feedback, default);
            input.Send(ScopedNotice(3, 0, 1, control.Epoch, cookie)); input.Send(ScopedNotice(4, 2, 1, control.Epoch, cookie));
            await input.WaitReads(4); await actor.StepAsync(() => feedback, default);
            await Until(() => menu.Trace.Any(t => t.StartsWith("artwork #4:", StringComparison.Ordinal)));
            host.Applied.Should().HaveCount(2, "two distinct neutral-separated center presses toggle independently; no coalescing by kind");
            control.Arms.Should().Be(1); input.Opens.Should().Be(1);
            await menu.SetNativeMenuEnabledAsync(false); (await actor.StepAsync(() => feedback, default)).Should().BeFalse();
            input.LiveHandles.Should().Be(0); input.Pending.Should().Be(0);
        }
        finally { await menu.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native49_only_route_opens25_without_inheriting_legacy24_lighting_or_CAP_fallback(bool legacyVerified)
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNative49Route-" + Guid.NewGuid().ToString("N"));
        var time = new SourceClock(); using var input = new Input(time, 1); using var control = new Connection(time, 1) { Input = input, BadCapabilityGrammar = true };
        var policy = Policy(); var source = new RogNativeMenuSource(policy, input, input, time); source.RequestEnabled(true);
        var factory = new LegacyPageFactory(control, false); var bridge = new RogWiredLightingBridge();
        var legacy = legacyVerified ? new KeyboardDisplayOptions { ControlVerified = true, LightingVerified = true, DeviceKey = control.DeviceKey, FirmwareHeader = "001200000024000100" } : new();
        legacy.HasVerifiedWiredLighting.Should().Be(legacyVerified);
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
        var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        var wireless = new Native49ClosedWireless();
        using var owner = new KeyboardDisplayService(new() { DataDirectory = data, KeyboardDisplay = legacy }, rgb, screens, factory,
            new MutationClearGuard(), bridge, wireless, nativeMenu: source);
        try
        {
            await owner.StartAsync(default); await Until(() => control.Writes.Contains("12/7D")); await Task.Delay(100);
            factory.Opens.Should().Be(1); control.Arms.Should().Be(0); control.ImageBegins.Should().Be(0); input.Opens.Should().Be(0);
            wireless.Calls.Should().Be(0);
            factory.RequestedScopes.Should().ContainSingle().Which.Should().Be(new KeyboardNativePresentationScope(control.DeviceKey, RogRuntimeCompatibility.NativeFamilyAnchor("wired")!, "wired"));
            bridge.Available.Should().Be(legacyVerified, "only independently verified lighting is admitted; native CAP never grants it");
            if (!legacyVerified) control.Packets.Should().NotContain(p => p[1] == 0x25 || p[1] == 0x51);
            else
            {
                await bridge.WriteAsync(new(7, 8, 9), default).WaitAsync(Patience);
                control.Colours.Should().Contain(new Rgb(7, 8, 9));
            }
            source.OwnsDisplayOutput.Should().BeTrue("failed live CAP does not authorize legacy image output");
            legacy.ControlVerified.Should().Be(legacyVerified); legacy.FirmwareHeader.Should().Be(legacyVerified ? "001200000024000100" : "");
        }
        finally { await owner.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Theory]
    [InlineData("header")] [InlineData("kind")] [InlineData("missing-key")] [InlineData("default-off")]
    public async Task Native49_invalid_or_default_off_route_never_opens_any_factory(string fault)
    {
        var policy = fault switch { "header" => Policy() with { DeviceKey = "not-a-qualified-device-key" },
            "kind" => Policy() with { ConnectionKind = "bluetooth" }, "missing-key" => Policy() with { DeviceKey = "" }, _ => new() };
        var data = Path.Combine(Path.GetTempPath(), "SolNative49NoOpen-" + Guid.NewGuid().ToString("N"));
        var source = new RogNativeMenuSource(policy); source.RequestEnabled(true);
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>(); var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        var wired = new Native49ClosedWired(); var wireless = new Native49ClosedWireless();
        using var owner = new KeyboardDisplayService(new() { DataDirectory = data }, rgb, screens, wired, new MutationClearGuard(), wirelessFactory: wireless, nativeMenu: source);
        try
        {
            await owner.StartAsync(default); await Task.Delay(100);
            source.IsPresent.Should().BeFalse(); (await owner.GetKeyboardDisplayStatusAsync()).FramesAcknowledged.Should().Be(0);
            wired.Calls.Should().Be(0); wireless.Calls.Should().Be(0);
            ((KeyboardDisplayServiceTests.ReadProxy)rgb).CaseMutations.Should().Be(0); ((KeyboardDisplayServiceTests.ReadProxy)screens).ScreenMutations.Should().Be(0);
        }
        finally { await owner.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Fact]
    public async Task Native49_absent_known_USB_route_checks_the_configured_RF_route_without_legacy_output()
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNative49NoFallback-" + Guid.NewGuid().ToString("N"));
        var display = new KeyboardDisplayOptions { WirelessControlVerified = true, WirelessLightingVerified = true,
            WirelessReceiverKey = new string('A', 64), WirelessFirmwareHeader = "0800070025000100" };
        var source = new RogNativeMenuSource(Policy(), displayOptions: display); source.RequestEnabled(true);
        var wired = new Native49ClosedWired(); var wireless = new Native49ClosedWireless();
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>(); var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var owner = new KeyboardDisplayService(new() { DataDirectory = data, KeyboardDisplay = display },
            rgb, screens, wired, new MutationClearGuard(), wirelessFactory: wireless, nativeMenu: source);
        try
        {
            await owner.StartAsync(default); await Until(() => wireless.Calls == 1); await Task.Delay(50);
            wired.Calls.Should().Be(1); wireless.Calls.Should().Be(1); source.IsPresent.Should().BeFalse();
            (await owner.GetKeyboardDisplayStatusAsync()).FramesAcknowledged.Should().Be(0);
        }
        finally { await owner.StopAsync(default); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Fact]
    public async Task Native49_disabled_native_journal_uses_exact_explicit_recovery_without_ARM_or_lighting()
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNative49Recovery-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json"); Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var time = new SourceClock(); using var input = new Input(time, 1); using var control = new Connection(time, 1) { Input = input };
        var source = new RogNativeMenuSource(Policy(), input, input, time); source.RequestEnabled(false);
        var factory = new LegacyPageFactory(control, false); var bridge = new RogWiredLightingBridge();
        File.WriteAllText(journal, JsonSerializer.Serialize(new { Schema = 2, control.DeviceKey, control.FirmwareHeader, Brightness = 100, ProfileToken = 6 }));
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>(); var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var owner = new KeyboardDisplayService(new() { DataDirectory = data }, rgb, screens, factory, new MutationClearGuard(), bridge, nativeMenu: source);
        try
        {
            await owner.ReleaseForPeripheralConfigurationAsync(default);
            factory.Opens.Should().Be(1); File.Exists(journal).Should().BeFalse("the existing exact-scope/profile verified recovery completed");
            source.Requested.Should().BeFalse(); input.Opens.Should().Be(0); control.Arms.Should().Be(0); control.ImageBegins.Should().Be(0);
            control.Packets.Should().NotContain(p => p[1] == 0x25 || p[1] == 0x51 || p[1] == 0x67); bridge.Available.Should().BeFalse();
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    [Theory]
    [InlineData("profile")] [InlineData("device")] [InlineData("header")]
    public async Task Native49_recovery_scope_mismatch_retains_journal_and_never_rearms(string fault)
    {
        var data = Path.Combine(Path.GetTempPath(), "SolNative49RecoveryBlocked-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(data, "peripherals", "keyboard-display-session.json"); Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var time = new SourceClock(); using var input = new Input(time, 1); using var control = new Connection(time, 1) { Input = input };
        var source = new RogNativeMenuSource(Policy(), input, input, time); source.RequestEnabled(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = fault == "device" ? new string('b', 64) : control.DeviceKey,
            FirmwareHeader = fault == "header" ? "001300000025000100" : control.FirmwareHeader, Brightness = 100, ProfileToken = fault == "profile" ? 1 : 6 });
        File.WriteAllBytes(journal, bytes);
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>(); var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
        using var owner = new KeyboardDisplayService(new() { DataDirectory = data }, rgb, screens, new LegacyPageFactory(control, false), new MutationClearGuard(), nativeMenu: source);
        try
        {
            await ((Func<Task>)(() => owner.ReleaseForPeripheralConfigurationAsync(default))).Should().ThrowAsync<IOException>();
            File.ReadAllBytes(journal).Should().Equal(bytes); input.Opens.Should().Be(0); control.Arms.Should().Be(0); control.ImageBegins.Should().Be(0);
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private sealed class Native49ClosedWired : IRogWiredOledFactory
    {
        public int Calls { get; private set; }
        public RogWiredOledConnection? TryOpen(string deviceKey) { Calls++; return null; }
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader) { Calls++; return null; }
    }
    private sealed class Native49ClosedWireless : IRogWirelessOledFactory
    {
        public int Calls { get; private set; }
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader) { Calls++; return null; }
    }
}
