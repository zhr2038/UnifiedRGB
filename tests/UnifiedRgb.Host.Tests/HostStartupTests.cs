using UnifiedRgb.App.Ipc;

namespace UnifiedRgb.Host.Tests;

public sealed class HostStartupTests
{
    [Fact]
    public async Task An_existing_trusted_Host_is_not_started_again()
    {
        var calls = 0;
        var requested = await HostStartup.EnsureAsync(_ => Task.FromResult(true), () => { calls++; return Task.CompletedTask; });
        requested.Should().BeFalse(); calls.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_Host_requests_the_installed_task_once()
    {
        var calls = 0; var grace = TimeSpan.Zero;
        var requested = await HostStartup.EnsureAsync(delay => { grace = delay; return Task.FromResult(false); }, () => { calls++; return Task.CompletedTask; });
        requested.Should().BeTrue(); calls.Should().Be(1); grace.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Task_failure_propagates_for_the_UI_to_report_without_retries_or_elevation()
    {
        var calls = 0;
        var start = () => HostStartup.EnsureAsync(_ => Task.FromResult(false), () => { calls++; throw new IOException("task denied"); });
        await start.Should().ThrowAsync<IOException>().WithMessage("task denied"); calls.Should().Be(1);
    }

    [Fact]
    public async Task Closing_the_app_cancels_a_pending_start_request()
    {
        using var stop = new CancellationTokenSource(); await stop.CancelAsync();
        var calls = 0;
        var start = () => HostStartup.EnsureAsync(_ => Task.FromResult(false), () => { calls++; return Task.CompletedTask; }, stop.Token);
        await start.Should().ThrowAsync<OperationCanceledException>(); calls.Should().Be(0);
    }

    [Fact]
    public void Only_the_fixed_task_is_requested_through_the_system_binary_without_a_console()
    {
        var command = HostStartup.Command();
        command.FileName.Should().Be(Path.Combine(Environment.SystemDirectory, "schtasks.exe"));
        command.ArgumentList.Should().Equal("/Run", "/TN", "UnifiedRgb Host");
        command.UseShellExecute.Should().BeFalse(); command.CreateNoWindow.Should().BeTrue();
        command.RedirectStandardError.Should().BeTrue(); command.Verb.Should().BeEmpty();
    }
}
