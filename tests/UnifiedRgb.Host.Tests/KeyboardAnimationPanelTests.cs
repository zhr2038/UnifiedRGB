using System.Reflection;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// The App side of the keyboard animation: the optional RPC through the shared contract JSON over an in-memory stream pair (no
/// pipe, no Host process), an old Host without the methods, one request per click, the preview decoded from the packed bytes,
/// the actual panel XAML, and the Host composition handing the owner the one shared timeline.
/// </summary>
[Collection("Host")]
public sealed class KeyboardAnimationPanelTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "urgb-keyboard-animation-app-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private static readonly string NewBindingTimelineId = "package:" + new string('a', 64) + ":" + new string('c', 64);
    private static readonly string OtherPackageTimelineId = "package:" + new string('d', 64) + ":" + new string('b', 64);
    private static readonly string[] PreselectionOrDisplayClaims = ["预选", "已选", "同一素材", "同一", "显示", "状态屏", "播放", "帧率", "FPS", "fps"];

    [Fact]
    public async Task An_old_host_without_the_methods_is_read_only_and_never_probed_again()
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        var old = new OldKeyboardRpc();
        server.AddLocalRpcTarget(old); // An older Host: only the status-page methods exist on the pipe.
        server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<IKeyboardDisplayService>();
        client.StartListening();
        (await proxy.GetKeyboardDisplayStatusAsync()).State.Should().Be("Stopped", "the old methods still work");
        var errors = new List<Exception>();
        var proxies = 0;
        var vm = new KeyboardAnimationViewModel(() => { proxies++; return proxy; }, InlineDispatcher.Instance, errors.Add, timeout: Guard);

        await vm.RefreshAsync();
        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        await vm.PreviewCommand.ExecuteAsync().WaitAsync(Guard);
        await vm.RefreshAsync();

        using (new AssertionScope())
        {
            vm.IsUnsupported.Should().BeTrue();
            vm.Message.Should().Be(KeyboardAnimationViewModel.UnsupportedMessage);
            vm.StateTitle.Should().Be("当前后台不支持键盘动画");
            (vm.CanEdit, vm.CanEnable, vm.CanPreview).Should().Be((false, false, false));
            proxies.Should().Be(1, "one probe; clicks on an unsupported Host send nothing");
            old.Calls.Should().Be(1);
            errors.Should().BeEmpty();
        }

        // In process, an implementation without the additive methods reports the same honest state.
        var local = new KeyboardAnimationViewModel(() => new OldKeyboardHost(), InlineDispatcher.Instance, errors.Add, timeout: Guard);
        await local.RefreshAsync();
        local.IsUnsupported.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task The_new_methods_round_trip_the_real_owner_and_each_click_sends_one_request()
    {
        var media = new FakeKeyboardOledMedia
        {
            Scene = new([
                new("screen:h2:a", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId),
                new("screen:h2:b", KeyboardDisplayServiceTests.AssetB, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId),
            ]),
        };
        var owner = Owner(media, admitted: true);
        var host = new CountingKeyboard(owner);
        var proxy = Connect(host);
        var errors = new List<Exception>();
        var vm = new KeyboardAnimationViewModel(() => proxy, InlineDispatcher.Instance, errors.Add, timeout: Guard);

        await vm.RefreshAsync();
        var direct = await owner.GetKeyboardDisplayAnimationAsync();
        using (new AssertionScope())
        {
            vm.Status.Should().BeEquivalentTo(direct, o => o.Excluding(s => s.UpdatedAt), "every field survives the shared contract JSON");
            vm.Status!.State.Should().Be(KeyboardDisplayAnimationStates.Off);
            vm.StateTitle.Should().Be("未开启（默认关闭）");
            vm.Choices.Select(c => c.AssetId).Should().Equal(KeyboardDisplayServiceTests.AssetA, KeyboardDisplayServiceTests.AssetB);
            vm.SelectedChoice!.AssetId.Should().Be(KeyboardDisplayServiceTests.AssetA, "the editor starts at the first listed asset");
            vm.Intervals.Select(i => i.Value).Should().Equal(2000, 3000, 5000, 10000);
            vm.IntervalNote.Should().Contain("不是帧率");
            (vm.CanEnable, vm.CanDisable, vm.CanPreview).Should().Be((true, false, true));
        }

        vm.SelectedChoice = vm.Choices[1];
        vm.SelectedFit = vm.Fits.Single(f => f.Value == "cover");
        vm.SelectedEncoding = vm.Encodings.Single(e => e.Value == KeyboardDisplayAnimationEncodings.LumaBayer4);
        vm.SelectedInterval = vm.Intervals.Single(i => i.Value == 3000);
        await vm.PreviewCommand.ExecuteAsync().WaitAsync(Guard);
        vm.Preview.Select(p => p.PositionMs).Should().Equal(0, 1000, 2000);
        vm.Preview[1].Levels.Should().OnlyContain(l => l == 5, "0x55 unpacks to level 5 in both nibbles");
        vm.PreviewMessage.Should().Contain("低半字节为偶数像素");
        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        var expected = new KeyboardDisplayAnimationSettingsDto(true, KeyboardDisplayServiceTests.AssetB, KeyboardDisplayServiceTests.SharedTimelineId,
            "cover", KeyboardDisplayAnimationEncodings.LumaBayer4, 3000);
        using (new AssertionScope())
        {
            host.Saves.Should().Equal(expected);
            vm.Status!.Settings.Should().Be(expected);
            vm.Status.State.Should().Be(KeyboardDisplayAnimationStates.PageOff, "the owner was never started: saved, but honestly not playing");
            vm.StateTitle.Should().Be("键盘状态屏已关闭：显示 ROG 原动画");
            vm.Message.Should().Contain("后台已答复");
            vm.EnableLabel.Should().Be("保存并保持开启");
            errors.Should().BeEmpty();
        }

        await vm.DisableCommand.ExecuteAsync().WaitAsync(Guard);
        host.Saves.Should().HaveCount(2);
        host.Saves[1].Should().Be(expected with { Enabled = false }, "disabling keeps the saved binding and options");
        vm.Status!.State.Should().Be(KeyboardDisplayAnimationStates.Off);

        // A Host refusal (the asset left the scene) changes nothing, is reported once and is read back, never resent.
        media.Scene = new([new("screen:h2:a", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]);
        await vm.RefreshAsync();
        vm.SelectedChoice = null; // a list refresh may clear the combo box; that is not an edit
        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        host.Saves.Should().HaveCount(3);
        host.Saves[2].AssetId.Should().Be(KeyboardDisplayServiceTests.AssetA, "the editor followed the saved settings to the remaining asset");
        errors.Should().BeEmpty();
        vm.SelectedChoice = new(KeyboardDisplayServiceTests.AssetB, KeyboardDisplayServiceTests.SharedTimelineId, "screen:h2:b", "动画 B", 3, 3000, "animation");
        var reads = host.Reads;
        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        using (new AssertionScope())
        {
            host.Saves.Should().HaveCount(4);
            errors.Should().ContainSingle().Which.Should().BeOfType<RemoteInvocationException>().Which.Message.Should().Contain("设置未保存");
            host.Reads.Should().Be(reads + 1, "one readback after the refusal");
            vm.Status!.Settings.AssetId.Should().Be(KeyboardDisplayServiceTests.AssetA);
            vm.Message.Should().Contain("后台拒绝了");
        }
    }

    [Fact]
    public void Preview_decoding_uses_the_native_nibble_order_and_rejects_wrong_lengths()
    {
        var packed = Enumerable.Repeat((byte)0x21, KeyboardOledFormat.PackedBytes).ToArray();
        packed[KeyboardOledFormat.PackedBytes - 1] = 0xF0;
        var frame = KeyboardAnimationViewModel.Decode(new(250, Convert.ToBase64String(packed)));
        frame.Levels.Take(4).Should().Equal(1, 2, 1, 2);
        frame.Levels[^2..].Should().Equal(0, 15);
        frame.Image.IsFrozen.Should().BeTrue();
        (frame.Image.PixelWidth, frame.Image.PixelHeight).Should().Be((208, 64));
        var gray = new byte[KeyboardOledFormat.Pixels];
        frame.Image.CopyPixels(gray, KeyboardOledFormat.Width, 0);
        gray.Take(2).Should().Equal(17, 34);
        gray[^1].Should().Be(255);
        frame.Label.Should().Be("循环内 250 ms");
        ((Action)(() => KeyboardAnimationViewModel.Decode(new(0, Convert.ToBase64String(new byte[6655])))))
            .Should().Throw<System.IO.InvalidDataException>();
    }

    [Fact]
    public async Task Actual_panel_xaml_shows_the_section_with_its_scope_and_native_limitation()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var errors = new List<Exception>();
        var panel = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var detached = Render(panel);
        var media = new FakeKeyboardOledMedia
        {
            Scene = new([new("screen:h2:a", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]),
        };
        var owner = Owner(media, admitted: true);
        var animation = new KeyboardAnimationViewModel(() => owner, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        panel.AttachKeyboardAnimation(animation);
        await animation.RefreshAsync();
        var attached = Render(panel);
        using (new AssertionScope())
        {
            detached.Texts.Should().Contain(("键盘屏幕动画", false));
            attached.Texts.Should().Contain(("键盘屏幕动画", true));
            attached.Texts.Should().Contain((KeyboardAnimationViewModel.Scope, true));
            attached.Texts.Should().Contain(("动画与场景灯光共用同一条共享时间轴，需单独开启（默认关闭）；不可用时的原因以后台给出的详情为准。", true));
            attached.Texts.Should().NotContain(t => t.Text.Contains("抑制", StringComparison.Ordinal) || t.Text.Contains("审核", StringComparison.Ordinal),
                "no universal suppression notice; an actual suppression comes from the backend state");
            attached.Texts.Should().Contain(("未开启（默认关闭）", true));
            attached.Buttons.Should().ContainKey("保存并开启键盘动画").WhoseValue.Should().BeTrue();
            attached.Buttons.Should().ContainKey("停用键盘动画").WhoseValue.Should().BeFalse();
            attached.Buttons.Should().ContainKey("生成预览").WhoseValue.Should().BeTrue();
            attached.Section.Should().Contain([animation.StatusLine, animation.Detail, animation.IntervalNote, animation.Message]);
            foreach (var text in attached.Section) text.Should().NotContainAny("FPS", "fps", "帧/秒", "帧率：");
            detached.Section.Should().BeEmpty();
            errors.Should().BeEmpty();
        }
        panel.Invoking(p => p.AttachKeyboardAnimation(animation)).Should().Throw<InvalidOperationException>("one section per panel");

        // An unrendered panel (command events must not reach the ended render thread) forwards the connection to the section.
        var other = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var section = new KeyboardAnimationViewModel(() => owner, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        other.AttachKeyboardAnimation(section);
        await section.RefreshAsync();
        section.CanEnable.Should().BeTrue();
        other.SetConnected(false);
        (section.CanEnable, section.CanDisable, section.CanPreview).Should().Be((false, false, false));
        section.StateTitle.Should().Be("后台未连接");
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task The_host_composition_hands_the_keyboard_owner_the_exact_shared_timeline_and_starts_unadmitted()
    {
        var options = new UnifiedRgbOptions { UseVirtualAdapter = false, DataDirectory = _data };
        var services = new ServiceCollection().AddLogging().AddUnifiedRgb(options, enableIpc: false);
        services.AddSingleton<IWriterGuard>(new Clear());
        services.AddSingleton<IRogWiredOledFactory>(new NoOled());
        services.AddSingleton<IRogWirelessOledFactory>(new NoWirelessOled());
        await using var provider = services.BuildServiceProvider();
        var keyboard = provider.GetRequiredService<KeyboardDisplayService>();
        var timeline = provider.GetRequiredService<ScenePackageTimeline>();
        var screens = provider.GetRequiredService<ScreenManager>();
        provider.GetRequiredService<IScreenTimeline>().Should().BeSameAs(timeline);
        provider.GetRequiredService<IKeyboardOledMedia>().Should().BeSameAs(screens);
        var animation = Field<KeyboardDisplayAnimation>(keyboard, "_animation");
        Field<IScreenTimeline>(animation, "_timeline").Should().BeSameAs(timeline, "no second clock: the one package timeline the screens use");
        Field<IKeyboardOledMedia>(animation, "_media").Should().BeSameAs(screens);
        Field<TimeProvider>(animation, "_time").Should().BeSameAs(provider.GetRequiredService<TimeProvider>());
        var status = await keyboard.GetKeyboardDisplayAnimationAsync();
        status.Should().Match<KeyboardDisplayAnimationStatusDto>(s => !s.Admitted && s.State == KeyboardDisplayAnimationStates.NotAdmitted
            && !s.Settings.Enabled && s.Choices.Count == 0 && s.Selected == 0);
        new KeyboardDisplayOptions().AnimationAdmitted.Should().BeFalse();
        new KeyboardDisplayOptions().AnimationMinimumIntervalMs.Should().Be(2000);
        File.Exists(Path.Combine(_data, "peripherals", KeyboardDisplayAnimation.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task Preselection_prefers_the_exact_pair_then_the_same_asset_then_the_first_choice_and_never_writes()
    {
        const string A = KeyboardDisplayServiceTests.AssetA, B = KeyboardDisplayServiceTests.AssetB;
        var shared = KeyboardDisplayServiceTests.SharedTimelineId;
        var errors = new List<Exception>();
        static KeyboardDisplayAnimationChoiceDto Choice(string asset, string timeline, string screen) => new(asset, timeline, screen, "动画", 3, 3000, "animation");
        static KeyboardDisplayAnimationSettingsDto Saved(bool enabled, string asset, string timeline) =>
            new(enabled, asset, timeline, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 2000);
        async Task Check(string because, KeyboardDisplayAnimationSettingsDto saved, string state, KeyboardDisplayAnimationChoiceDto[] choices,
            int? expected, bool rebind, string label)
        {
            var host = new ScriptedKeyboard(new(true, saved, state, "测试", choices, 2000, 10000, 0, 0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch, null));
            var vm = new KeyboardAnimationViewModel(() => host, InlineDispatcher.Instance, errors.Add, timeout: Guard);
            await vm.RefreshAsync();
            await vm.RefreshAsync();
            if (expected is { } index) vm.SelectedChoice.Should().Be(choices[index], because);
            else vm.SelectedChoice.Should().BeNull(because);
            (vm.NeedsRebind, vm.RebindHint, vm.EnableLabel).Should().Be((rebind, rebind ? KeyboardAnimationViewModel.RebindNote : string.Empty, label), because);
            (host.Reads, host.Writes).Should().Be((2, 0), because);
        }

        await Check("the exact pair wins over an earlier same-asset choice", Saved(true, A, NewBindingTimelineId), KeyboardDisplayAnimationStates.Ready,
            [Choice(A, OtherPackageTimelineId, "screen:h2:a"), Choice(A, NewBindingTimelineId, "screen:h2:b"), Choice(B, NewBindingTimelineId, "screen:h2:c")],
            1, false, "保存并保持开启");
        await Check("the same asset wins over the first listed choice", Saved(true, A, shared), KeyboardDisplayAnimationStates.Unavailable,
            [Choice(B, NewBindingTimelineId, "screen:h2:a"), Choice(A, NewBindingTimelineId, "screen:h2:b")], 1, true, KeyboardAnimationViewModel.RebindLabel);
        await Check("an asset that left the scene keeps the existing first-choice fallback", Saved(true, A, shared), KeyboardDisplayAnimationStates.Unavailable,
            [Choice(B, NewBindingTimelineId, "screen:h2:a"), Choice(B, OtherPackageTimelineId, "screen:h2:b")], 0, true, KeyboardAnimationViewModel.RebindLabel);
        await Check("nothing to choose offers no rebind", Saved(true, A, shared), KeyboardDisplayAnimationStates.Unavailable, [], null, false, "保存并保持开启");
        await Check("off keeps precedence", Saved(false, A, shared), KeyboardDisplayAnimationStates.Off,
            [Choice(B, NewBindingTimelineId, "screen:h2:a"), Choice(A, NewBindingTimelineId, "screen:h2:b")], 1, false, "保存并开启键盘动画");
        await Check("page off keeps precedence", Saved(true, A, shared), KeyboardDisplayAnimationStates.PageOff,
            [Choice(B, NewBindingTimelineId, "screen:h2:a"), Choice(A, NewBindingTimelineId, "screen:h2:b")], 1, false, "保存并保持开启");
        await Check("the opaque native owner keeps precedence", Saved(true, A, shared), KeyboardDisplayAnimationStates.SuppressedByNativeMenu,
            [Choice(B, NewBindingTimelineId, "screen:h2:a"), Choice(A, NewBindingTimelineId, "screen:h2:b")], 1, false, "保存并保持开启");
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_drifted_binding_preselects_the_same_asset_and_only_one_explicit_click_rebinds_it()
    {
        var (owner, sidecar) = DriftedOwner();
        var host = new CountingKeyboard(owner);
        var proxy = Connect(host);
        var errors = new List<Exception>();
        var vm = new KeyboardAnimationViewModel(() => proxy, InlineDispatcher.Instance, errors.Add, timeout: Guard);

        await vm.RefreshAsync();
        await vm.RefreshAsync();
        using (new AssertionScope())
        {
            vm.Status!.State.Should().Be(KeyboardDisplayAnimationStates.Unavailable);
            vm.Detail.Should().Contain("新的绑定快照").And.Contain("已保存的键盘设置未改变").And.NotContain("显示状态屏");
            vm.Choices.Select(c => c.AssetId).Should().Equal(KeyboardDisplayServiceTests.AssetB, KeyboardDisplayServiceTests.AssetA);
            vm.SelectedChoice.Should().Be(vm.Choices[1], "the same asset on the current shared timeline, not the first listed choice");
            (vm.NeedsRebind, vm.RebindHint, vm.EnableLabel).Should().Be((true, KeyboardAnimationViewModel.RebindNote, KeyboardAnimationViewModel.RebindLabel));
            vm.CanEnable.Should().BeTrue();
            host.Saves.Should().BeEmpty("reading, refreshing and preselecting never save");
            host.Reads.Should().Be(2);
            File.ReadAllBytes(SidecarPath()).Should().Equal(sidecar);
            errors.Should().BeEmpty();
        }

        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        var rebound = new KeyboardDisplayAnimationSettingsDto(true, KeyboardDisplayServiceTests.AssetA, NewBindingTimelineId, "contain",
            KeyboardDisplayAnimationEncodings.LumaRound, 2000);
        using (new AssertionScope())
        {
            host.Saves.Should().Equal(rebound);
            host.Reads.Should().Be(2, "an answered request is not read back");
            vm.Status!.Settings.Should().Be(rebound);
            vm.Status.State.Should().NotBe(KeyboardDisplayAnimationStates.Unavailable);
            (vm.NeedsRebind, vm.RebindHint, vm.EnableLabel).Should().Be((false, string.Empty, "保存并保持开启"));
            vm.Message.Should().Contain("后台已答复");
            File.ReadAllBytes(SidecarPath()).Should().Equal(System.Text.Encoding.UTF8.GetBytes("{\"Schema\":1,\"Enabled\":true,\"AssetId\":\""
                + KeyboardDisplayServiceTests.AssetA + "\",\"TimelineId\":\"" + NewBindingTimelineId
                + "\",\"Fit\":\"contain\",\"Encoding\":\"luma-round-v1\",\"IntervalMs\":2000}"), "the one explicit click writes the same schema-1 format");
            errors.Should().BeEmpty();
        }
        await vm.RefreshAsync();
        host.Saves.Should().HaveCount(1, "never resent");
        vm.SelectedChoice.Should().Be(vm.Choices[1], "now the exact saved pair");
    }

    [Fact]
    public async Task The_rebind_caption_is_hidden_when_off_shown_for_a_dirty_other_asset_and_hidden_after_one_explicit_rebind()
    {
        var proxy = DispatchProxy.Create<IRgbService, CasePowerRpc>();
        var actions = new CasePowerActions(() => proxy); _owned.Add(actions);
        var errors = new List<Exception>();

        // Default off (its own owner, created before the drifted sidecar exists): the caption is present but not shown.
        var offOwner = Owner(new FakeKeyboardOledMedia
        {
            Scene = new([new("screen:h2:a", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]),
        }, admitted: true);
        var offPanel = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var offSection = new KeyboardAnimationViewModel(() => offOwner, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        offPanel.AttachKeyboardAnimation(offSection);
        await offSection.RefreshAsync();
        offSection.Status!.State.Should().Be(KeyboardDisplayAnimationStates.Off);
        var off = RenderRebind(offPanel);
        using (new AssertionScope())
        {
            (off.Captions, off.Caption, off.CaptionShown).Should().Be((1, string.Empty, false));
            off.Panel.Buttons.Should().ContainKey("保存并开启键盘动画");
            off.Panel.Section.Should().NotContain(KeyboardAnimationViewModel.RebindNote);
        }

        // Drift: the user picks the OTHER asset; refreshes keep that draft and the shown hint stays selection-agnostic.
        var (owner, sidecar) = DriftedOwner();
        var host = new CountingKeyboard(owner);
        var rpc = Connect(host);
        var panel = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var section = new KeyboardAnimationViewModel(() => rpc, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        panel.AttachKeyboardAnimation(section);
        await section.RefreshAsync();
        section.SelectedChoice!.AssetId.Should().Be(KeyboardDisplayServiceTests.AssetA, "the same asset before any edit");
        section.SelectedChoice = section.Choices.Single(c => c.AssetId == KeyboardDisplayServiceTests.AssetB);
        await section.RefreshAsync();
        await section.RefreshAsync();
        using (new AssertionScope())
        {
            section.SelectedChoice!.AssetId.Should().Be(KeyboardDisplayServiceTests.AssetB, "the edited draft wins over adoption");
            (section.NeedsRebind, section.RebindHint, section.EnableLabel)
                .Should().Be((true, KeyboardAnimationViewModel.RebindNote, KeyboardAnimationViewModel.RebindLabel));
            section.RebindHint.Should().NotContainAny(PreselectionOrDisplayClaims);
            host.Saves.Should().BeEmpty("no click yet");
            host.Reads.Should().Be(3);
            File.ReadAllBytes(SidecarPath()).Should().Equal(sidecar);
        }
        var drifted = RenderRebind(panel); // nothing is invoked on this section after its render
        using (new AssertionScope())
        {
            (drifted.Captions, drifted.Caption, drifted.CaptionShown).Should().Be((1, KeyboardAnimationViewModel.RebindNote, true));
            drifted.Panel.Buttons.Should().ContainKey(KeyboardAnimationViewModel.RebindLabel).WhoseValue.Should().BeTrue();
            drifted.Panel.Section.Should().Contain([KeyboardAnimationViewModel.RebindNote, section.Detail, section.StatusLine, section.Message]);
            foreach (var text in drifted.Panel.Section) text.Should().NotContainAny("FPS", "fps", "帧/秒", "帧率：");
        }

        // A second, unrendered section on the same Host: dirty OTHER asset, then exactly one click; afterwards the caption is hidden.
        var clickPanel = new CaseControlPanelViewModel(actions, () => proxy, null, InlineDispatcher.Instance, errors.Add);
        var click = new KeyboardAnimationViewModel(() => rpc, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        clickPanel.AttachKeyboardAnimation(click);
        await click.RefreshAsync();
        click.SelectedChoice = click.Choices.Single(c => c.AssetId == KeyboardDisplayServiceTests.AssetB);
        await click.RefreshAsync();
        host.Saves.Should().BeEmpty();
        await click.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        host.Saves.Should().Equal(new KeyboardDisplayAnimationSettingsDto(true, KeyboardDisplayServiceTests.AssetB, NewBindingTimelineId, "contain",
            KeyboardDisplayAnimationEncodings.LumaRound, 2000));
        (click.NeedsRebind, click.RebindHint, click.EnableLabel).Should().Be((false, string.Empty, "保存并保持开启"));
        var rebound = RenderRebind(clickPanel);
        using (new AssertionScope())
        {
            (rebound.Captions, rebound.Caption, rebound.CaptionShown).Should().Be((1, string.Empty, false));
            rebound.Panel.Buttons.Should().ContainKey("保存并保持开启");
            rebound.Panel.Section.Should().NotContain(KeyboardAnimationViewModel.RebindNote);
            host.Saves.Should().HaveCount(1);
            errors.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Rebind_wording_keeps_the_old_host_freshness_disconnected_and_dirty_draft_guards()
    {
        var errors = new List<Exception>();
        var old = new KeyboardAnimationViewModel(() => new OldKeyboardHost(), InlineDispatcher.Instance, errors.Add, timeout: Guard);
        await old.RefreshAsync();
        await old.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        (old.IsUnsupported, old.NeedsRebind, old.RebindHint, old.EnableLabel, old.CanEnable)
            .Should().Be((true, false, string.Empty, "保存并开启键盘动画", false), "an old Host stays read-only without rebind wording");

        var (owner, sidecar) = DriftedOwner();
        var host = new CountingKeyboard(owner);
        var proxy = Connect(host);
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var vm = new KeyboardAnimationViewModel(() => proxy, InlineDispatcher.Instance, errors.Add, () => now, Guard);
        await vm.RefreshAsync();
        vm.NeedsRebind.Should().BeTrue();
        vm.SelectedChoice = vm.Choices.Single(c => c.AssetId == KeyboardDisplayServiceTests.AssetB);

        // An expired observation: no hint, no rebind label, and a click sends nothing; the draft is kept.
        now += TimeSpan.FromSeconds(11);
        vm.ExpireObservation();
        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        using (new AssertionScope())
        {
            vm.Status.Should().BeNull();
            (vm.NeedsRebind, vm.RebindHint, vm.CanEnable, vm.EnableLabel).Should().Be((false, string.Empty, false, "保存并开启键盘动画"));
            vm.SelectedChoice!.AssetId.Should().Be(KeyboardDisplayServiceTests.AssetB);
            host.Saves.Should().BeEmpty();
        }

        // A new read shows the hint again without adopting over the draft.
        await vm.RefreshAsync();
        (vm.NeedsRebind, vm.SelectedChoice!.AssetId).Should().Be((true, KeyboardDisplayServiceTests.AssetB));

        // Disconnected: nothing is known or sent; reconnecting drops the draft and adopts the same asset again.
        vm.SetConnected(false);
        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        (vm.NeedsRebind, vm.RebindHint, vm.CanEnable, vm.StateTitle).Should().Be((false, string.Empty, false, "后台未连接"));
        vm.SetConnected(true);
        await vm.RefreshAsync();
        using (new AssertionScope())
        {
            (vm.NeedsRebind, vm.SelectedChoice!.AssetId, vm.SelectedChoice.TimelineId)
                .Should().Be((true, KeyboardDisplayServiceTests.AssetA, NewBindingTimelineId));
            host.Saves.Should().BeEmpty("no click reached the Host");
            File.ReadAllBytes(SidecarPath()).Should().Equal(sidecar);
            errors.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task An_unanswered_rebind_is_read_back_once_and_never_resent()
    {
        var (owner, _) = DriftedOwner();
        var host = new CountingKeyboard(owner);
        var lost = new AnswerLostKeyboard(Connect(host));
        var errors = new List<Exception>();
        var vm = new KeyboardAnimationViewModel(() => lost, InlineDispatcher.Instance, errors.Add, timeout: Guard);
        await vm.RefreshAsync();
        (vm.NeedsRebind, vm.SelectedChoice!.AssetId).Should().Be((true, KeyboardDisplayServiceTests.AssetA));
        var reads = host.Reads;

        await vm.EnableCommand.ExecuteAsync().WaitAsync(Guard);
        using (new AssertionScope())
        {
            lost.Sets.Should().Be(1, "one request per click");
            host.Saves.Should().ContainSingle().Which.TimelineId.Should().Be(NewBindingTimelineId);
            host.Reads.Should().Be(reads + 1, "one readback of the unknown outcome");
            vm.Message.Should().Be("保存并开启键盘动画结果未确认：未收到后台答复，未自动重发；以上为回读状态。");
            vm.Status!.Settings.TimelineId.Should().Be(NewBindingTimelineId, "the readback shows that the request applied");
            (vm.NeedsRebind, vm.RebindHint).Should().Be((false, string.Empty));
            errors.Should().BeEmpty();
        }
        await vm.RefreshAsync();
        (lost.Sets, host.Saves.Count).Should().Be((1, 1), "never resent");
    }

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        try { Directory.Delete(_data, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private KeyboardDisplayService Owner(FakeKeyboardOledMedia media, bool admitted)
    {
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
        var service = new KeyboardDisplayService(new UnifiedRgbOptions
        {
            DataDirectory = _data,
            KeyboardDisplay = new() { AnimationAdmitted = admitted },
        }, rgb, KeyboardAnimationScreens.Create(media), new NoOled(), new Clear(), keyboardMedia: media, sharedTimeline: new FakeSharedTimeline(),
            time: new ManualTimeProvider());
        _owned.Add(service);
        return service;
    }

    // The saved enabled binding (A, shared timeline) as exact schema-1 bytes; the current scene plays B and A on the same package's new
    // binding snapshot, B listed first. Admitted with the status page on (not started: no device is opened), so the status is the binding outcome.
    private (KeyboardDisplayService Owner, byte[] Sidecar) DriftedOwner()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath())!);
        var sidecar = KeyboardDisplayServiceTests.AnimationFile(enabled: true);
        File.WriteAllBytes(SidecarPath(), sidecar);
        var media = new FakeKeyboardOledMedia
        {
            Scene = new([
                new("screen:h2:a", KeyboardDisplayServiceTests.AssetB, TimelineId: NewBindingTimelineId),
                new("screen:h2:b", KeyboardDisplayServiceTests.AssetA, TimelineId: NewBindingTimelineId),
            ]),
        };
        var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
        var service = new KeyboardDisplayService(new UnifiedRgbOptions
        {
            DataDirectory = _data,
            KeyboardDisplay = new()
            {
                Enabled = true, ControlVerified = true, DeviceKey = new string('A', 64), FirmwareHeader = "001200000024000100", AnimationAdmitted = true,
            },
        }, rgb, KeyboardAnimationScreens.Create(media), new NoOled(), new Clear(), keyboardMedia: media, sharedTimeline: new FakeSharedTimeline(),
            time: new ManualTimeProvider());
        _owned.Add(service);
        return (service, sidecar);
    }

    private string SidecarPath() => Path.Combine(_data, "peripherals", KeyboardDisplayAnimation.FileName);

    private IKeyboardDisplayService Connect(IKeyboardDisplayService target)
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        server.AddLocalRpcTarget(target, null);
        server.StartListening();
        var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<IKeyboardDisplayService>();
        client.StartListening();
        _owned.Add(client); _owned.Add(server);
        return proxy;
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    // Loads the actual panel XAML on an STA thread without a window; Shown means the element and every logical ancestor are visible.
    private static Rendered Render(CaseControlPanelViewModel vm)
    {
        var buttons = new Dictionary<string, bool>();
        var texts = new List<(string Text, bool Shown)>();
        var section = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = vm };
                view.Measure(new(300, 2400)); view.Arrange(new(0, 0, 300, 2400)); view.UpdateLayout();
                foreach (var (button, shown) in Logical<System.Windows.Controls.Button>(view, true))
                    if (shown && button.DataContext is KeyboardAnimationViewModel) buttons[button.Content?.ToString() ?? string.Empty] = button.IsEnabled;
                foreach (var (text, shown) in Logical<System.Windows.Controls.TextBlock>(view, true))
                {
                    texts.Add((text.Text, shown));
                    if (shown && text.DataContext is KeyboardAnimationViewModel) section.Add(text.Text);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
        return new(buttons, texts, section);
    }

    private static IEnumerable<(T Element, bool Shown)> Logical<T>(System.Windows.DependencyObject root, bool shown)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            var visible = shown && (child is not System.Windows.UIElement element || element.Visibility == System.Windows.Visibility.Visible);
            if (child is T match) yield return (match, visible);
            foreach (var nested in Logical<T>(child, visible)) yield return nested;
        }
    }

    // Section: the shown texts whose data context is the keyboard animation view model.
    private sealed record Rendered(Dictionary<string, bool> Buttons, List<(string Text, bool Shown)> Texts, List<string> Section);

    // Like Render, and also finds every TextBlock whose Text is bound to RebindHint: its text and whether it is shown.
    private static RebindRender RenderRebind(CaseControlPanelViewModel vm)
    {
        var buttons = new Dictionary<string, bool>();
        var texts = new List<(string Text, bool Shown)>();
        var section = new List<string>();
        var captions = 0;
        string? caption = null;
        var captionShown = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new App.Views.CaseControlPanelView { DataContext = vm };
                view.Measure(new(300, 2400)); view.Arrange(new(0, 0, 300, 2400)); view.UpdateLayout();
                foreach (var (button, shown) in Logical<System.Windows.Controls.Button>(view, true))
                    if (shown && button.DataContext is KeyboardAnimationViewModel) buttons[button.Content?.ToString() ?? string.Empty] = button.IsEnabled;
                foreach (var (text, shown) in Logical<System.Windows.Controls.TextBlock>(view, true))
                {
                    texts.Add((text.Text, shown));
                    if (shown && text.DataContext is KeyboardAnimationViewModel) section.Add(text.Text);
                    if (System.Windows.Data.BindingOperations.GetBinding(text, System.Windows.Controls.TextBlock.TextProperty)?.Path?.Path
                        == nameof(KeyboardAnimationViewModel.RebindHint))
                    {
                        captions++; caption = text.Text; captionShown = shown;
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
        return new(new(buttons, texts, section), captions, caption, captionShown);
    }

    private sealed record RebindRender(Rendered Panel, int Captions, string? Caption, bool CaptionShown);

    /// <summary>An older Host's pipe target: the status-page methods only.</summary>
    private sealed class OldKeyboardRpc
    {
        public int Calls { get; private set; }
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default)
        { Calls++; return Task.FromResult(new KeyboardDisplayStatusDto(false, "Stopped", "键盘状态屏未启动", 0, 0, null, null)); }
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("No writes."); }
    }

    /// <summary>An implementation compiled against the contract without overriding the additive animation methods.</summary>
    private sealed class OldKeyboardHost : IKeyboardDisplayService
    {
        public int Calls { get; private set; }
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default)
        { Calls++; return Task.FromResult(new KeyboardDisplayStatusDto(false, "Stopped", "键盘状态屏未启动", 0, 0, null, null)); }
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) => throw new InvalidOperationException("No writes.");
    }

    /// <summary>Delegates to the real owner and records every animation request in order.</summary>
    private sealed class CountingKeyboard(IKeyboardDisplayService inner) : IKeyboardDisplayService
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public List<KeyboardDisplayAnimationSettingsDto> Saves { get; } = [];
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => inner.GetKeyboardDisplayStatusAsync(ct);
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) => throw new InvalidOperationException("No page writes.");
        public Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default)
        { Interlocked.Increment(ref _reads); return inner.GetKeyboardDisplayAnimationAsync(ct); }
        public Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default)
        { lock (Saves) Saves.Add(settings); return inner.SetKeyboardDisplayAnimationAsync(settings, ct); }
        public Task<KeyboardDisplayAnimationPreviewDto> PreviewKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationPreviewRequestDto request, CancellationToken ct = default) =>
            inner.PreviewKeyboardDisplayAnimationAsync(request, ct);
    }

    /// <summary>Answers every animation read with one fixed status; any write is counted and refused.</summary>
    private sealed class ScriptedKeyboard(KeyboardDisplayAnimationStatusDto status) : IKeyboardDisplayService
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => throw new InvalidOperationException("No page reads.");
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) { Writes++; throw new InvalidOperationException("No page writes."); }
        public Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default) { Reads++; return Task.FromResult(status); }
        public Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default)
        { Writes++; throw new InvalidOperationException("No animation writes."); }
        public Task<KeyboardDisplayAnimationPreviewDto> PreviewKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationPreviewRequestDto request, CancellationToken ct = default)
        { Writes++; throw new InvalidOperationException("No previews."); }
    }

    /// <summary>Forwards everything; each save reaches the Host, but its answer is lost on the way back (an unknown outcome).</summary>
    private sealed class AnswerLostKeyboard(IKeyboardDisplayService inner) : IKeyboardDisplayService
    {
        private int _sets;
        public int Sets => Volatile.Read(ref _sets);
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => inner.GetKeyboardDisplayStatusAsync(ct);
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) => throw new InvalidOperationException("No page writes.");
        public Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default) => inner.GetKeyboardDisplayAnimationAsync(ct);
        public async Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _sets);
            await inner.SetKeyboardDisplayAnimationAsync(settings, ct);
            throw new IOException("The answer was lost after the request reached the Host.");
        }
        public Task<KeyboardDisplayAnimationPreviewDto> PreviewKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationPreviewRequestDto request, CancellationToken ct = default) =>
            inner.PreviewKeyboardDisplayAnimationAsync(request, ct);
    }

    private sealed class NoOled : IRogWiredOledFactory
    {
        public RogWiredOledConnection? TryOpen(string key) => throw new InvalidOperationException("No keyboard opens.");
    }

    private sealed class NoWirelessOled : IRogWirelessOledFactory
    {
        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader) => throw new InvalidOperationException("No keyboard opens.");
    }

    private sealed class Clear : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover.");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover.");
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException("No takeover.");
    }
}
