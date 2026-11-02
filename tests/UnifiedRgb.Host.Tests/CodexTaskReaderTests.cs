using System.Text.Json;
using FluentAssertions;
using UnifiedRgb.Host.Services;
using Xunit;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexTaskReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 20, 0, TimeSpan.Zero);
    private const string ThreadId = "01a197a5-12e8-7771-bf3d-83cf726bdf71";
    private const string TurnId = "01a19bb8-0d4d-7202-a72f-5c21ac95cb30";
    private const string OtherTurn = "01a19b6c-47be-7dc3-9886-0902e47108e5";

    [Fact]
    public async Task Matching_unclosed_turn_uses_actual_started_at_not_log_or_chat_time()
    {
        var fixture = new Fixture();
        using var reader = fixture.Reader();
        var result = await reader.ReadAsync(default);
        result.State.Should().Be("Ready");
        result.Tasks.Should().ContainSingle();
        result.Tasks[0].Id.Should().Be(ThreadId);
        result.Tasks[0].Title.Should().Be("正在实现 OLED 任务浏览");
        result.Tasks[0].StartedAt.Should().Be(Now - TimeSpan.FromHours(2));
        result.ObservedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("dead-owner")]
    [InlineData("pid-reused")]
    public async Task Completed_or_unowned_turn_is_not_running(string state)
    {
        var fixture = new Fixture();
        if (state == "completed") fixture.Lifecycle.Value = new(false, TurnId, null);
        if (state == "dead-owner") fixture.Owners.Values = [];
        if (state == "pid-reused") fixture.Owners.Values = [new(7, Now + TimeSpan.FromSeconds(1))];
        using var reader = fixture.Reader();
        var result = await reader.ReadAsync(default);
        result.State.Should().Be("Ready");
        result.Tasks.Should().BeEmpty();
        if (state == "dead-owner") fixture.Store.Calls.Should().Be(0);
        if (state == "pid-reused") fixture.Lifecycle.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("wrong-turn")]
    [InlineData("future-start")]
    [InlineData("missing-start")]
    [InlineData("locked-store")]
    [InlineData("bad-lifecycle")]
    [InlineData("changed-number")]
    public async Task Missing_or_inconsistent_metadata_is_unavailable_never_no_tasks(string fault)
    {
        var fixture = new Fixture();
        if (fault == "wrong-turn") fixture.Lifecycle.Value = new(true, OtherTurn, Now - TimeSpan.FromMinutes(1));
        if (fault == "future-start") fixture.Lifecycle.Value = new(true, TurnId, Now + TimeSpan.FromSeconds(1));
        if (fault == "missing-start") fixture.Lifecycle.Value = new(true, TurnId, null);
        if (fault == "locked-store") fixture.Store.Error = new IOException("FAKE SQLite lock");
        if (fault == "bad-lifecycle") fixture.Lifecycle.Error = new InvalidDataException("FAKE changed schema");
        if (fault == "changed-number") fixture.Store.Error = new FormatException("FAKE changed integer");
        using var reader = fixture.Reader();
        var result = await reader.ReadAsync(default);
        result.State.Should().Be("Unavailable"); result.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_an_empty_task_set()
    {
        var fixture = new Fixture(); using var reader = fixture.Reader();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancel.Token));
        fixture.Store.Calls.Should().Be(0);
    }

    [Fact]
    public void Lifecycle_incremental_append_ignores_body_text_and_only_closes_matching_turn()
    {
        using var fixture = new RolloutFixture();
        fixture.Append(Start(TurnId, Now.AddHours(-1)));
        var reader = new CodexTaskLifecycleReader(fixture.Root);
        reader.Read(fixture.Path, ThreadId, default).Should().Be(new CodexTaskLifecycle(true, TurnId, Now.AddHours(-1)));
        fixture.Append(new { type = "response_item", payload = new { type = "message", text = "task_complete turn_id=" + TurnId } });
        fixture.Append(End(OtherTurn));
        reader.Read(fixture.Path, ThreadId, default).Running.Should().BeTrue();
        fixture.Append(End(TurnId));
        reader.Read(fixture.Path, ThreadId, default).Running.Should().BeFalse();
        fixture.Append(Start(OtherTurn, Now));
        reader.Read(fixture.Path, ThreadId, default).Should().Be(new CodexTaskLifecycle(true, OtherTurn, Now));
    }

    [Fact]
    public void Large_non_lifecycle_body_is_not_retained_or_mistaken_for_lifecycle()
    {
        using var fixture = new RolloutFixture(); fixture.Append(Start(TurnId, Now));
        fixture.Append(new { type = "response_item", payload = new { type = "tool_output", text = new string('x', 2 * 1024 * 1024) + "task_complete" } });
        new CodexTaskLifecycleReader(fixture.Root).Read(fixture.Path, ThreadId, default).Running.Should().BeTrue();
    }

    [Fact]
    public void Known_metadata_envelopes_do_not_clear_running_lifecycle_but_unknown_schema_does()
    {
        using var fixture = new RolloutFixture(); fixture.Append(Start(TurnId, Now));
        foreach (var kind in new[] { "world_state", "token_usage_record", "inter_agent_communication_metadata" })
            fixture.Append(new { type = kind, payload = new { type = "metadata" } });
        var reader = new CodexTaskLifecycleReader(fixture.Root);
        reader.Read(fixture.Path, ThreadId, default).Running.Should().BeTrue();
        fixture.Append(new { type = "future_unknown_schema", payload = new { type = "unknown" } });
        Assert.Throws<InvalidDataException>(() => reader.Read(fixture.Path, ThreadId, default));
    }

    [Fact]
    public void Partial_lifecycle_is_unavailable_until_its_complete_line_arrives()
    {
        using var fixture = new RolloutFixture(); fixture.Append(Start(TurnId, Now));
        var reader = new CodexTaskLifecycleReader(fixture.Root);
        reader.Read(fixture.Path, ThreadId, default).Running.Should().BeTrue();
        var end = JsonSerializer.Serialize(End(TurnId));
        File.AppendAllText(fixture.Path, end[..^1]);
        Assert.Throws<IOException>(() => reader.Read(fixture.Path, ThreadId, default));
        File.AppendAllText(fixture.Path, "}\n");
        reader.Read(fixture.Path, ThreadId, default).Running.Should().BeFalse();
    }

    [Fact]
    public void Rollout_must_match_trusted_root_and_exact_thread_basename()
    {
        using var fixture = new RolloutFixture(); fixture.Append(Start(TurnId, Now));
        var reader = new CodexTaskLifecycleReader(fixture.Root);
        Assert.Throws<InvalidDataException>(() => reader.Read(fixture.Path, OtherTurn, default));
        Assert.Throws<InvalidDataException>(() => reader.Read(Path.Combine(Path.GetTempPath(), "outside-" + ThreadId + ".jsonl"), ThreadId, default));
    }

    private static object Start(string turn, DateTimeOffset stamp) => new { timestamp = stamp, type = "event_msg", payload = new { type = "task_started", turn_id = turn, started_at = stamp } };
    private static object End(string turn) => new { timestamp = Now, type = "event_msg", payload = new { type = "task_complete", turn_id = turn } };
    private sealed class Fixture
    {
        internal readonly Store Store = new(); internal readonly Owners Owners = new(); internal readonly Lifecycle Lifecycle = new();
        internal NativeCodexTaskReader Reader() => new(Store, Owners, Lifecycle, new Clock());
    }
    private sealed class Store : ICodexTaskMetadataStore
    {
        internal int Calls; internal Exception? Error;
        public IReadOnlyList<CodexTaskMetadata> Read(IReadOnlyList<CodexTaskOwner> owners, CancellationToken ct)
        { Calls++; if (Error is not null) throw Error; return [new(ThreadId, "正在实现 OLED 任务浏览", "FAKE-ONLY", TurnId, 7, Now)]; }
    }
    private sealed class Owners : ICodexTaskOwnerSource
    {
        internal IReadOnlyList<CodexTaskOwner> Values = [new(7, Now - TimeSpan.FromHours(3))];
        public IReadOnlyList<CodexTaskOwner> Read(CancellationToken ct) => Values;
    }
    private sealed class Lifecycle : ICodexTaskLifecycleSource
    {
        internal int Calls; internal Exception? Error;
        internal CodexTaskLifecycle Value = new(true, TurnId, Now - TimeSpan.FromHours(2));
        public CodexTaskLifecycle Read(string path, string id, CancellationToken ct) { Calls++; if (Error is not null) throw Error; return Value; }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RolloutFixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexTaskReader-Fake-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal RolloutFixture() { Directory.CreateDirectory(System.IO.Path.Combine(Root, "sessions")); Path = System.IO.Path.Combine(Root, "sessions", "rollout-FAKE-" + ThreadId + ".jsonl"); }
        internal void Append(object record) => File.AppendAllText(Path, JsonSerializer.Serialize(record) + "\n");
        public void Dispose()
        {
            var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexTaskReader-Fake-"));
            if (!System.IO.Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fake cleanup scope");
            Directory.Delete(Root, recursive: true);
        }
    }
}
