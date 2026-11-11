using System.Diagnostics;

namespace UnifiedRgb.Firmware.Worker;

/// <summary>Passive pre-entry presence only. The existing strict identity/admission path runs after readiness.
/// No handles, getter reports, transition intent or upgrade writes are created by this waiter.</summary>
public static class NormalUsbReadiness
{
    public static async Task WaitAsync(Func<(int Normal, int Boot)> capture,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(delay);
        var started = Stopwatch.GetTimestamp();
        elapsed ??= () => Stopwatch.GetElapsedTime(started);
        var deadline = TimeSpan.FromSeconds(120);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (elapsed() >= deadline || attempt >= 120)
                throw new TimeoutException("等待 USB 有线模式超时；尚未发送升级命令。请保持 USB 线连接，切到有线模式后重新准备。");
            var presence = capture(); // Capture/metadata exceptions remain hard failures; only exact absence waits.
            ct.ThrowIfCancellationRequested();
            if (elapsed() >= deadline)
                throw new TimeoutException("等待 USB 有线模式超时；尚未发送升级命令。请保持 USB 线连接，切到有线模式后重新准备。");
            if (presence.Boot != 0)
                throw new InvalidDataException("已存在升级接口；请先恢复正常 USB 连接，不通过等待绕过新的物理切换。");
            if (presence.Normal is < 0 or > 1)
                throw new InvalidDataException("正常 USB 控制接口数量不唯一；已停止准备，未发送升级命令。");
            if (presence.Normal == 1) return;
            var remaining = deadline - elapsed();
            if (remaining <= TimeSpan.Zero) continue;
            await delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }
}
