using System.Collections.Concurrent;
using System.Text.Json;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

[Collection("Codex")]
public sealed class KeyboardCodexProviderTests
{
    [Fact]
    public async Task Default_off_constructor_and_status_reads_do_no_queries_rendering_or_preference_writes()
    {
        var data = CodexTestData.Directory(); var client = new FakeQuotaClient(); var renders = 0;
        using var provider = new KeyboardCodexUsageProvider(data, client, render: _ => { renders++; return new byte[6656]; });
        provider.Status.Enabled.Should().BeFalse(); provider.Pixels.Should().BeNull();
        await provider.StartAsync(default);
        for (var i = 0; i < 10; i++) provider.Status.State.Should().Be("Off");
        await provider.StopAsync(default);
        client.Calls.Should().Be(0); renders.Should().Be(0);
        File.Exists(CodexTestData.Preference(data)).Should().BeFalse();
    }

    [Fact]
    public async Task Enable_is_immediate_persisted_and_disable_suppresses_a_late_uncancellable_reply()
    {
        var data = CodexTestData.Directory(); var clock = new ManualTimeProvider(); var client = new FakeQuotaClient();
        var reply = new TaskCompletionSource<KeyboardCodexUsageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Answers.Enqueue(_ => reply.Task);
        using var provider = new KeyboardCodexUsageProvider(data, client, clock, _ => new byte[6656]);
        await provider.StartAsync(default);
        provider.SetEnabled(true, default).State.Should().Be("Loading");
        await CodexTestData.Until(() => client.Calls == 1);
        using (var file = JsonDocument.Parse(File.ReadAllText(CodexTestData.Preference(data)))) file.RootElement.GetProperty("Enabled").GetBoolean().Should().BeTrue();
        provider.SetEnabled(false, default);
        client.LastToken.IsCancellationRequested.Should().BeTrue();
        reply.SetResult(Ready(clock));
        await provider.StopAsync(default);
        provider.Status.Should().Be(new KeyboardCodexUsageDto(false, "Off", null, null, null, null, null));
        provider.Pixels.Should().BeNull();
        using var reloaded = new KeyboardCodexUsageProvider(data, new FakeQuotaClient());
        reloaded.Status.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Persisted_enable_only_queries_after_hosted_start_not_construction_or_get()
    {
        var data = CodexTestData.Directory();
        File.WriteAllText(CodexTestData.Preference(data), "{\"Schema\":1,\"Enabled\":true}");
        var client = new FakeQuotaClient(); var clock = new ManualTimeProvider();
        client.Answers.Enqueue(_ => Task.FromResult(Ready(clock)));
        using var provider = new KeyboardCodexUsageProvider(data, client, clock, _ => new byte[6656]);
        provider.Status.Enabled.Should().BeTrue(); client.Calls.Should().Be(0); provider.Pixels.Should().BeNull();
        await provider.StartAsync(default);
        await CodexTestData.Until(() => provider.Status.State == "Ready");
        await provider.StopAsync(default);
        client.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Schema\":99,\"Enabled\":true}")]
    [InlineData("{\"Schema\":1,\"Enabled\":null}")]
    [InlineData("{\"Schema\":9999999999999999999999999,\"Enabled\":true}")]
    [InlineData("{}")]
    public async Task Malformed_settings_are_preserved_fail_off_and_cannot_be_overwritten_by_ui(string contents)
    {
        var data = CodexTestData.Directory(); File.WriteAllText(CodexTestData.Preference(data), contents);
        var client = new FakeQuotaClient();
        using var provider = new KeyboardCodexUsageProvider(data, client);
        provider.Status.Enabled.Should().BeFalse(); provider.Status.State.Should().Be("SettingsError");
        ((Action)(() => provider.SetEnabled(true, default))).Should().Throw<InvalidOperationException>();
        await provider.StartAsync(default); await provider.StopAsync(default);
        File.ReadAllText(CodexTestData.Preference(data)).Should().Be(contents); client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unchanged_success_keeps_exact_pixels_and_does_not_render_sample_timestamps()
    {
        var data = CodexTestData.Directory(); var clock = new ManualTimeProvider(); var client = new FakeQuotaClient(); var renders = 0;
        var sample = Ready(clock);
        client.Answers.Enqueue(_ => Task.FromResult(sample)); client.Answers.Enqueue(_ => Task.FromResult(sample));
        using var provider = new KeyboardCodexUsageProvider(data, client, clock, s =>
        { Interlocked.Increment(ref renders); return Enumerable.Repeat((byte)(s.State == "Ready" ? 0xFF : 0), 6656).ToArray(); });
        await provider.StartAsync(default); provider.SetEnabled(true, default);
        await CodexTestData.Until(() => provider.Status.State == "Ready" && provider.Pixels?[0] == 0xFF && clock.ActiveTimers > 0);
        var pixels = provider.Pixels; var count = renders; var firstSuccess = provider.Status.LastSuccess;
        clock.Advance(TimeSpan.FromSeconds(60));
        await CodexTestData.Until(() => provider.Status.LastSuccess > firstSuccess);
        await CodexTestData.Until(() => clock.ActiveTimers > 0);
        provider.Pixels.Should().BeSameAs(pixels); renders.Should().Be(count); client.Calls.Should().Be(2);
        await provider.StopAsync(default);
    }

    [Fact]
    public async Task Failures_mark_cached_numbers_stale_use_backoff_and_never_leak_exception_contents()
    {
        var clock = new ManualTimeProvider(); var client = new FakeQuotaClient();
        var sample = Ready(clock);
        client.Answers.Enqueue(_ => Task.FromResult(sample));
        client.Answers.Enqueue(_ => Task.FromException<KeyboardCodexUsageDto>(new IOException("SECRET email token raw-json")));
        client.Answers.Enqueue(_ => Task.FromException<KeyboardCodexUsageDto>(new IOException("SECRET")));
        using var provider = new KeyboardCodexUsageProvider(CodexTestData.Directory(), client, clock, _ => new byte[6656]);
        await provider.StartAsync(default); provider.SetEnabled(true, default);
        await CodexTestData.Until(() => provider.Status.State == "Ready" && clock.ActiveTimers > 0);
        var success = provider.Status.LastSuccess;
        clock.Advance(TimeSpan.FromSeconds(60));
        await CodexTestData.Until(() => provider.Status.State == "Stale" && clock.ActiveTimers > 0);
        provider.Status.Primary.Should().Be(sample.Primary); provider.Status.LastSuccess.Should().Be(success);
        JsonSerializer.Serialize(provider.Status).Should().NotContain("SECRET").And.NotContain("token").And.NotContain("email");
        clock.Advance(TimeSpan.FromSeconds(59)); await Task.Delay(30); client.Calls.Should().Be(2);
        await CodexTestData.Until(() => clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(1));
        await CodexTestData.Until(() => client.Calls == 3 && clock.ActiveTimers > 0);
        provider.SetEnabled(true, default); // idempotent enable must not bypass failed-read backoff
        clock.Advance(TimeSpan.FromSeconds(60)); await Task.Delay(30); client.Calls.Should().Be(3);
        await provider.StopAsync(default);
    }

    [Fact]
    public async Task Reset_expiry_removes_old_numbers_independently_of_next_poll_and_does_not_query_early()
    {
        var clock = new ManualTimeProvider(); var client = new FakeQuotaClient();
        var sample = Ready(clock);
        sample = sample with { Primary = sample.Primary! with { ResetsAt = clock.GetUtcNow().AddSeconds(20) }, Secondary = sample.Primary! with { ResetsAt = clock.GetUtcNow().AddSeconds(40) } };
        client.Answers.Enqueue(_ => Task.FromResult(sample));
        using var provider = new KeyboardCodexUsageProvider(CodexTestData.Directory(), client, clock, _ => new byte[6656]);
        await provider.StartAsync(default); provider.SetEnabled(true, default);
        await CodexTestData.Until(() => provider.Status.State == "Ready" && clock.ActiveTimers > 0);
        clock.Advance(TimeSpan.FromSeconds(20));
        await CodexTestData.Until(() => provider.Status.Primary is null && clock.ActiveTimers > 0);
        provider.Status.Secondary.Should().NotBeNull(); provider.Status.State.Should().Be("Stale"); client.Calls.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(20));
        await CodexTestData.Until(() => provider.Status.Secondary is null);
        client.Calls.Should().Be(1); await provider.StopAsync(default);
    }

    [Fact]
    public async Task Hosted_stop_cancels_pending_client_and_waits_for_its_cleanup()
    {
        var client = new FakeQuotaClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Answers.Enqueue(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { canceled.SetResult(); await cleanup.Task; }
            return Ready(new ManualTimeProvider());
        });
        using var provider = new KeyboardCodexUsageProvider(CodexTestData.Directory(), client, render: _ => new byte[6656]);
        await provider.StartAsync(default); provider.SetEnabled(true, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = provider.StopAsync(default);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopping.IsCompleted.Should().BeFalse(); cleanup.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Status.LastSuccess.Should().BeNull();
    }

    internal static KeyboardCodexUsageDto Ready(TimeProvider time) => new(true, "Ready", "codex",
        new(10080, "7天", 74.5, time.GetUtcNow().AddDays(7)), null, null, null);

    private sealed class FakeQuotaClient : ICodexQuotaClient
    {
        public ConcurrentQueue<Func<CancellationToken, Task<KeyboardCodexUsageDto>>> Answers { get; } = new();
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public CancellationToken LastToken { get; private set; }
        public Task<KeyboardCodexUsageDto> ReadAsync(CancellationToken ct)
        {
            LastToken = ct; Interlocked.Increment(ref _calls);
            return Answers.TryDequeue(out var answer) ? answer(ct) : Task.FromException<KeyboardCodexUsageDto>(new IOException("unscripted read"));
        }
    }
}

internal static class CodexTestData
{
    private static readonly ConcurrentBag<string> OwnedDirectories = [];
    public static string Directory()
    {
        var path = System.IO.Directory.CreateTempSubdirectory("UnifiedRgb-Codex-Tests-").FullName;
        OwnedDirectories.Add(path);
        System.IO.Directory.CreateDirectory(Path.Combine(path, "peripherals"));
        return path;
    }
    public static void Cleanup()
    {
        while (OwnedDirectories.TryTake(out var path)) System.IO.Directory.Delete(path, recursive: true);
    }
    public static string Preference(string data) => Path.Combine(data, "peripherals", "keyboard-codex-usage.json");
    public static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, stop.Token);
    }
}

// Presentation tests share this fixture and execute real WPF/STA artwork under its unchanged five-second bound.
// Keep the entire collection exclusive, including against the separately exclusive Host collection.
[CollectionDefinition("Codex", DisableParallelization = true)]
public sealed class CodexTestCollectionDefinition : ICollectionFixture<CodexTestDirectoryFixture> { }

public sealed class CodexTestDirectoryFixture : IDisposable
{
    public void Dispose() => CodexTestData.Cleanup();
}
