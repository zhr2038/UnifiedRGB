using System.IO;
using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class SceneCycleRegressionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_library_includes_usable_saved_scenes_even_without_readable_packages(bool packageReadFails)
    {
        var rgb = SceneCycleRgbProxy.Create();
        var state = (SceneCycleRgbProxy)rgb;
        state.Profiles.Add("A red", new("A red", Color("#FF0000")));
        state.Profiles.Add("B blue", new("B blue", Color("#0000FF")));
        state.Profiles.Add("Before Lights Off", new("Before Lights Off", Color("#00FF00")));
        state.Profiles.Add("Custom", new("Custom", Color("#00FF00")));
        state.Profiles.Add("Blackout", new("Blackout", Color("#000000")));
        state.Profiles.Add("Missing target", new("Missing target", new([new([new("offline", 0)], "StaticColor", ["#FFFFFF"])])));
        state.Profiles.Add("Screens", new("Screens", Color("#FFFFFF") with { Screens = new([]) }));
        var screens = new ScreenSceneDto([new("screen", BackgroundColor: "#101010")]);
        state.Scene = state.Profiles["A red"].Scene with { Screens = screens };
        var packages = SceneCyclePackageProxy.Create(state);
        ((SceneCyclePackageProxy)packages).ListFails = packageReadFails;
        var overlay = new KeyboardSceneCycleOverlay();
        await using var bridge = new KeyboardSceneCycleBridge(new() { DataDirectory = EmptyDataDirectory() }, () => rgb,
            () => packages, overlay, cycleOptions: new(TimeSpan.Zero));

        bridge.Browse(SceneCycleMove.Next).Should().BeTrue();
        await Until(() => overlay.Current?.Name == "B blue");
        overlay.Acknowledge(overlay.Apply(new byte[KeyboardSceneCycleOverlay.FrameBytes]));
        await Until(() => bridge.Current?.Phase == SceneCyclePhase.Accepted);

        bridge.Current!.Count.Should().Be(2);
        bridge.Current.Selection!.Name.Should().Be("B blue");
        state.Applied.Should().Equal("B blue");
        state.Scene.Screens.Should().BeSameAs(screens);
    }

    [Fact]
    public async Task Idle_browse_uses_actual_saved_scene_after_another_client_changes_it()
    {
        var rgb = SceneCycleRgbProxy.Create();
        var state = (SceneCycleRgbProxy)rgb;
        foreach (var (name, color) in new[] { ("A", "#FF0000"), ("B", "#00FF00"), ("C", "#0000FF") })
            state.Profiles.Add(name, new(name, Color(color)));
        state.Scene = state.Profiles["B"].Scene;
        using var cycle = Cycle(rgb, SceneCyclePackageProxy.Create(state), [SceneCycleEntry.Profile("A"), SceneCycleEntry.Profile("B"), SceneCycleEntry.Profile("C")]);

        (await cycle.RefreshAsync()).Selection!.Name.Should().Be("B");
        state.Applied.Should().BeEmpty();
        await cycle.MoveAsync(SceneCycleMove.Next);
        await cycle.Settled;
        state.Applied.Should().Equal("C");

        state.Scene = state.Profiles["A"].Scene; // another client's ordinary scene choice
        await cycle.MoveAsync(SceneCycleMove.Previous);
        await cycle.Settled;

        state.Applied.Should().Equal("C", "C");
        state.Scene.Assignments.Should().BeSameAs(state.Profiles["C"].Scene.Assignments);
        state.Scene.Assignments[0].Speed.Should().Be(.73f);
        state.Scene.Assignments[0].Seed.Should().Be(42);
    }

    [Fact]
    public async Task Dark_browse_holds_latest_profile_until_explicit_restore_apply()
    {
        var rgb = SceneCycleRgbProxy.Create();
        var state = (SceneCycleRgbProxy)rgb;
        state.Profiles.Add("A", new("A", Color("#FF0000")));
        state.Profiles.Add("B", new("B", Color("#0000FF")));
        state.Scene = Color("#000000");
        using var cycle = Cycle(rgb, SceneCyclePackageProxy.Create(state), [SceneCycleEntry.Profile("A"), SceneCycleEntry.Profile("B")]);
        await cycle.RefreshAsync();

        await cycle.MoveAsync(SceneCycleMove.Next);
        await cycle.Settled;
        await cycle.MoveAsync(SceneCycleMove.Next);
        await cycle.Settled;

        cycle.Current.Held.Should().BeTrue();
        cycle.Current.Selection!.Name.Should().Be("B");
        state.Applied.Should().BeEmpty();
        state.Scene = state.Profiles["A"].Scene;
        await cycle.ApplySelectedAsync();
        state.Applied.Should().Equal("B");
    }

    [Fact]
    public async Task Package_browse_keeps_existing_checked_binding_apply()
    {
        var rgb = SceneCycleRgbProxy.Create();
        var state = (SceneCycleRgbProxy)rgb;
        state.Scene = Color("#FF0000");
        var packages = SceneCyclePackageProxy.Create(state);
        using var cycle = Cycle(rgb, packages, [SceneCycleEntry.Package(SceneCyclePackageProxy.Content)]);
        await cycle.RefreshAsync();

        await cycle.MoveAsync(SceneCycleMove.Next);
        await cycle.Settled;

        cycle.Current.Phase.Should().Be(SceneCyclePhase.Applied);
        ((SceneCyclePackageProxy)packages).Applied.Should().ContainSingle().Which
            .Should().Be(new ScenePackageApplyRequestDto(SceneCyclePackageProxy.Content, SceneCyclePackageProxy.Binding));
        state.Applied.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "已接受")]
    [InlineData(true, "结果未知")]
    public async Task Caption_is_published_before_blocked_apply_and_then_reports_its_real_outcome(bool fail, string outcome)
    {
        var rgb = SceneCycleRgbProxy.Create();
        var state = (SceneCycleRgbProxy)rgb;
        state.Profiles.Add("A", new("A", Color("#FF0000")));
        state.Profiles.Add("B", new("B", Color("#0000FF")));
        state.Scene = state.Profiles["A"].Scene;
        var overlay = new KeyboardSceneCycleOverlay();
        var entered = new TaskCompletionSource<KeyboardSceneCycleCaption?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeApply = async () =>
        {
            entered.SetResult(overlay.Current);
            await release.Task;
            if (fail) throw new IOException("fake apply lost reply");
        };
        await using var bridge = new KeyboardSceneCycleBridge(new() { DataDirectory = EmptyDataDirectory() }, () => rgb,
            () => SceneCyclePackageProxy.Create(state), overlay, cycleOptions: new(TimeSpan.Zero));

        bridge.Browse(SceneCycleMove.Next).Should().BeTrue();
        await Until(() => overlay.Current?.Name == "B");
        overlay.Acknowledge(overlay.Apply(new byte[KeyboardSceneCycleOverlay.FrameBytes]));
        var caption = await entered.Task.WaitAsync(Patience);
        try
        {
            caption.Should().NotBeNull();
            caption!.Name.Should().Be("B");
            caption.Index.Should().Be(2);
            caption.State.Should().Be("切换中");
            caption.Strip.Length.Should().Be(KeyboardSceneCycleOverlay.StripBytes);
            release.Task.IsCompleted.Should().BeFalse();
            state.Scene.Should().BeSameAs(state.Profiles["A"].Scene);
        }
        finally
        {
            release.TrySetResult();
        }

        await Until(() => overlay.Current?.State == outcome);
        overlay.Current!.Name.Should().Be("B");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caption_transmission_orders_apply_after_exact_frame_or_bounded_unavailable_fallback(bool unavailable)
    {
        var rgb = SceneCycleRgbProxy.Create();
        var state = (SceneCycleRgbProxy)rgb;
        state.Profiles.Add("A", new("A", Color("#FF0000")));
        state.Profiles.Add("B", new("B", Color("#0000FF")));
        state.Scene = state.Profiles["A"].Scene;
        var overlay = new KeyboardSceneCycleOverlay();
        overlay.Show(new(-1, 1, 2, "old", "old", "old", new byte[KeyboardSceneCycleOverlay.StripBytes]));
        var staleFrame = overlay.Apply(new byte[KeyboardSceneCycleOverlay.FrameBytes]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeApply = async () => { entered.TrySetResult(); await release.Task; };
        var time = new ManualTimeProvider();
        await using var bridge = new KeyboardSceneCycleBridge(new() { DataDirectory = EmptyDataDirectory() }, () => rgb,
            () => SceneCyclePackageProxy.Create(state), overlay, time, cycleOptions: new(TimeSpan.Zero));

        bridge.Browse(SceneCycleMove.Next).Should().BeTrue();
        await Until(() => overlay.Current?.Name == "B" && bridge.Current?.Phase == SceneCyclePhase.Applying && time.ActiveTimers > 0);
        var frame = overlay.Apply(new byte[KeyboardSceneCycleOverlay.FrameBytes]);
        try
        {
            overlay.Current!.State.Should().Be("切换中");
            overlay.Acknowledge(new byte[KeyboardSceneCycleOverlay.FrameBytes]); // logo or unrelated frame
            overlay.Acknowledge(staleFrame);
            overlay.Acknowledge((byte[])frame.Clone()); // equal pixels are not this upload's exact frame
            entered.Task.IsCompleted.Should().BeFalse();
            state.Applied.Should().BeEmpty();
            if (unavailable) time.Advance(KeyboardSceneCycleBridge.ReadTimeout);
            else overlay.Acknowledge(frame);
            await entered.Task.WaitAsync(Patience);
            state.Applied.Should().BeEmpty(); // the fake scene apply itself is still blocked
        }
        finally
        {
            release.TrySetResult();
        }

        await Until(() => overlay.Current?.State == "已接受");
        state.Applied.Should().Equal("B");
        if (unavailable) bridge.Summary.Should().Contain("键盘提示发送延迟");
        else bridge.Trace.Should().Contain(t => t.Contains("transmission completed before scene apply", StringComparison.Ordinal));
    }

    private static SceneCycleCoordinator Cycle(IRgbService rgb, IScenePackageService packages, SceneCycleEntry[] entries) =>
        new(rgb, packages, CaseStatusSummary.IsBlackout, _ => Task.FromResult(new SceneCycleList(1, entries)), new(TimeSpan.Zero));

    private static SceneDto Color(string color) => new([new([new("live", 0)], "StaticColor", [color], Speed: .73f, Seed: 42, Strategy: "Program")]);

    private static string EmptyDataDirectory() => Path.Combine(Path.GetTempPath(), "UnifiedRgb-scene-cycle-no-files", Guid.NewGuid().ToString("N"));

    private static async Task Until(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!completed()) await Task.Delay(10, timeout.Token);
    }
}

public class SceneCycleRgbProxy : DispatchProxy
{
    public SceneDto Scene { get; set; } = new([]);
    public Dictionary<string, ProfileDto> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Applied { get; } = [];
    public Func<Task>? BeforeApply { get; set; }

    public static IRgbService Create() => Create<IRgbService, SceneCycleRgbProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IRgbService.GetSceneAsync) => Task.FromResult(Scene),
        nameof(IRgbService.ListProfilesAsync) => Task.FromResult<IReadOnlyList<ProfileInfoDto>>(Profiles.Keys.Select(n => new ProfileInfoDto(n, DateTimeOffset.UnixEpoch)).ToArray()),
        nameof(IRgbService.LoadProfileAsync) => Task.FromResult(Profiles[(string)args![0]!]),
        nameof(IRgbService.ListControllersAsync) => Task.FromResult<IReadOnlyList<ControllerDto>>([new("live", "Fake", "Fake", "Virtual", "Strip", "fake", [new(0, "zone", "Linear", 1, 1, 1)], 60, ControllerStates.Active, null)]),
        nameof(IRgbService.ListEffectsAsync) => Task.FromResult<IReadOnlyList<EffectDto>>([new("StaticColor", "Static", 1, 1, false, false, false, ["#FFFFFF"])]),
        nameof(IRgbService.ApplyProfileAsync) => Apply((string)args![0]!),
        _ => throw new InvalidOperationException("Unexpected fake RGB operation: " + targetMethod?.Name),
    };

    private async Task Apply(string name)
    {
        if (BeforeApply is { } before) await before();
        Applied.Add(name);
        var next = Profiles[name].Scene;
        Scene = next.Screens is null ? next with { Screens = Scene.Screens } : next;
    }
}

public class SceneCyclePackageProxy : DispatchProxy
{
    public const string Content = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string Binding = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public bool ListFails { get; set; }
    public List<ScenePackageApplyRequestDto> Applied { get; } = [];
    private SceneCycleRgbProxy? _rgb;

    public static IScenePackageService Create(SceneCycleRgbProxy rgb)
    {
        var proxy = Create<IScenePackageService, SceneCyclePackageProxy>();
        ((SceneCyclePackageProxy)proxy)._rgb = rgb;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IScenePackageService.ListScenePackagesAsync) => ListFails
            ? Task.FromException<IReadOnlyList<ScenePackageInfoDto>>(new IOException("fake unavailable"))
            : Task.FromResult<IReadOnlyList<ScenePackageInfoDto>>([]),
        nameof(IScenePackageService.GetScenePackageBindingsAsync) => Task.FromResult(new ScenePackageBindingStateDto(Content, Binding, [], true, [])),
        nameof(IScenePackageService.ApplyScenePackageCheckedAsync) => Apply((ScenePackageApplyRequestDto)args![0]!),
        _ => throw new InvalidOperationException("Unexpected fake package operation: " + targetMethod?.Name),
    };

    private Task<ScenePackageApplyDto> Apply(ScenePackageApplyRequestDto request)
    {
        Applied.Add(request);
        _rgb!.Scene = _rgb.Scene with { Package = new(request.ContentId, request.ExpectedBindingId, "rgb24-hold-physical-ms-v1") };
        return Task.FromResult(new ScenePackageApplyDto(true, request.ContentId, request.ExpectedBindingId, "fake accepted"));
    }
}
