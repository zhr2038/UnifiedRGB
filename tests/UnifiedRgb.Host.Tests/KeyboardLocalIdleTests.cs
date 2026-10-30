using System.Reflection;
using System.Runtime.CompilerServices;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Host;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

public sealed class KeyboardLocalIdleTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "urgb-local-idle-" + Guid.NewGuid().ToString("N"));
    private const string Asset = KeyboardDisplayServiceTests.AssetA;
    private const string Other = KeyboardDisplayServiceTests.AssetB;
    private static readonly string Timeline = KeyboardDisplayServiceTests.SharedTimelineId;
    private readonly ManualTimeProvider _time = new();
    private readonly SharedClock _clock = new() { Now = 1.5 };
    private readonly FakeKeyboardOledMedia _media = new();
    private readonly FakeSharedTimeline _timeline = new();

    private KeyboardDisplayAnimation Make(Func<string, int?>? duration = null) => new(
        new KeyboardDisplayOptions { AnimationAdmitted = true }, _data, _media, _timeline, _time, _clock, duration);

    [Fact]
    public async Task LocalIdle_empty_screen_scene_lists_library_assets_and_uses_master_epoch_without_scene_reads()
    {
        using var animation = Make(); var screens = KeyboardAnimationScreens.Create(_media);
        await animation.RefreshAsync(screens, default);
        animation.Status(true, false).Choices.Where(c => KeyboardDisplayAnimation.IsLocal(c.TimelineId)).Should().HaveCount(2);
        await animation.SaveAsync(Settings(KeyboardDisplayAnimation.LocalTimelineId(Asset)), screens, true, false, default);
        await Ready(animation);
        animation.Select(new byte[6656], true, false).Should().Equal(FakeKeyboardOledMedia.Bytes(Asset)[1]);
        var preview = await animation.PreviewAsync(new(Asset, KeyboardDisplayAnimation.LocalTimelineId(Asset), "contain",
            KeyboardDisplayAnimationEncodings.LumaRound), screens, default);
        Convert.FromBase64String(preview.Frames[0].PackedBase64).Should().Equal(FakeKeyboardOledMedia.Bytes(Asset)[0]);
        _timeline.Reads.Should().Be(0); ((KeyboardAnimationScreens)(object)screens).Mutations.Should().Be(0);
        _clock.Starts.Should().Be(0);
    }

    [Fact]
    public async Task LocalIdle_popup_returns_to_current_master_phase_despite_the_previous_upload_hold_gate()
    {
        using var animation = Make(); var screens = KeyboardAnimationScreens.Create(_media);
        await animation.SaveAsync(Settings(KeyboardDisplayAnimation.LocalTimelineId(Asset)) with { IntervalMs = 10000 }, screens, true, false, default);
        await Ready(animation);
        var presentation = new KeyboardNativePresentation(animation, null, _time);
        var status = new byte[6656];
        presentation.Native(status, true).Should().Equal(FakeKeyboardOledMedia.Bytes(Asset)[1]);
        presentation.Acknowledge();
        presentation.InteractionBegan(false);
        presentation.Native(status).Should().BeSameAs(status);
        _time.Advance(TimeSpan.FromMilliseconds(2600)); _clock.Now = 5.5;
        presentation.Native(status).Should().Equal(FakeKeyboardOledMedia.Bytes(Asset)[2]);
        _timeline.Reads.Should().Be(0);
    }

    [Fact]
    public async Task LocalIdle_invalid_asset_binding_changes_nothing_and_deleted_library_asset_is_not_kept_playing()
    {
        using var animation = Make(); var screens = KeyboardAnimationScreens.Create(_media);
        var wrong = Settings(KeyboardDisplayAnimation.LocalTimelineId(Other));
        await ((Func<Task>)(() => animation.SaveAsync(wrong, screens, true, false, default))).Should().ThrowAsync<ArgumentException>();
        File.Exists(Path.Combine(_data, "peripherals", KeyboardDisplayAnimation.FileName)).Should().BeFalse();
        await animation.SaveAsync(Settings(KeyboardDisplayAnimation.LocalTimelineId(Asset)), screens, true, false, default);
        await Ready(animation);
        var empty = DispatchProxy.Create<IScreenService, EmptyScreens>();
        await animation.RefreshAsync(empty, default);
        var fallback = new byte[6656]; animation.Select(fallback, true, false).Should().BeSameAs(fallback);
        animation.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.Unavailable);
    }

    [Fact]
    public async Task LocalIdle_real_scene_binding_requires_matching_duration_and_never_substitutes_master_clock()
    {
        _media.Scene = new([new("screen:test", Other, TimelineId: Timeline)]);
        using var animation = Make(id => id == Timeline ? 3000 : null); var screens = KeyboardAnimationScreens.Create(_media);
        var id = KeyboardDisplayAnimation.SceneTimelineId(Asset, Timeline);
        await animation.SaveAsync(Settings(id), screens, true, false, default); await Ready(animation);
        _timeline.At(2500); _clock.Now = 0;
        animation.Select(new byte[6656], true, false).Should().Equal(FakeKeyboardOledMedia.Bytes(Asset)[2]);
        animation.EndSession(); _timeline.Block("package RGB is off"); _clock.Now = 1.5;
        var fallback = new byte[6656]; animation.Select(fallback, true, false).Should().BeSameAs(fallback);
        animation.Status(true, false).Settings.TimelineId.Should().Be(id);
        animation.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.Fallback);
        animation.EndSession(); _media.Scene = new([]);
        animation.Select(fallback, true, false).Should().BeSameAs(fallback);
    }

    [Theory]
    [InlineData(16000)] [InlineData(0)]
    public async Task LocalIdle_scene_duration_mismatch_or_unknown_id_is_rejected_without_saving(int duration)
    {
        _media.Scene = new([new("screen:test", Other, TimelineId: Timeline)]);
        using var animation = Make(_ => duration); var screens = KeyboardAnimationScreens.Create(_media);
        await ((Func<Task>)(() => animation.SaveAsync(Settings(KeyboardDisplayAnimation.SceneTimelineId(Asset, Timeline)),
            screens, true, false, default))).Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(_data, "peripherals", KeyboardDisplayAnimation.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task LocalIdle_legacy_screen_mirror_keeps_its_binding_and_blocked_behavior()
    {
        _media.Scene = new([new("screen:test", Asset, TimelineId: Timeline)]);
        using var animation = Make(); var screens = KeyboardAnimationScreens.Create(_media);
        await animation.SaveAsync(Settings(Timeline), screens, true, false, default); await Ready(animation);
        _timeline.At(2500); animation.Select(new byte[6656], true, false).Should().Equal(FakeKeyboardOledMedia.Bytes(Asset)[2]);
        _timeline.Block("paused"); var fallback = new byte[6656];
        animation.Select(fallback, true, false).Should().BeSameAs(fallback);
        animation.Status(true, false).Settings.TimelineId.Should().Be(Timeline);
    }

    [Fact]
    public async Task LocalIdle_nonfinite_master_position_falls_back_without_restarting_or_using_a_scene_clock()
    {
        using var animation = Make(); var screens = KeyboardAnimationScreens.Create(_media);
        await animation.SaveAsync(Settings(KeyboardDisplayAnimation.LocalTimelineId(Asset)), screens, true, false, default); await Ready(animation);
        _clock.Now = double.NaN; var fallback = new byte[6656];
        animation.Select(fallback, true, false).Should().BeSameAs(fallback);
        animation.Status(true, false).Detail.Should().Contain("主时钟"); _timeline.Reads.Should().Be(0);
    }

    [Fact]
    public async Task LocalIdle_import_uses_one_existing_upload_and_selects_only_an_unsaved_local_draft()
    {
        Directory.CreateDirectory(_data); var path = Path.Combine(_data, "owner.gif"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var keyboard = new EditorKeyboard(); var screens = DispatchProxy.Create<IScreenService, ImportScreens>();
        var imports = (ImportScreens)(object)screens;
        var vm = new KeyboardAnimationViewModel(() => keyboard, InlineDispatcher.Instance, _ => { },
            screens: () => screens, chooseFile: () => path);
        await vm.RefreshAsync(); await vm.ImportAsync();
        imports.Uploads.Should().Be(1); imports.OtherMutations.Should().Be(0); keyboard.Saves.Should().Be(0);
        vm.SelectedChoice!.TimelineId.Should().Be(KeyboardDisplayAnimation.LocalTimelineId(Asset));
        vm.IsLocalSource.Should().BeTrue(); vm.Message.Should().Contain("尚未保存");
        vm.Sources.Should().Contain(s => s.Value == "scene");
        vm.SourceNote.Should().Contain("0 秒");
    }

    [Fact]
    public void LocalIdle_supplied_packed_art_keeps_all_16_frames_and_black_caption_rows_with_even_pixel_low_nibble()
    {
        var packed = File.ReadAllBytes(ArtworkPath());
        packed.Should().HaveCount(16 * 6656);
        for (var i = 0; i < 16; i++)
        {
            var frame = packed.AsSpan(i * 6656, 6656).ToArray();
            frame.AsSpan(47 * 104).ContainsAnyExcept((byte)0).Should().BeFalse();
            var decoded = KeyboardAnimationViewModel.Decode(new(i * 1000, Convert.ToBase64String(frame)));
            decoded.Levels[0].Should().Be((byte)(frame[0] & 15));
            decoded.Levels[1].Should().Be((byte)(frame[0] >> 4));
        }
    }

    private static string ArtworkPath([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,
        "../../assets/scenes/pikachu/peripheral-design-20261030/oled/gray4-frames.bin"));
    private static KeyboardDisplayAnimationSettingsDto Settings(string id) => new(true, Asset, id, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 2000);
    private static async Task Ready(KeyboardDisplayAnimation animation)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (animation.Status(true, false).CachedFrames == 0) await Task.Delay(5, stop.Token);
    }
    public void Dispose() { if (Directory.Exists(_data)) Directory.Delete(_data, true); }

    private sealed class SharedClock : IClock
    {
        public double Now { get; set; }
        public long Frame => 0;
        public int Starts { get; private set; }
        public event Action<double, double>? Tick { add { } remove { } }
        public void Start(int fps) => Starts++;
        public void Stop() => throw new InvalidOperationException("Shared clock is never stopped by keyboard playback.");
    }

    public class EmptyScreens : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "GetScreenSceneAsync" => Task.FromResult(new ScreenSceneDto([])),
            "ListScreenAssetsAsync" => Task.FromResult<IReadOnlyList<ScreenAssetDto>>([]),
            _ => throw new InvalidOperationException("No mutation"),
        };
    }
    public class ImportScreens : DispatchProxy
    {
        public int Uploads { get; private set; }
        public int OtherMutations { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "UploadScreenAssetAsync")
            { Uploads++; return Task.FromResult(new ScreenAssetDto(Asset, "owner.gif", 208, 64, 16, 16000, "animation")); }
            OtherMutations++; throw new InvalidOperationException("No other screen calls");
        }
    }
    private sealed class EditorKeyboard : IKeyboardDisplayService
    {
        public int Saves { get; private set; }
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<KeyboardDisplayAnimationStatusDto> GetKeyboardDisplayAnimationAsync(CancellationToken ct = default) => Task.FromResult(
            new KeyboardDisplayAnimationStatusDto(true, new(false, null, null, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 2000),
                KeyboardDisplayAnimationStates.Off, "设备已存动画", [new(Asset, KeyboardDisplayAnimation.LocalTimelineId(Asset), "", "owner.gif", 16, 16000, "animation")],
                1000, 10000, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, null));
        public Task<KeyboardDisplayAnimationStatusDto> SetKeyboardDisplayAnimationAsync(KeyboardDisplayAnimationSettingsDto settings, CancellationToken ct = default)
        { Saves++; throw new InvalidOperationException("No implicit save"); }
    }
}
