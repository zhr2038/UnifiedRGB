using FluentAssertions;
using System.Globalization;
using System.Text;
using UnifiedRgb.Host.Services;
using Xunit;

namespace UnifiedRgb.Host.Tests;

// Includes native-geometry WPF rendering with the production five-second bound.
[Collection("Host")]
public sealed class CodexTaskDisplayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = CodexTaskDisplayTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static CodexTaskSnapshot Snapshot(params CodexRunningTask[] tasks) => new("Ready", tasks, Now);
    private static CodexRunningTask Task(string id, string? title = null) => new(id, title ?? id, Now - TimeSpan.FromMinutes(5));

    [Fact]
    public void Initial_and_returned_quota_views_return_no_task_pixels()
    {
        var display = new CodexTaskDisplay(new Clock());
        display.RenderTaskFrame(Now).Should().BeNull();
        display.PublishSnapshot(Snapshot(Task("a")));
        display.Browse(1); display.IsTaskView.Should().BeTrue();
        display.ReturnToQuota();
        display.IsTaskView.Should().BeFalse(); display.RenderTaskFrame(Now).Should().BeNull();
    }

    [Fact]
    public void Navigation_wraps_quota_and_all_current_tasks_in_both_directions()
    {
        var display = new CodexTaskDisplay(new Clock());
        display.PublishSnapshot(Snapshot(Task("a"), Task("b")));
        display.Browse(1); display.View(Now).Should().Match<CodexTaskView>(v => v.Title == "a" && v.Index == 1 && v.Total == 2);
        display.Browse(1); display.View(Now).Title.Should().Be("b");
        display.Browse(1); display.View(Now).Kind.Should().Be(CodexTaskViewKind.Quota);
        display.Browse(-1); display.View(Now).Title.Should().Be("b");
        display.Browse(-1); display.View(Now).Title.Should().Be("a");
        display.Browse(-1); display.View(Now).Kind.Should().Be(CodexTaskViewKind.Quota);
    }

    [Fact]
    public void Ready_empty_and_unknown_are_distinct_and_each_can_return_to_quota()
    {
        var display = new CodexTaskDisplay(new Clock());
        display.Browse(1); display.View(Now).Kind.Should().Be(CodexTaskViewKind.Unavailable);
        display.PublishSnapshot(Snapshot()); display.View(Now).Title.Should().Be("没有任务");
        display.Browse(-1); display.IsTaskView.Should().BeFalse();
        display.Browse(-1); display.View(Now).Kind.Should().Be(CodexTaskViewKind.Empty);
        display.PublishSnapshot(new("Unavailable", [], Now));
        display.View(Now).Title.Should().Be("任务状态暂不可用");
    }

    [Fact]
    public void Refresh_retains_stable_identity_and_scroll_phase_but_changed_title_resets_it()
    {
        var clock = new Clock(); var display = new CodexTaskDisplay(clock);
        display.PublishSnapshot(Snapshot(Task("a"), Task("b")));
        display.Browse(-1); clock.Now += TimeSpan.FromSeconds(8);
        display.PublishSnapshot(new("Ready", [Task("b"), Task("a")], clock.Now));
        display.View(clock.Now).Should().Match<CodexTaskView>(v => v.Title == "b" && v.Index == 1 && v.SelectedFor == TimeSpan.FromSeconds(8));
        display.PublishSnapshot(new("Ready", [Task("b", "new title"), Task("a")], clock.Now));
        display.View(clock.Now).SelectedFor.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Removal_chooses_remaining_task_and_empty_after_last_completion()
    {
        var display = new CodexTaskDisplay(new Clock());
        display.PublishSnapshot(Snapshot(Task("a"), Task("b"))); display.Browse(-1);
        display.PublishSnapshot(Snapshot(Task("a"))); display.View(Now).Title.Should().Be("a");
        display.PublishSnapshot(Snapshot()); display.View(Now).Kind.Should().Be(CodexTaskViewKind.Empty);
    }

    [Fact]
    public void Read_failure_hides_old_task_but_restores_its_selection_when_valid_metadata_returns()
    {
        var display = new CodexTaskDisplay(new Clock());
        display.PublishSnapshot(Snapshot(Task("a"), Task("b"))); display.Browse(-1);
        display.PublishSnapshot(new("Unavailable", [], Now)); display.View(Now).Kind.Should().Be(CodexTaskViewKind.Unavailable);
        display.PublishSnapshot(Snapshot(Task("a"), Task("b"))); display.View(Now).Title.Should().Be("b");
    }

    [Theory]
    [InlineData(31)] [InlineData(-6)]
    public void Stale_or_future_snapshots_are_unavailable_instead_of_claiming_no_tasks(int age)
    {
        var display = new CodexTaskDisplay(new Clock());
        display.PublishSnapshot(new("Ready", [], Now - TimeSpan.FromSeconds(age))); display.Browse(1);
        display.View(Now).Kind.Should().Be(CodexTaskViewKind.Unavailable);
    }

    [Fact]
    public void Caller_list_mutation_cannot_change_the_published_snapshot()
    {
        var source = new List<CodexRunningTask> { Task("a") }; var display = new CodexTaskDisplay(new Clock());
        display.PublishSnapshot(new("Ready", source, Now)); source[0] = Task("replaced"); display.Browse(1);
        display.View(Now).Title.Should().Be("a");
    }

    [Fact]
    public void Duplicate_or_overbound_tasks_fail_closed_as_unavailable()
    {
        var display = new CodexTaskDisplay(new Clock());
        display.PublishSnapshot(Snapshot(Task("a"), Task("a"))); display.Browse(1);
        display.View(Now).Kind.Should().Be(CodexTaskViewKind.Unavailable);
        display.PublishSnapshot(new("Ready", Enumerable.Range(0, CodexTaskDisplay.MaximumTasks + 1).Select(i => Task(i.ToString(CultureInfo.InvariantCulture))).ToArray(), Now));
        display.View(Now).Kind.Should().Be(CodexTaskViewKind.Unavailable);
    }

    [Fact]
    public void Names_are_data_only_and_controls_bounded_without_splitting_surrogate_pairs()
    {
        CodexTaskDisplay.SanitizeTitle("  项目\n\t测试\u202eABC  ").Should().Be("项目 测试 ABC");
        CodexTaskDisplay.SanitizeTitle("").Should().Be("未命名任务");
        var title = CodexTaskDisplay.SanitizeTitle(string.Concat(Enumerable.Repeat("😀", 400)));
        title.EnumerateRunes().Count().Should().Be(CodexTaskDisplay.MaximumTitleRunes);
        title.Should().NotContain('\uFFFD'.ToString());
        CodexTaskDisplay.SanitizeTitle(string.Concat(Enumerable.Repeat("字 ", 400))).EnumerateRunes().Count().Should().BeLessThanOrEqualTo(CodexTaskDisplay.MaximumTitleRunes);
    }

    [Fact]
    public void Elapsed_is_current_turn_based_and_does_not_wrap_at_twenty_four_hours_or_go_negative()
    {
        CodexTaskDisplay.Elapsed(Now - TimeSpan.FromSeconds(3661), Now).Should().Be("01:01:01");
        CodexTaskDisplay.Elapsed(Now - TimeSpan.FromHours(25), Now).Should().Be("25:00:00");
        CodexTaskDisplay.Elapsed(Now + TimeSpan.FromSeconds(2), Now).Should().Be("00:00:00");
        CodexTaskDisplay.Elapsed(Now + TimeSpan.FromMinutes(2), Now).Should().Be("时长未知");
        CodexTaskDisplay.Elapsed(null, Now).Should().Be("时长未知");
    }

    [Fact]
    public void New_turn_in_same_task_resets_title_scroll_and_elapsed()
    {
        var clock = new Clock(); var display = new CodexTaskDisplay(clock);
        display.PublishSnapshot(Snapshot(Task("a"))); display.Browse(1); clock.Now += TimeSpan.FromSeconds(8);
        display.PublishSnapshot(new("Ready", [new("a", "a", clock.Now)], clock.Now));
        display.View(clock.Now).Should().Match<CodexTaskView>(v => v.Duration == "00:00:00" && v.SelectedFor == TimeSpan.Zero);
    }

    [Fact]
    public void Marquee_has_initial_hold_and_does_not_scroll_short_titles()
    {
        KeyboardCodexTaskArtwork.Offset(100, TimeSpan.FromSeconds(12)).Should().Be(0);
        KeyboardCodexTaskArtwork.Offset(300, TimeSpan.FromSeconds(1)).Should().Be(0);
        KeyboardCodexTaskArtwork.Offset(300, TimeSpan.FromSeconds(3)).Should().Be(20);
        KeyboardCodexTaskArtwork.Offset(300, TimeSpan.FromSeconds(34)).Should().Be(6);
    }

    [Fact]
    public void Rasterized_task_empty_and_unknown_frames_have_fixed_native_geometry_and_long_title_really_scrolls()
    {
        var title = "这是一项正在执行的很长很长的任务名称 Long task with emoji 😀";
        var first = KeyboardCodexTaskArtwork.Render(new(CodexTaskViewKind.Task, title, "00:03:15", 1, 2, TimeSpan.Zero));
        var later = KeyboardCodexTaskArtwork.Render(new(CodexTaskViewKind.Task, title, "00:03:15", 1, 2, TimeSpan.FromSeconds(8)));
        first.Length.Should().Be(6656); later.Length.Should().Be(6656);
        first.Should().NotEqual(later);
        var none = KeyboardCodexTaskArtwork.Render(new(CodexTaskViewKind.Empty, "没有任务", "", 0, 0, TimeSpan.Zero));
        var unknown = KeyboardCodexTaskArtwork.Render(new(CodexTaskViewKind.Unavailable, "任务状态暂不可用", "", 0, 0, TimeSpan.Zero));
        none.Length.Should().Be(6656); unknown.Length.Should().Be(6656); none.Should().NotEqual(unknown);
    }
}
