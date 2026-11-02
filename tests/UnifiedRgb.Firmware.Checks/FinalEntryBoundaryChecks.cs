using UnifiedRgb.Firmware.Worker;

namespace UnifiedRgb.Firmware.Checks;

internal static class FinalEntryBoundaryChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string Dir() { var path = Path.Combine(root, "last-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        var target = AutoEntryChecks.Target;
        var normal = new NormalObservation("1C24", "001200000025000100", target.ContainerId, target.PhysicalLocation,
            DateTimeOffset.UtcNow, "fake", RetainedBaselinePolicy.Origin);
        var authorization = new PhysicalPreparationAuthorization(Guid.NewGuid(), true, true);
        foreach (var kind in new[] { "observer-fault", "observer-completed", "target-changed", "cancelled" })
        {
            var channel = new EntryChannel(); var current = target;
            var observed = new TaskCompletionSource<BootObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();
            CancellationTokenRegistration observerCancellation = default;
            var entry = new AutomaticBootEntry(() => current, _ => channel, (normalIgnored, ct) =>
            {
                observerCancellation = ct.Register(() => observed.TrySetCanceled(ct));
                return observed.Task; // the actual observer task, without an asynchronously propagating proxy
            });
            Exception? failure = null;
            try
            {
                await entry.ExecuteAsync(normal, target, Dir(), authorization, () =>
                {
                    if (kind == "observer-fault") observed.TrySetException(new IOException("Observer failed during durable admission"));
                    else if (kind == "observer-completed") observed.TrySetResult(AutoEntryChecks.Boot);
                    else if (kind == "target-changed") current = target with { UsbAncestor = target.UsbAncestor + "-changed" };
                    else stop.Cancel();
                }, stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { failure = ex; }
            finally { observerCancellation.Dispose(); }
            check(failure is not null && channel.Writes == 0 && channel.Reads == 0 && channel.Disposals == 1,
                "admission callback " + kind + " cannot cross final write boundary; zero reports and closed old handle");
        }
        var emptyChannel = new EntryChannel(); var admissions = 0; var samples = 0;
        var emptyEntry = new AutomaticBootEntry(() => target, _ => emptyChannel,
            (_, ct) => BootTransitionWatcher.WatchAsync(target, () => { samples++; return new([], []); },
                token => Task.Delay(Timeout.Infinite, token), ct));
        Exception? emptyFailure = null;
        try { await emptyEntry.ExecuteAsync(normal, target, Dir(), authorization, () => admissions++, CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidDataException ex) { emptyFailure = ex; }
        check(emptyFailure is not null && samples == 1 && admissions == 0 && emptyChannel.Writes == 0 && emptyChannel.Reads == 0 && emptyChannel.Disposals == 1,
            "initial missing-normal/empty-boot snapshot is not observer readiness; zero-send before admission");
    }
}
