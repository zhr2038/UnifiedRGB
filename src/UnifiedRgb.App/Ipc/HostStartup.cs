using System.Diagnostics;
using System.IO;

namespace UnifiedRgb.App.Ipc;

/// <summary>At an explicit app launch, request only the existing installed Host task if trusted IPC is not ready.
/// Does not register tasks, elevate, run a hardware executable directly, or keep restarting a stopped Host.</summary>
internal static class HostStartup
{
    internal const string TaskName = "UnifiedRgb Host";
    internal static readonly TimeSpan ConnectionGrace = TimeSpan.FromSeconds(2);

    internal static async Task<bool> EnsureAsync(Func<TimeSpan, Task<bool>> connected, Func<Task> startTask, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(startTask);
        if (await connected(ConnectionGrace).ConfigureAwait(false)) return false;
        ct.ThrowIfCancellationRequested();
        await startTask().ConfigureAwait(false);
        return true;
    }

    internal static ProcessStartInfo Command()
    {
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        command.ArgumentList.Add("/Run"); command.ArgumentList.Add("/TN"); command.ArgumentList.Add(TaskName);
        return command;
    }

    internal static async Task StartTaskAsync(CancellationToken ct)
    {
        using var process = Process.Start(Command()) ?? throw new IOException("无法启动后台任务。请检查“UnifiedRgb Host”自启任务是否已安装。");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(); // only our schtasks client, never the hardware Host
            throw new TimeoutException("请求启动后台超时；界面仍会自动重连，不会重复启动后台。");
        }
        var details = (await error.ConfigureAwait(false)).Trim();
        var stdout = (await output.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            if (details.Length == 0) details = stdout;
            if (details.Length > 2048) details = details[..2048];
            throw new IOException($"无法启动后台任务（{process.ExitCode}）：{details}");
        }
    }
}
