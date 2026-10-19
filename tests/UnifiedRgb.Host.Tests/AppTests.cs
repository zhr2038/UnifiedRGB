using System.IO.Compression;
using UnifiedRgb.App.Diagnostics;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Preview;
using UnifiedRgb.App.Tray;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.Tests;

/// <summary>M7: GUI logic without a window — preview parity with the golden fixtures, reconnect, view models over the pipe.</summary>
public sealed class PreviewTests
{
    private static readonly (string Name, ZoneLayout Layout)[] Layouts =
    [
        ("Linear20", ZoneLayout.Linear(20)),
        ("Ring16", ZoneLayout.Ring(16)),
        ("Matrix4x4", ZoneLayout.Matrix(4, 4)),
        ("Point", ZoneLayout.Point),
    ];

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Preview_frames_equal_the_golden_fixtures()
    {
        var checkedFrames = 0;
        foreach (var gen in EffectRegistry.Default.All)
        {
            foreach (var (name, layout) in Layouts)
            {
                var preview = new PreviewRenderer(gen, [layout], new EffectParams([], 0.5f, 1f, Direction.Forward, 12345));
                foreach (var frame in new[] { 0, 30, 120 })
                {
                    var px = preview.Render(frame / PreviewRenderer.Fps);
                    var bytes = px.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
                    var golden = File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "Golden", gen.Id, $"{name}-{frame}.bin"));
                    bytes.Should().Equal(golden, $"{gen.Id}/{name}-{frame}");
                    checkedFrames++;
                }
            }
        }

        checkedFrames.Should().Be(EffectRegistry.Default.All.Count * 4 * 3);
    }

    [Fact]
    public void Wire_zones_map_to_engine_layouts()
    {
        PreviewRenderer.ToLayout(new ZoneDto(0, "Ring", "Ring", 16, 0, 0)).Kind.Should().Be(LayoutKind.Ring);
        PreviewRenderer.ToLayout(new ZoneDto(0, "M", "Matrix", 16, 4, 4)).Kind.Should().Be(LayoutKind.Matrix);
        PreviewRenderer.ToLayout(new ZoneDto(0, "P", "Point", 1, 0, 0)).Kind.Should().Be(LayoutKind.Point);
        PreviewRenderer.ToLayout(new ZoneDto(0, "S", "Linear", 30, 0, 0)).LedCount.Should().Be(30);
    }

    [Fact]
    public void Group_preview_is_one_continuous_strip()
    {
        var p = new PreviewRenderer(EffectRegistry.Default["Runway"], [ZoneLayout.Ring(16), ZoneLayout.Linear(8)], EffectParams.Default);
        p.Render(0.5).Should().HaveCount(24);
        p.Samples[16].U.Should().BeGreaterThan(p.Samples[15].U);
    }

    [Theory]
    [InlineData("#00FF7f", true)]
    [InlineData("12ab34", true)]
    [InlineData("#12345", false)]
    [InlineData("red", false)]
    public void Color_text_parses_only_rrggbb(string text, bool ok)
    {
        ColorText.TryParse(text, out var c).Should().Be(ok);
        if (ok)
        {
            ColorText.TryParse(ColorText.Format(c), out var again).Should().BeTrue();
            again.Should().Be(c);
        }
    }

    private static ZoneDto[] FourZones(bool reverseTl120) =>
    [
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3, reverseTl120),
        new ZoneDto(0, "H2", "Ring", 24, 0, 0, "H2", 1),
        new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
    ];

    [Fact]
    public void Preview_slices_a_reversed_chain_like_the_host()
    {
        // WP-13: ZoneDto.ChainReversed reaches the preview's vendor plan exactly as the Host's LConnectZone.ReverseChain
        var gen = EffectRegistry.Default["Meteor"];
        var p = new EffectParams([new Rgb(255, 0, 0), new Rgb(0, 255, 0)], 0.5f);
        var plain = PreviewRenderer.VendorPlanFor(gen, p, FourZones(false), Core.Engine.LoopPolicy.LConnect)!;
        var flipped = PreviewRenderer.VendorPlanFor(gen, p, FourZones(true), Core.Engine.LoopPolicy.LConnect)!;
        var host = Core.Effects.LianLi.LConnectLoops.TryPlan("Meteor", p, p.Colors,
            FourZones(true).Select(z => new Core.Effects.LianLi.LConnectZone(z.Family!, z.Units, z.LedCount, z.ChainReversed)).ToArray())!;
        flipped.Zones[1].Rgb.Should().Equal(host.Zones[1].Rgb);
        flipped.Zones[1].Rgb.Should().NotEqual(plain.Zones[1].Rgb, "the reversed chain plays fan 3 → fan 1");
        flipped.Zones[0].Rgb.Should().Equal(plain.Zones[0].Rgb);
    }

    [Fact]
    public void Preview_of_an_empty_stacking_selection_uses_the_non_black_default()
    {
        // WP-13 Fix 1 (DEBUG-SESSION-3 §5): the editor default and a restored empty assignment both light every chain
        var gen = EffectRegistry.Default["Stacking"];
        (gen.Schema.MinColors, gen.Schema.DefaultColors.Count).Should().Be((1, 1));
        var plan = PreviewRenderer.VendorPlanFor(gen, new EffectParams([], 0.5f), FourZones(false), Core.Engine.LoopPolicy.LConnect)!;
        plan.Zones.Should().OnlyContain(z => z.Frames == 792 && z.Rgb.Any(b => b != 0));
        var black = PreviewRenderer.VendorPlanFor(gen, new EffectParams([new Rgb(0, 0, 0)], 0.5f), FourZones(false), Core.Engine.LoopPolicy.LConnect)!;
        black.Zones.Should().OnlyContain(z => z.Rgb.All(b => b == 0), "explicit black stays black");
    }

    [Fact]
    public async Task Mounting_editor_round_trips_the_chain_reversal_separately_from_the_mirror()
    {
        var dto = new MountingDto("lianli:rf:AA-BB-CC-DD-EE-01", 0, true, [], "TL", 3, ReverseChain: true);
        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var item = new MountingItem(dto, client, InlineDispatcher.Instance, _ => { });
        (item.Reverse, item.ReverseChain).Should().Be((true, true));
        item.Reverse = false;
        var built = item.Build();
        (built.Reverse, built.ReverseChain).Should().Be((false, true));
        item.ReverseChain = false;
        item.Build().ReverseChain.Should().BeFalse();
    }
}


public sealed class AppLogicTests
{
    [Fact]
    public void Backoff_doubles_from_250ms_and_caps_at_5s()
    {
        Enumerable.Range(0, 8).Select(i => PipeClient.Backoff(i).TotalMilliseconds)
            .Should().Equal(250, 500, 1000, 2000, 4000, 5000, 5000, 5000);
        PipeClient.Backoff(1000).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Tray_state_reflects_connection_and_controller_states()
    {
        var ok = Controller("a", ControllerStates.Active);
        MainViewModel.TrayStateOf(false, [ok]).Should().Be(TrayState.Disconnected);
        MainViewModel.TrayStateOf(true, [ok]).Should().Be(TrayState.Ok);
        MainViewModel.TrayStateOf(true, []).Should().Be(TrayState.Attention);
        MainViewModel.TrayStateOf(true, [ok, Controller("b", ControllerStates.Blocked)]).Should().Be(TrayState.Attention);
        MainViewModel.TrayStateOf(true, [Controller("c", ControllerStates.Contended)]).Should().Be(TrayState.Attention);
        TrayIcon.TextOf(TrayState.Attention).Length.Should().BeLessThan(64, "NotifyIcon.Text limit");
    }

    [Fact]
    public void Controller_hints_name_the_blocking_process()
    {
        new ControllerItem(Controller("a", ControllerStates.Blocked) with { BlockedBy = "L-Connect 3.exe" }).Hint
            .Should().Contain("L-Connect 3.exe").And.Contain("Takeover");
        new ControllerItem(Controller("a", ControllerStates.Active)).Hint.Should().BeEmpty();
    }

    [Fact]
    public void Takeover_result_text_lists_stopped_services_and_processes()
    {
        DevicesViewModel.Describe(new TakeoverDto("LianLi", true, ["L-Connect-Service"], ["L-Connect 3.exe"], null))
            .Should().Contain("L-Connect-Service").And.Contain("Please close: L-Connect 3.exe");
        DevicesViewModel.Describe(new TakeoverDto("MsiGpu", false, [], [], null)).Should().Be("Takeover off.");
    }

    [Fact]
    public void Redaction_hashes_macs_and_strips_user_names()
    {
        var text = "rx AA:BB:CC:DD:EE:0F and aa-bb-cc-dd-ee-0f at C:\\Users\\alice\\AppData and C:\\\\Users\\\\bob\\\\x \"/Users/carol/y\"";
        var r = DiagnosticsExporter.Redact(text);
        r.Should().NotContain("AA:BB").And.NotContain("aa-bb").And.NotContain("alice").And.NotContain("bob").And.NotContain("carol");
        r.Should().Contain("C:\\Users\\<user>\\AppData");
        var macs = System.Text.RegularExpressions.Regex.Matches(r, "MAC-[0-9A-F]{8}").Select(m => m.Value).ToArray();
        macs.Should().HaveCount(2);
        macs[0].Should().Be(macs[1], "the hash is stable across separator/case so logs stay correlatable");
    }

    [Fact]
    public void Export_zips_logs_status_and_active_profile_redacted()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N"));
        var host = Path.Combine(root, "host");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(host, "logs"));
        Directory.CreateDirectory(Path.Combine(host, "profiles"));
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(host, "logs", "host-20261017.log"), "opened 01:02:03:04:05:06");
        File.WriteAllText(Path.Combine(app, "app-20261017.log"), "C:\\Users\\alice\\x");
        File.WriteAllText(Path.Combine(host, "status.json"), "{}");
        File.WriteAllText(Path.Combine(host, "profiles", "active.json"), "{\"name\":\"x\"}");
        try
        {
            var zip = Path.Combine(root, "d.zip");

            // the Host keeps its log open for writing while we export
            using (new FileStream(Path.Combine(host, "logs", "host-20261017.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                DiagnosticsExporter.Export(host, app, zip).Should().BeEquivalentTo(
                    "logs/host/host-20261017.log", "logs/app/app-20261017.log", "status.json", "active-profile.json");
            }

            using var archive = ZipFile.OpenRead(zip);
            var all = string.Concat(archive.Entries.Select(e =>
            {
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }));
            all.Should().NotContain("01:02:03").And.NotContain("alice").And.Contain("MAC-");
            File.Exists(zip + ".tmp").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Settings_toggle_writes_the_run_entry()
    {
        var reg = new MemoryAutostart();
        var s = new SettingsViewModel(reg, @"C:\Program Files\UnifiedRgb\UnifiedRgb.App.exe");
        s.StartWithWindows.Should().BeFalse();
        s.StartWithWindows = true;
        reg.Value.Should().Be("\"C:\\Program Files\\UnifiedRgb\\UnifiedRgb.App.exe\" --minimized");
        new SettingsViewModel(reg, "x").StartWithWindows.Should().BeTrue();
        s.StartWithWindows = false;
        reg.Value.Should().BeNull();
    }

    internal static ControllerDto Controller(string id, string state) =>
        new(id, "Test", id, "Virtual", "Virtual", id, [new ZoneDto(0, "z", "Linear", 4, 0, 0)], 60, state, null);

    internal sealed class MemoryAutostart : IAutostartRegistry
    {
        public string? Value { get; private set; }

        public string? Get() => Value;

        public void Set(string? command) => Value = command;
    }
}

[Collection("Host")]
public sealed class GuiAgainstHostTests
{

    private static MainViewModel NewMain(PipeClient client) =>
        new(client, InlineDispatcher.Instance, new AppLogicTests.MemoryAutostart(), "app.exe", Path.GetTempPath(), Path.GetTempPath());

    [Fact]
    public async Task View_models_drive_a_host_with_virtual_devices()
    {
        await using var host = await TestHost.StartAsync();
        await using var client = new PipeClient(host.Options.PipeName, PipeTrust.ElevatedOwnerOrCurrentUser);
        var main = NewMain(client);
        client.Start();
        (await client.WaitConnectedAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await main.RefreshAllAsync();

        main.Devices.Controllers.Should().HaveCount(9);
        main.Devices.Snapshot.Should().OnlyContain(c => c.ControlVerified, "virtual adapters have no native gate; the flag crosses the pipe");
        main.Bringup.Targets.Should().BeEmpty("no native class is present");
        main.Diagnostics.Bringup.Should().BeSameAs(main.Bringup);
        main.TrayState.Should().Be(TrayState.Ok);
        main.Settings.FrameRate.Should().StartWith("60 fps");

        // Effects page: schema-driven, two fans → one group assignment
        var fx = main.Effects;
        fx.Effects.Should().Contain(e => e.Id == "Runway");
        fx.SelectedEffect = fx.Effects.Single(e => e.Id == "StaticColor");
        fx.Colors.Should().HaveCount(fx.SelectedEffect.DefaultColors.Count);
        fx.Colors[0].Hex = "#102030";
        foreach (var z in fx.Zones.Where(z => z.Controller.Id is "virtual:fan1" or "virtual:fan2"))
        {
            z.IsSelected = true;
        }

        fx.ShowFamilyModes.Should().BeFalse("virtual zones are not Lian Li");
        fx.Preview.Should().NotBeNull();
        fx.Preview!.Layouts.Should().HaveCount(2);
        await fx.ApplyCommand.ExecuteAsync();
        var scene = await client.Require().GetSceneAsync();
        scene.Assignments.Should().ContainSingle(a => a.EffectId == "StaticColor" && a.Targets.Count == 2 && a.Colors[0] == "#102030");

        // live apply: two slider moves inside the 300 ms window → one debounced apply (D2 §4)
        fx.LiveApply.Should().BeFalse("the Chinese workbench defaults to explicit apply");
        fx.LiveApply = true; // Exercise the retained opt-in legacy live-apply feature.
        fx.Speed = 0.9f;
        fx.Brightness = 0.4f;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !(await client.Require().GetSceneAsync()).Assignments.Any(a => a.EffectId == "StaticColor" && Math.Abs(a.Brightness - 0.4f) < 1e-6))
        {
            await Task.Delay(50);
        }

        await Task.Delay(400);
        fx.LiveApplyDebouncer.Fired.Should().Be(1);
        (await client.Require().GetSceneAsync()).Assignments.Should().Contain(a => a.EffectId == "StaticColor" && Math.Abs(a.Brightness - 0.4f) < 1e-6);
        fx.LiveApply = false;
        fx.Brightness = 0.5f;
        await Task.Delay(400);
        fx.LiveApplyDebouncer.Fired.Should().Be(1, "LiveApply off: only the Apply button sends");

        // Scene page: group across fan → GPU → DRAM, applied as one continuous strip
        var sc = main.Scene;
        await sc.RefreshAsync();
        foreach (var id in new[] { "virtual:fan3", "virtual:gpu", "virtual:dram1" })
        {
            sc.Candidate = sc.AvailableZones.First(z => z.Controller.Id == id);
            sc.AddToGroupCommand.Execute(null);
        }

        sc.MoveUpCommand.Execute(sc.GroupZones[2]);
        sc.GroupZones.Select(z => z.Controller.Id).Should().Equal("virtual:fan3", "virtual:dram1", "virtual:gpu");
        sc.GroupName = "Runway all";
        await sc.ApplyGroupCommand.ExecuteAsync();
        sc.Assignments.Should().Contain(a => a.Dto.GroupName == "Runway all" && a.Preview != null && a.Preview.Layouts.Count == 3);
        host.Engine.Loop.Scene.Assignments.Should().Contain(a => a.EffectId == "Runway" && a.Targets.Count == 3);

        await sc.RemoveAssignmentCommand.ExecuteAsync(sc.Assignments.Single(a => a.Dto.GroupName == "Runway all"));
        sc.Assignments.Should().NotContain(a => a.Dto.GroupName == "Runway all");

        // Profiles page
        var pr = main.Profiles;
        pr.NewName = "Desk";
        await pr.SaveCommand.ExecuteAsync();
        pr.Profiles.Select(p => p.Name).Should().Contain("Desk");
        pr.Selected = pr.Profiles.Single(p => p.Name == "Desk");
        await pr.ApplyCommand.ExecuteAsync();
        host.Engine.ActiveProfileName.Should().Be("Desk");

        pr.NewName = "..\\evil";
        await pr.SaveCommand.ExecuteAsync();
        main.Error.Should().NotBeEmpty("the Host rejects the name and the error reaches the status bar");

        // Diagnostics export pulls the Host's packet ring buffer into the zip
        var zip = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        try
        {
            await main.Diagnostics.ExportAsync(zip);
            using var archive = ZipFile.OpenRead(zip);
            using var r = new StreamReader(archive.GetEntry("packets.json")!.Open());
            (await r.ReadToEndAsync()).Should().Contain("\"packets\"").And.Contain("\"Virtual\"");
            main.Diagnostics.ExportResult.Should().StartWith("Exported");
        }
        finally
        {
            File.Delete(zip);
        }

        // Lian Li actions with no Lian Li device: Re-sync is a no-op, Save is refused by the Host (D2 §5 gate)
        main.Devices.HasLianLi.Should().BeFalse();
        await main.Devices.ResyncLianLiCommand.ExecuteAsync();
        main.Devices.LianLiMessage.Should().Be("Re-sync: 0 loop(s) re-uploaded.");
        await main.Devices.SaveToFansCommand.ExecuteAsync();
        main.Error.Should().Contain("No Lian Li device is open");
        main.Bringup.HasSession.Should().BeFalse("nothing on this page began a bring-up session");
        main.Dispose();
    }

    [Fact]
    public async Task Debouncer_runs_once_after_the_last_trigger()
    {
        var runs = 0;
        using var d = new Debouncer(TimeSpan.FromMilliseconds(100), () => { Interlocked.Increment(ref runs); return Task.CompletedTask; }, e => throw e);
        for (var i = 0; i < 20; i++)
        {
            d.Trigger();
            await Task.Delay(5);
        }

        await Task.Delay(400);
        runs.Should().Be(1);
        d.Fired.Should().Be(1);
        d.Trigger();
        d.Cancel();
        await Task.Delay(250);
        runs.Should().Be(1, "a cancelled window never fires");
    }

    [Fact]
    public void Lian_li_preview_plays_the_compiled_loop_frames()
    {
        var gen = EffectRegistry.Default["Runway"];
        var p = new EffectParams([new Rgb(255, 255, 255)], 0.5f);
        var caps = EffectsViewModel.LianLiLoopCaps;
        var loop = PreviewRenderer.LoopFor(gen, p, caps);
        var timing = loop.Timing;
        timing.Frames.Should().BeInRange(2, 200);
        var preview = new PreviewRenderer(gen, [ZoneLayout.Linear(40)], p, loop);
        var period = preview.LoopSeconds;
        var frames = Core.Engine.LoopCompiler.Render(gen, Core.Layout.Projector.Project(ZoneLayout.Linear(40)), [.. p.Colors], p.Speed, p.Brightness, p.Direction, p.Seed,
            Core.Engine.LoopCompiler.GroupPeriod(Core.Engine.LoopCompiler.NaturalPeriod(gen, p), [caps], out _),
            [new Core.Engine.LoopTarget(0, 40, Core.Color.OutputStage.Identity, timing.Frames)])[0];
        foreach (var k in new[] { 0, 1, timing.Frames / 2, timing.Frames - 1 })
        {
            foreach (var wall in new[] { (k + 0.25) * timing.IntervalMs / 1000.0, (3 * timing.Frames * timing.IntervalMs / 1000.0) + ((k + 0.5) * timing.IntervalMs / 1000.0) })
            {
                var px = preview.Render(wall);
                for (var i = 0; i < 40; i++)
                {
                    var o = ((k * 40) + i) * 3;
                    (px[i].R, px[i].G, px[i].B).Should().Be((frames[o], frames[o + 1], frames[o + 2]), $"frame {k}, LED {i}, wall {wall:0.###} s");
                }
            }
        }

        new PreviewRenderer(gen, [ZoneLayout.Linear(40)], p).LoopTime(12.345).Should().Be(12.345, "streamed previews run continuously");
    }

    [Fact]
    public void Lian_li_preview_mirrors_the_loop_policy_and_plays_the_vendor_frames()
    {
        var gen = EffectRegistry.Default["Rainbow"];
        var p = new EffectParams([], 0.5f);
        ZoneDto[] tl = [new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3)];
        var plan = PreviewRenderer.VendorPlanFor(gen, p, tl, Core.Engine.LoopPolicy.LConnect)!;
        plan.Should().NotBeNull();
        var host = Core.Effects.LianLi.LConnectLoops.TryPlan("Rainbow", p, gen.Schema.DefaultColors, [new Core.Effects.LianLi.LConnectZone("TL", 3, 78)])!;
        plan.Zones[0].Rgb.Should().Equal(host.Zones[0].Rgb, "the preview uses the Host's planner");
        PreviewRenderer.VendorPlanFor(gen, p, tl, Core.Engine.LoopPolicy.Generic).Should().BeNull();
        PreviewRenderer.VendorPlanFor(gen, p, [tl[0] with { Family = null, Units = 0 }], Core.Engine.LoopPolicy.LConnect).Should().BeNull();

        var preview = new PreviewRenderer(gen, [ZoneLayout.Matrix(39, 2)], p, vendor: plan);
        (preview.Loop!.Timing.Frames, preview.Loop.Timing.IntervalMs).Should().Be((39, 55.0));
        var px = preview.Render((5 + 0.5) * 0.055);
        for (var i = 0; i < 78; i++)
        {
            var o = ((5 * 78) + i) * 3;
            (px[i].R, px[i].G, px[i].B).Should().Be((plan.Zones[0].Rgb[o], plan.Zones[0].Rgb[o + 1], plan.Zones[0].Rgb[o + 2]), $"frame 5, LED {i}");
        }
    }

    [Fact]
    public void Lian_li_preview_shows_the_hosts_four_chain_group_plan_for_every_route()
    {
        // WP-12 §9: TL3/TL3/H2/TL3 as the Host compiles it; the preview must play the same plan (shared slicing, per-zone timing)
        ZoneDto[] zones =
        [
            new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
            new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
            new ZoneDto(0, "H2", "Ring", 24, 0, 0, "H2", 1),
            new ZoneDto(0, "Chain (3 fans)", "Matrix", 78, 39, 2, "TL", 3),
        ];
        var hostZones = zones.Select(z => new Core.Effects.LianLi.LConnectZone(z.Family!, z.Units, z.LedCount)).ToArray();
        foreach (var effect in Core.Effects.LianLi.LConnectLoops.GroupRoutes.Keys)
        {
            var gen = EffectRegistry.Default[effect];
            var p = new EffectParams([], 0.5f);
            var plan = PreviewRenderer.VendorPlanFor(gen, p, zones, Core.Engine.LoopPolicy.LConnect);
            plan.Should().NotBeNull(effect);
            var host = Core.Effects.LianLi.LConnectLoops.TryPlan(effect, p, gen.Schema.DefaultColors, hostZones)!;
            plan!.Variant.Should().Be(host.Variant, effect);
            for (var z = 0; z < 4; z++)
            {
                (plan.Zones[z].Frames, plan.Zones[z].IntervalMs).Should().Be((host.Zones[z].Frames, host.Zones[z].IntervalMs), $"{effect} zone {z}");
                plan.Zones[z].Rgb.Should().Equal(host.Zones[z].Rgb, $"{effect} zone {z}: the same bytes the Host uploads");
            }
        }

        // unequal-period Twinkle: each zone steps at its own interval (TL 55 ms, H2 100 ms)
        var twinkle = EffectRegistry.Default["Twinkle"];
        var tp = PreviewRenderer.VendorPlanFor(twinkle, new EffectParams([], 0.5f), zones, Core.Engine.LoopPolicy.LConnect)!;
        tp.Synchronous.Should().BeFalse();
        var preview = new PreviewRenderer(twinkle, zones.Select(PreviewRenderer.ToLayout).ToArray(), new EffectParams([], 0.5f), vendor: tp);
        preview.LoopSeconds.Should().BeApproximately(220, 1e-9);
        foreach (var t in new[] { 0.03, 1.234, 10.99, 15.5, 47.0 })
        {
            var px = preview.Render(t);
            var o = 0;
            for (var z = 0; z < 4; z++)
            {
                var leds = zones[z].LedCount;
                var frame = (int)Math.Floor(t * 1000.0 / tp.Zones[z].IntervalMs) % 200;
                for (var i = 0; i < leds; i++, o++)
                {
                    var b = ((frame * leds) + i) * 3;
                    (px[o].R, px[o].G, px[o].B).Should().Be((tp.Zones[z].Rgb[b], tp.Zones[z].Rgb[b + 1], tp.Zones[z].Rgb[b + 2]), $"t {t} zone {z} LED {i}");
                }
            }
        }
    }

    [Fact]
    public void Loop_upload_fidelity_reports_complete_decimated_and_static_fallback()
    {
        LoopUploadDto Dto(int frames, int source, bool still) =>
            new(DateTimeOffset.UnixEpoch, 1, frames, 60, frames * 234, 100, 1, 1, still ? 4 : 0, "x", SourceFrames: source, StaticFallback: still);
        Dto(900, 900, false).Fidelity.Should().Be("complete");
        Dto(225, 900, false).Fidelity.Should().Be("decimated 900→225");
        Dto(1, 900, true).Fidelity.Should().Be("STATIC FALLBACK: 900 frames → 1 still frame");
    }

    [Fact]
    public async Task Gui_survives_a_host_restart()
    {
        var pipe = "UnifiedRgb-test-" + Guid.NewGuid().ToString("N");
        await using var client = new PipeClient(pipe, PipeTrust.ElevatedOwnerOrCurrentUser);
        var main = NewMain(client);
        var states = new List<ConnectionState>();
        client.StateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };
        client.Start();

        await using (var first = await TestHost.StartAsync(seed: o => o.PipeName = pipe))
        {
            (await client.WaitConnectedAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            (await client.Require().ListControllersAsync()).Should().HaveCount(9);
        }

        // Host gone: the client notices, the tray goes grey, calls fail fast instead of hanging
        await WaitUntilAsync(() => client.Service is null);
        await WaitUntilAsync(() => main.TrayState == TrayState.Disconnected);
        var call = () => client.Require();
        call.Should().Throw<InvalidOperationException>();

        await using var second = await TestHost.StartAsync(seed: o => o.PipeName = pipe);
        (await client.WaitConnectedAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        client.Connections.Should().Be(2);
        (await client.Require().ListControllersAsync()).Should().HaveCount(9);
        await WaitUntilAsync(() => main.Devices.Controllers.Count == 9 && main.TrayState == TrayState.Ok);

        // notifications are re-wired on the new session
        var status = new TaskCompletionSource<StatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, s) => status.TrySetResult(s);
        (await status.Task.WaitAsync(TimeSpan.FromSeconds(10))).Devices.Should().NotBeEmpty();
        lock (states)
        {
            states.Should().ContainInOrder(ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connected);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "condition was not reached in time");
            await Task.Delay(50);
        }
    }
}
