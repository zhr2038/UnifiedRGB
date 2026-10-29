using System.Reflection;
using System.Text;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

// M1 binding-drift transparency (status wording only) on the actual KeyboardDisplayAnimation and the published owner wrapper, over the
// existing in-memory fakes: why a saved (asset, timeline) pair is unavailable, an Unbound owner decision while the cached choice list is
// stale, the unchanged precedence of the earlier states, and the exact schema-1 sidecar bytes around every read, status and refresh.
public sealed partial class KeyboardDisplayServiceTests
{
    private static readonly string M1NewBindingTimelineId = "package:" + new string('a', 64) + ":" + new string('c', 64);
    private static readonly string M1OtherPackageTimelineId = "package:" + new string('d', 64) + ":" + new string('b', 64);
    private static readonly string[] M1MalformedTimelineIds =
    [
        "package:" + new string('A', 64) + ":" + new string('c', 64),
        "package:" + new string('a', 64),
        "package:" + new string('a', 64) + ":",
        "package:" + new string('a', 64) + ":" + new string('c', 63),
        "package:" + new string('a', 64) + ":" + new string('c', 64) + ":" + new string('c', 64),
        "Package:" + new string('a', 64) + ":" + new string('c', 64),
        " package:" + new string('a', 64) + ":" + new string('c', 64),
        "screen:" + new string('a', 64),
        "",
    ];
    private static readonly string[] M1DisplayClaims = ["显示", "状态屏", "播放", "帧率", "FPS", "fps", "帧/秒", "预选"];
    private const string M1Generic = "所选素材不在当前场景的共享时间轴上；显示状态屏";
    private const string M1NewBinding = "键盘动画绑定不可用：所选素材仍在当前场景中，但同一场景包已改用新的绑定快照，共享时间轴随之改变；已保存的键盘设置未改变；需要明确重新绑定";
    private const string M1OtherPackage = "键盘动画绑定不可用：所选素材现在位于另一个场景包的共享时间轴上；已保存的键盘设置未改变；需要明确重新绑定";
    private const string M1Stale = "键盘动画绑定暂不可用：最近一次检查发现，当前屏幕场景已不含所选素材与已保存共享时间轴的组合（可选素材列表尚未更新）；已保存的键盘设置未改变；场景未恢复时需要明确重新绑定";

    [Theory]
    [InlineData("same-package-new-binding")]
    [InlineData("other-package")]
    [InlineData("asset-missing")]
    [InlineData("malformed-current-ids")]
    public async Task M1_an_unlisted_saved_pair_gets_a_conservative_detail_and_reads_leave_settings_and_schema1_bytes_unchanged(string drift)
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        var saved = Enable(AssetA);
        (await service.SetKeyboardDisplayAnimationAsync(saved)).Settings.Should().Be(saved);
        await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Ready, "the saved binding prepared");
        var bytes = File.ReadAllBytes(AnimationPath());
        bytes.Should().Equal(M1SchemaOne(true, AssetA, SharedTimelineId), "an explicit save writes exactly the schema-1 sidecar");
        bytes.Should().Equal(AnimationFile(enabled: true));
        var written = File.GetLastWriteTimeUtc(AnimationPath());

        var malformed = M1MalformedTimelineIds.Select((id, i) => new ScreenAssignmentDto("screen:h2:m" + i, AssetA, TimelineId: id));
        var (scene, expected, listsA) = drift switch
        {
            "same-package-new-binding" => (new ScreenSceneDto([new("screen:h2:test", AssetA, TimelineId: M1NewBindingTimelineId),
                new("screen:h2:zwei", AssetB, TimelineId: M1NewBindingTimelineId)]), M1NewBinding, true),
            "other-package" => (new ScreenSceneDto([new("screen:h2:test", AssetA, TimelineId: M1OtherPackageTimelineId)]), M1OtherPackage, true),
            "asset-missing" => (new ScreenSceneDto([new("screen:h2:zwei", AssetB, TimelineId: M1NewBindingTimelineId)]), M1Generic, false),
            _ => (new ScreenSceneDto([.. malformed, new("screen:h2:zwei", AssetB, TimelineId: M1NewBindingTimelineId)]), M1Generic, false),
        };
        rig.Media.Scene = scene;
        var reads = new List<KeyboardDisplayAnimationStatusDto>();
        for (var i = 0; i < 3; i++) reads.Add(await service.GetKeyboardDisplayAnimationAsync()); // each read refreshes, reconciles and reports

        foreach (var status in reads)
        {
            status.State.Should().Be(KeyboardDisplayAnimationStates.Unavailable);
            status.Detail.Should().Be(expected);
            status.Settings.Should().Be(saved, "a read never changes the saved preference");
            (status.Admitted, status.LastError).Should().Be((true, (string?)null));
            status.Choices.Should().NotContain(c => c.AssetId == AssetA && c.TimelineId == SharedTimelineId);
            status.Choices.Any(c => c.AssetId == AssetA).Should().Be(listsA, "a malformed timeline id is never listed");
        }
        if (expected != M1Generic) expected.Should().NotContainAny(M1DisplayClaims);
        File.ReadAllBytes(AnimationPath()).Should().Equal(bytes, "reads leave the exact schema-1 bytes");
        File.GetLastWriteTimeUtc(AnimationPath()).Should().Be(written);
        File.Exists(AnimationPath() + ".tmp").Should().BeFalse();
        (rig.Media.Prepares, rig.Screens.Mutations).Should().Be((1, 0), "no new preparation and no scene or asset change");
    }

    [Fact]
    public async Task M1_a_malformed_saved_timeline_id_is_never_matched_stays_a_settings_error_and_the_file_is_untouched()
    {
        var rig = new AnimationRig(this);
        // A loose prefix or split match of several malformed ids would "find" asset A on the same content here.
        rig.Media.Scene = new([new("screen:h2:test", AssetA, TimelineId: M1NewBindingTimelineId), new("screen:h2:zwei", AssetA, TimelineId: SharedTimelineId)]);
        Directory.CreateDirectory(Path.GetDirectoryName(AnimationPath())!);
        foreach (var malformed in M1MalformedTimelineIds)
        {
            var bytes = M1SchemaOne(true, AssetA, malformed);
            File.WriteAllBytes(AnimationPath(), bytes);
            using var service = rig.Make(Admitted());
            for (var i = 0; i < 2; i++)
            {
                var status = await service.GetKeyboardDisplayAnimationAsync();
                status.State.Should().Be(KeyboardDisplayAnimationStates.SettingsError);
                status.Detail.Should().Be("键盘动画设置无法读取；动画保持关闭，原文件未被覆盖");
                status.Settings.Enabled.Should().BeFalse();
            }
            File.ReadAllBytes(AnimationPath()).Should().Equal(bytes, "an unreadable sidecar is kept exactly as it is");
        }
        rig.Media.Prepares.Should().Be(0);
    }

    [Fact]
    public void M1_the_pure_classifier_compares_only_parseable_ids_and_never_infers_a_match_from_malformed_ones()
    {
        var method = typeof(KeyboardDisplayAnimation).GetMethod("BindingUnavailableDetail", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        string Classify(KeyboardDisplayAnimationSettingsDto settings, params KeyboardDisplayAnimationChoiceDto[] choices) =>
            (string)method!.Invoke(null, [settings, choices])!;
        static KeyboardDisplayAnimationChoiceDto Choice(string asset, string timeline) => new(asset, timeline, "screen:h2:test", "动画", 3, 3000, "animation");
        var saved = Enable(AssetA);

        Classify(saved, Choice(AssetA, M1NewBindingTimelineId)).Should().Be(M1NewBinding);
        Classify(saved, Choice(AssetA, M1OtherPackageTimelineId)).Should().Be(M1OtherPackage, "another ContentId, even with the same binding hash text");
        Classify(saved, Choice(AssetA, M1OtherPackageTimelineId), Choice(AssetA, M1NewBindingTimelineId)).Should().Be(M1NewBinding);
        Classify(saved, Choice(AssetB, M1NewBindingTimelineId)).Should().Be(M1Generic, "only the saved asset is compared");
        Classify(saved).Should().Be(M1Generic);
        Classify(saved, Choice(AssetA, SharedTimelineId)).Should().Be(M1Stale, "the cached list still holds the exact pair");
        foreach (var bad in M1MalformedTimelineIds)
        {
            Classify(saved, Choice(AssetA, bad), Choice(AssetB, M1NewBindingTimelineId)).Should().Be(M1Generic, "a malformed current id is never matched");
            Classify(saved with { TimelineId = bad }, Choice(AssetA, M1NewBindingTimelineId), Choice(AssetA, M1OtherPackageTimelineId))
                .Should().Be(M1Generic, "a malformed saved id is never matched");
        }
        Classify(saved with { AssetId = null }, Choice(AssetA, M1NewBindingTimelineId)).Should().Be(M1Generic);
        Classify(saved with { TimelineId = null }, Choice(AssetA, M1NewBindingTimelineId)).Should().Be(M1Generic);
        foreach (var text in new[] { M1NewBinding, M1OtherPackage, M1Stale })
            text.Should().Contain("已保存的键盘设置未改变").And.Contain("明确重新绑定").And.NotContainAny(M1DisplayClaims);
    }

    [Fact]
    public async Task M1_an_unbound_owner_decision_with_a_stale_choice_list_reports_unavailable_not_ready_and_changes_nothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AnimationPath())!);
        var bytes = M1SchemaOne(true, AssetA, SharedTimelineId);
        File.WriteAllBytes(AnimationPath(), bytes);
        var live = new FakeKeyboardOledMedia { Scene = AnimationRig.BothScene }; // the owner path's live scene (CurrentReferences)
        var observed = new FakeKeyboardOledMedia { Scene = AnimationRig.BothScene }; // the screen-service observation behind the choices
        var screens = KeyboardAnimationScreens.Create(observed);
        var time = new ManualTimeProvider();
        using var animation = new KeyboardDisplayAnimation(Admitted(), _directory, live, new FakeSharedTimeline(), time);
        var status = Fill(0x99);
        await animation.RefreshAsync(screens, default);
        await UntilAsync(() => animation.Status(true, false).State == KeyboardDisplayAnimationStates.Ready, "the saved binding prepared");
        animation.Select(status, true, false).Should().Equal(Fill(0x11), "bound on a known timeline: the frame for shared position 0");
        animation.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.Playing);

        // The live scene drops the pair while the observation behind the cached choices still lists it.
        live.Scene = new([new("screen:h2:test", AssetA, TimelineId: M1NewBindingTimelineId)]);
        await animation.RefreshAsync(screens, default);
        animation.Select(status, true, false).Should().BeSameAs(status, "Unbound: the owner returns the caller's own bytes");
        var unbound = animation.Status(true, false);
        unbound.State.Should().Be(KeyboardDisplayAnimationStates.Unavailable);
        unbound.State.Should().NotBe(KeyboardDisplayAnimationStates.Ready);
        unbound.Detail.Should().Be(M1Stale);
        unbound.Detail.Should().NotContainAny(M1DisplayClaims);
        unbound.Choices.Should().Contain(c => c.AssetId == AssetA && c.TimelineId == SharedTimelineId, "the cached choice list is stale");
        unbound.Settings.Should().Be(Enable(AssetA));
        animation.Status(false, false).Should().Match<KeyboardDisplayAnimationStatusDto>(s => s.State == KeyboardDisplayAnimationStates.PageOff
            && s.Detail == "键盘状态屏已关闭，键盘显示 ROG 原动画；开启状态屏后才会播放");
        animation.Status(true, true).State.Should().Be(KeyboardDisplayAnimationStates.SuppressedByNativeMenu);

        // A fresh observation without the pair classifies it.
        observed.Scene = live.Scene;
        await animation.RefreshAsync(screens, default);
        animation.Status(true, false).Should().Match<KeyboardDisplayAnimationStatusDto>(s => s.State == KeyboardDisplayAnimationStates.Unavailable
            && s.Detail == M1NewBinding);

        // The scene returns: the same prepared loop resumes at the cadence gate, with no save in between.
        live.Scene = AnimationRig.BothScene;
        observed.Scene = AnimationRig.BothScene;
        await animation.RefreshAsync(screens, default);
        animation.Select(status, true, false).Should().BeSameAs(status, "re-entry waits for the cadence gate");
        animation.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.Ready);
        time.Advance(TimeSpan.FromSeconds(2));
        animation.Select(status, true, false).Should().Equal(Fill(0x11));
        animation.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.Playing);
        live.Prepares.Should().Be(1);
        File.ReadAllBytes(AnimationPath()).Should().Equal(bytes, "status, selection and refresh never write the sidecar");
        File.Exists(AnimationPath() + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task M1_settings_error_not_admitted_off_native_owner_and_page_off_keep_precedence_over_the_drift_detail()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AnimationPath())!);
        var media = new FakeKeyboardOledMedia { Scene = new([new("screen:h2:test", AssetA, TimelineId: M1NewBindingTimelineId)]) };
        var screens = KeyboardAnimationScreens.Create(media);
        var on = M1SchemaOne(true, AssetA, SharedTimelineId);
        File.WriteAllBytes(AnimationPath(), on);
        using (var admitted = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider()))
        {
            await admitted.RefreshAsync(screens, default);
            admitted.Status(true, false).Should().Match<KeyboardDisplayAnimationStatusDto>(s => s.State == KeyboardDisplayAnimationStates.Unavailable
                && s.Detail == M1NewBinding);
            admitted.Status(true, true).Should().Match<KeyboardDisplayAnimationStatusDto>(s => s.State == KeyboardDisplayAnimationStates.SuppressedByNativeMenu
                && s.Detail == "已安装的原生光域菜单拥有键盘屏幕输出：不发送动画画面，原生状态画面与拨杆不受影响；绑定与开启偏好已保留");
            admitted.Status(false, false).Should().Match<KeyboardDisplayAnimationStatusDto>(s => s.State == KeyboardDisplayAnimationStates.PageOff
                && s.Detail == "键盘状态屏已关闭，键盘显示 ROG 原动画；开启状态屏后才会播放");
            admitted.Status(true, false).Settings.Should().Be(Enable(AssetA));
        }
        using (var unadmitted = new KeyboardDisplayAnimation(Pinned(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider()))
        {
            await unadmitted.RefreshAsync(screens, default);
            var status = unadmitted.Status(true, false);
            (status.State, status.Admitted, status.Choices.Count).Should().Be((KeyboardDisplayAnimationStates.NotAdmitted, false, 0));
            status.Detail.Should().StartWith("主机未准许键盘动画（").And.EndWith("）；已保存的偏好保持不变，键盘显示状态屏");
            status.Settings.Should().Be(Enable(AssetA));
        }
        File.ReadAllBytes(AnimationPath()).Should().Equal(on);

        var off = M1SchemaOne(false, AssetA, SharedTimelineId);
        File.WriteAllBytes(AnimationPath(), off);
        using (var disabled = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider()))
        {
            await disabled.RefreshAsync(screens, default);
            disabled.Status(true, false).Should().Match<KeyboardDisplayAnimationStatusDto>(s => s.State == KeyboardDisplayAnimationStates.Off
                && s.Detail == "键盘动画未开启；键盘显示状态屏");
            disabled.Status(true, false).Settings.Should().Be(Enable(AssetA) with { Enabled = false });
        }
        File.ReadAllBytes(AnimationPath()).Should().Equal(off);

        var unreadable = Encoding.UTF8.GetBytes("{\"Schema\":1,\"Enabled\":tru");
        File.WriteAllBytes(AnimationPath(), unreadable);
        using (var broken = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider()))
        {
            await broken.RefreshAsync(screens, default);
            broken.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.SettingsError);
        }
        File.ReadAllBytes(AnimationPath()).Should().Equal(unreadable);
        media.Prepares.Should().Be(0, "an unlisted or inactive binding is never prepared");
    }

    /// <summary>The exact schema-1 sidecar bytes of <c>SettingsFile</c> for contain / luma-round-v1 / 2000 ms (golden).</summary>
    private static byte[] M1SchemaOne(bool enabled, string assetId, string timelineId) => Encoding.UTF8.GetBytes(
        "{\"Schema\":1,\"Enabled\":" + (enabled ? "true" : "false") + ",\"AssetId\":\"" + assetId + "\",\"TimelineId\":\"" + timelineId
        + "\",\"Fit\":\"contain\",\"Encoding\":\"luma-round-v1\",\"IntervalMs\":2000}");
}
