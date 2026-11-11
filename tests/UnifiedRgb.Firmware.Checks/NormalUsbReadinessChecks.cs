using UnifiedRgb.Firmware.Worker;

namespace UnifiedRgb.Firmware.Checks;

internal static class NormalUsbReadinessChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        async Task Reject<T>(Func<Task> action, string description) where T : Exception
        {
            try { await action().ConfigureAwait(false); }
            catch (T) { check(true, description); return; }
            throw new InvalidOperationException("Did not reject: " + description);
        }

        var captures = 0; var delays = 0; var elapsed = TimeSpan.Zero;
        Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); delays++; elapsed += duration; return Task.CompletedTask;
        }
        await NormalUsbReadiness.WaitAsync(() => { captures++; return (1, 0); }, Delay, default, () => elapsed).ConfigureAwait(false);
        check(captures == 1 && delays == 0, "one normal, no boot: ready immediately without polling");
        captures = delays = 0; elapsed = TimeSpan.Zero;
        await NormalUsbReadiness.WaitAsync(() => (++captures == 3 ? 1 : 0, 0), Delay, default, () => elapsed).ConfigureAwait(false);
        check(captures == 3 && delays == 2 && elapsed == TimeSpan.FromSeconds(2), "exact absence waits; subsequent unique normal becomes ready");

        foreach (var presence in new[] { (2, 0), (0, 1), (1, 1), (0, 2), (-1, 0), (0, -1) })
        {
            captures = delays = 0; elapsed = TimeSpan.Zero;
            await Reject<InvalidDataException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; return presence; }, Delay, default, () => elapsed),
                $"invalid or boot inventory {presence} rejected immediately").ConfigureAwait(false);
            check(captures == 1 && delays == 0, "bad inventory is never retried");
        }

        captures = delays = 0; elapsed = TimeSpan.Zero;
        await Reject<IOException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; throw new IOException("Metadata unavailable"); }, Delay, default, () => elapsed),
            "metadata error propagates instead of treating it as absence").ConfigureAwait(false);
        check(captures == 1 && delays == 0, "metadata fault has one capture and no retry");
        captures = delays = 0; elapsed = TimeSpan.Zero;
        await Reject<TimeoutException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; return (0, 0); }, Delay, default, () => elapsed),
            "normal absence expires at 120 seconds").ConfigureAwait(false);
        check(captures == 120 && delays == 120 && elapsed == TimeSpan.FromSeconds(120), "absence has bounded inventory and delay counts");
        captures = delays = 0;
        await Reject<TimeoutException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; return (0, 0); },
            (_, _) => { delays++; return Task.CompletedTask; }, default, () => TimeSpan.Zero), "stalled fake clock cannot create an unbounded loop").ConfigureAwait(false);
        check(captures == 120 && delays == 120, "iteration bound remains independent of elapsed clock");

        foreach (var normal in new[] { 0, 1 })
        {
            captures = delays = 0; elapsed = TimeSpan.Zero;
            await Reject<TimeoutException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; elapsed = TimeSpan.FromSeconds(121); return (normal, 0); },
                Delay, default, () => elapsed), "late capture cannot claim success or start another wait").ConfigureAwait(false);
            check(captures == 1 && delays == 0, "late capture is joined and not retried");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); captures = delays = 0;
            await Reject<OperationCanceledException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; return (1, 0); }, Delay, cancelled.Token),
                "pre-cancelled wait does not capture metadata").ConfigureAwait(false);
            check(captures == 0 && delays == 0, "pre-cancel has zero capture and delay");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            captures = delays = 0; elapsed = TimeSpan.Zero;
            await Reject<OperationCanceledException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; cancelled.Cancel(); return (1, 0); },
                Delay, cancelled.Token, () => elapsed), "cancellation during capture wins over ready result").ConfigureAwait(false);
            check(captures == 1 && delays == 0, "capture-time cancel joins capture and never proceeds");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            captures = delays = 0; elapsed = TimeSpan.Zero;
            await Reject<OperationCanceledException>(() => NormalUsbReadiness.WaitAsync(() => { captures++; return (0, 0); },
                (_, token) => { delays++; cancelled.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancelled.Token, () => elapsed),
                "delay cancellation is propagated").ConfigureAwait(false);
            check(captures == 1 && delays == 1, "delay cancellation never recaptures inventory");
        }
    }
}
