using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexQuotaClientTests
{
    internal const string ValidWindow = "{\"usedPercent\":25.5,\"windowDurationMins\":10080,\"resetsAt\":2000000000}";
    internal static string Result(string primary = ValidWindow) => "{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":" + primary + ",\"secondary\":null}}}";

    [Fact]
    public void Process_is_direct_trusted_local_executable_and_never_a_shell()
    {
        var start = CodexQuotaProcess.StartInfo();
        Path.IsPathFullyQualified(start.FileName).Should().BeTrue();
        start.FileName.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "bin", "codex-real.exe"));
        start.UseShellExecute.Should().BeFalse(); start.CreateNoWindow.Should().BeTrue();
        start.RedirectStandardInput.Should().BeTrue(); start.RedirectStandardOutput.Should().BeTrue(); start.RedirectStandardError.Should().BeTrue();
        start.ArgumentList.Should().Equal("app-server", "--listen", "stdio://");
    }

    [Fact]
    public async Task Handshake_has_exactly_three_allowlisted_methods_and_joins_only_owned_child()
    {
        var child = new FakeQuotaProcess("{\"id\":1,\"result\":{}}\n{\"method\":\"ignored\"}\n{\"id\":null,\"method\":\"notice\"}\n{\"id\":2,\"result\":" + Result() + "}\n");
        var starts = 0;
        var client = new CodexQuotaClient(() => { starts++; return child; });
        var result = await client.ReadAsync(default);
        result.Primary!.RemainingPercent.Should().Be(74.5); result.Primary.Label.Should().Be("7天");
        var lines = child.Written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        var methods = new List<string>();
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            methods.Add(json.RootElement.GetProperty("method").GetString()!);
        }
        methods.Should().Equal("initialize", "initialized", "account/rateLimits/read");
        using var initialize = JsonDocument.Parse(lines[0]);
        var info = initialize.RootElement.GetProperty("params").GetProperty("clientInfo");
        foreach (var key in new[] { "name", "title", "version" }) info.GetProperty(key).GetString().Should().NotBeNullOrEmpty();
        starts.Should().Be(1); child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Fact]
    public async Task Initialize_refusal_never_sends_initialized_or_quota_or_forbidden_methods()
    {
        var child = new FakeQuotaProcess("{\"id\":1,\"error\":{\"message\":\"SECRET\"}}\n");
        var action = () => new CodexQuotaClient(() => child).ReadAsync(default);
        var error = await action.Should().ThrowAsync<InvalidDataException>();
        error.Which.Message.Should().NotContain("SECRET");
        child.Written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_terminates_and_reaps_child_when_both_pipe_readers_ignore_cancellation(bool cancel)
    {
        var child = new FakeQuotaProcess(null, ignoreCancellation: true);
        using var stop = new CancellationTokenSource();
        var client = new CodexQuotaClient(() => child, TimeSpan.FromMilliseconds(cancel ? 5000 : 40));
        var pending = client.ReadAsync(stop.Token);
        child.ActiveReads.Should().Be(2);
        if (cancel) stop.Cancel();
        await ((Func<Task>)(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2)))).Should().ThrowAsync<OperationCanceledException>();
        child.Events.Should().Equal("terminated", "joined", "disposed");
        child.PipesClosed.Should().BeTrue(); child.ActiveReads.Should().Be(0, "there must be no orphan reader tasks");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_timeout_wait_for_owned_child_join_before_dispose(bool cancel)
    {
        var child = new FakeQuotaProcess(null) { Join = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var stop = new CancellationTokenSource();
        var client = new CodexQuotaClient(() => child, TimeSpan.FromMilliseconds(cancel ? 5000 : 40));
        var pending = client.ReadAsync(stop.Token);
        if (cancel) stop.Cancel();
        await child.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pending.IsCompleted.Should().BeFalse("termination alone is not a joined child");
        child.Events.Should().Equal("terminated");
        child.Join.SetResult();
        await ((Func<Task>)(async () => await pending)).Should().ThrowAsync<OperationCanceledException>();
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Fact]
    public async Task Output_line_overflow_is_bounded_and_reaped()
    {
        var child = new FakeQuotaProcess(new string('x', CodexQuotaClient.MaximumLine + 1));
        await ((Func<Task>)(async () => await new CodexQuotaClient(() => child).ReadAsync(default))).Should().ThrowAsync<InvalidDataException>();
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Fact]
    public async Task Stderr_overflow_is_bounded_without_returning_its_contents()
    {
        var child = new FakeQuotaProcess(null, new string('s', CodexQuotaClient.MaximumOutput + 1));
        var action = () => new CodexQuotaClient(() => child).ReadAsync(default);
        var error = await action.Should().ThrowAsync<InvalidDataException>();
        error.Which.Message.Should().Be("Local Codex output exceeded its bound.");
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Fact]
    public async Task Already_canceled_read_never_starts_a_child()
    {
        var starts = 0;
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var client = new CodexQuotaClient(() => { starts++; throw new InvalidOperationException("must not start"); });
        await ((Func<Task>)(async () => await client.ReadAsync(stop.Token))).Should().ThrowAsync<OperationCanceledException>();
        starts.Should().Be(0);
    }

    [Fact]
    public async Task Canceling_a_pending_quota_reply_after_handshake_joins_the_owned_child()
    {
        var child = new FakeQuotaProcess("{\"id\":1,\"result\":{}}\n", blockAtEnd: true);
        using var stop = new CancellationTokenSource();
        var read = new CodexQuotaClient(() => child).ReadAsync(stop.Token);
        child.Written.ToString().Should().Contain("account/rateLimits/read");
        stop.Cancel();
        await ((Func<Task>)(async () => await read)).Should().ThrowAsync<OperationCanceledException>();
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Fact]
    public async Task Total_stdout_is_bounded_independently_of_line_limit()
    {
        var notification = "{\"method\":\"notice\",\"params\":\"" + new string('x', 3000) + "\"}\n";
        var child = new FakeQuotaProcess(string.Concat(Enumerable.Repeat(notification, 100)));
        await ((Func<Task>)(async () => await new CodexQuotaClient(() => child).ReadAsync(default))).Should().ThrowAsync<InvalidDataException>();
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Fact]
    public async Task Notification_flood_is_bounded_even_with_short_lines()
    {
        var child = new FakeQuotaProcess(string.Concat(Enumerable.Repeat("{}\n", 130)));
        await ((Func<Task>)(async () => await new CodexQuotaClient(() => child).ReadAsync(default))).Should().ThrowAsync<InvalidDataException>();
        child.Events.Should().Equal("terminated", "joined", "disposed");
    }

    [Theory]
    [InlineData(300, "5小时")]
    [InlineData(10080, "7天")]
    [InlineData(90, "90分钟")]
    [InlineData(1440, "1天")]
    public void Labels_follow_actual_duration_not_primary_position(int minutes, string expected)
    {
        using var json = JsonDocument.Parse(Result(ValidWindow.Replace("10080", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        CodexQuotaClient.Parse(json.RootElement).Primary!.Label.Should().Be(expected);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"usedPercent\":1e400,\"windowDurationMins\":300,\"resetsAt\":2000000000}")]
    [InlineData("{\"usedPercent\":\"5\",\"windowDurationMins\":300,\"resetsAt\":2000000000}")]
    [InlineData("{\"usedPercent\":5,\"windowDurationMins\":null,\"resetsAt\":2000000000}")]
    [InlineData("{\"usedPercent\":5,\"windowDurationMins\":0,\"resetsAt\":2000000000}")]
    [InlineData("{\"usedPercent\":5,\"windowDurationMins\":300,\"resetsAt\":null}")]
    [InlineData("{\"usedPercent\":5,\"windowDurationMins\":300,\"resetsAt\":9223372036854775807}")]
    public void Missing_null_and_invalid_numbers_are_unavailable_never_zero(string window)
    {
        using var json = JsonDocument.Parse(Result(window));
        var result = CodexQuotaClient.Parse(json.RootElement);
        result.Primary.Should().BeNull(); result.Secondary.Should().BeNull(); result.State.Should().Be("Unavailable");
    }

    [Fact]
    public void Exact_codex_bucket_wins_without_summing_or_using_legacy()
    {
        using var json = JsonDocument.Parse("{\"rateLimits\":{\"primary\":" + ValidWindow.Replace("25.5", "99", StringComparison.Ordinal)
            + "},\"rateLimitsByLimitId\":{\"other\":{\"primary\":" + ValidWindow + "},\"codex\":{\"primary\":" + ValidWindow
            + ",\"secondary\":" + ValidWindow.Replace("10080", "300", StringComparison.Ordinal) + "}}}");
        var result = CodexQuotaClient.Parse(json.RootElement);
        result.Bucket.Should().Be("codex"); result.Primary!.RemainingPercent.Should().Be(74.5);
        result.Secondary!.Label.Should().Be("5小时");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"other\":{}}")]
    [InlineData("{\"one\":{},\"two\":{}}")]
    public void Present_map_without_exact_codex_is_unavailable_even_when_legacy_has_numbers(string map)
    {
        using var json = JsonDocument.Parse("{\"rateLimits\":{\"primary\":" + ValidWindow + "},\"rateLimitsByLimitId\":" + map + "}");
        var result = CodexQuotaClient.Parse(json.RootElement);
        result.Bucket.Should().BeNull(); result.Primary.Should().BeNull(); result.State.Should().Be("Unavailable");
    }

    [Fact]
    public void Genuine_zero_or_full_use_is_not_confused_with_an_unavailable_window()
    {
        using var unused = JsonDocument.Parse(Result(ValidWindow.Replace("25.5", "0", StringComparison.Ordinal)));
        using var exhausted = JsonDocument.Parse(Result(ValidWindow.Replace("25.5", "100", StringComparison.Ordinal)));
        CodexQuotaClient.Parse(unused.RootElement).Primary!.RemainingPercent.Should().Be(100);
        CodexQuotaClient.Parse(exhausted.RootElement).Primary!.RemainingPercent.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(101, 0)]
    [InlineData(-1e300, 100)]
    [InlineData(1e300, 0)]
    public void Finite_used_percent_is_clamped_to_desktop_remaining_semantics(double used, double remaining)
    {
        using var json = JsonDocument.Parse(Result(ValidWindow.Replace("25.5", used.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        var result = CodexQuotaClient.Parse(json.RootElement);
        result.Primary!.RemainingPercent.Should().Be(remaining); result.State.Should().Be("Ready");
    }

    [Fact]
    public void Legacy_is_supported_when_map_is_absent_and_null_secondary_stays_null()
    {
        using var json = JsonDocument.Parse("{\"rateLimits\":{\"primary\":" + ValidWindow + ",\"secondary\":null}}");
        var result = CodexQuotaClient.Parse(json.RootElement);
        result.Bucket.Should().Be("legacy"); result.Primary.Should().NotBeNull(); result.Secondary.Should().BeNull();
    }

    internal sealed class FakeQuotaProcess : ICodexQuotaProcess
    {
        public StringWriter Written { get; } = new();
        public List<string> Events { get; } = [];
        public TaskCompletionSource Terminated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Join { get; init; }
        public TextReader Output { get; }
        public TextReader Error { get; }
        public TextWriter Input => Written;
        public bool PipesClosed { get; private set; }
        public int ActiveReads => (Output is BlockedReader stdout ? stdout.ActiveReads : 0) + (Error is BlockedReader stderr ? stderr.ActiveReads : 0);
        public FakeQuotaProcess(string? output, string error = "", bool blockAtEnd = false, bool ignoreCancellation = false)
        {
            Output = output is null ? new BlockedReader(Terminated.Task, ignoreCancellation)
                : blockAtEnd ? new PrefixReader(output, Terminated.Task) : new StringReader(output);
            Error = ignoreCancellation ? new BlockedReader(Terminated.Task, true) : new StringReader(error);
        }
        public async Task TerminateAndJoinAsync()
        {
            Events.Add("terminated"); Terminated.TrySetResult();
            if (Join is not null) await Join.Task;
            Events.Add("joined");
        }
        public ValueTask DisposeAsync()
        { Events.Add("disposed"); Output.Dispose(); Error.Dispose(); Written.Dispose(); return ValueTask.CompletedTask; }
        public void ClosePipes() { PipesClosed = true; Output.Dispose(); Error.Dispose(); }
    }

    private sealed class BlockedReader(Task terminated, bool ignoreCancellation = false) : TextReader
    {
        private int _activeReads;
        public int ActiveReads => Volatile.Read(ref _activeReads);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                if (ignoreCancellation) await terminated;
                else await terminated.WaitAsync(cancellationToken);
                return 0;
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }
    }

    private sealed class PrefixReader(string prefix, Task terminated) : TextReader
    {
        private int _position;
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length) { buffer.Span[0] = prefix[_position++]; return 1; }
            await terminated.WaitAsync(cancellationToken); return 0;
        }
    }
}
