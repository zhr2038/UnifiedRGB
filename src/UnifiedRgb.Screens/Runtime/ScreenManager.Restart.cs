using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// The manual fan-LCD group restart (OPUS-LCD-RESTART-UI-BACKEND). Order: config and RF precheck without I/O → screen mutation
/// gate (bounded wait; no scene change or asset delete can interleave) → the mapped TL workers are disposed and kept from being
/// re-created, other screens untouched → RF reset through <see cref="IFanLcdGroupRecovery"/> → settle and wait for the mapped
/// USB identities → workers restored from the CURRENT scene and pause flag (a StopScreens pause stays) → gate released → wait
/// for new frames from the screens whose output was running. The restore runs on every path, failure and cancellation included.
/// </summary>
public sealed partial class ScreenManager
{
    private readonly IFanLcdGroupRecovery? _lcdRecovery;
    private HashSet<string>? _quiesced; // under _gate: mapped screens held without a worker while a restart runs
    private int _restartBusy;
    private long? _lastRestartSend; // Stopwatch timestamp of the latest actual RF send (cooldown), under _gate

    /// <summary>Minimum time from the previous restart's first actual send.</summary>
    internal TimeSpan RestartCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Longest wait for the screen mutation gate; a busy gate refuses the restart before any I/O.</summary>
    internal TimeSpan RestartGateWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Mapped workers stay quiesced at least this long after the sends (the screens are rebooting).</summary>
    internal TimeSpan RestartSettle { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait from the first send for the mapped identities and their new frames.</summary>
    internal TimeSpan RestartReconnectWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard bound of the whole handler (the reconnect wait is cut to fit).</summary>
    internal TimeSpan RestartTotal { get; set; } = TimeSpan.FromSeconds(28);

    /// <summary>True while a restart holds mapped screens without workers.</summary>
    internal bool RestartQuiescing
    {
        get
        {
            lock (_gate)
            {
                return _quiesced is not null;
            }
        }
    }

    /// <summary>Whether the screen currently has a worker (tests).</summary>
    internal bool HasWorker(string screenId)
    {
        lock (_gate)
        {
            return _workers.ContainsKey(screenId);
        }
    }

    public async Task<ScreenRestartDto> RestartFanLcdAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var config = _options.FanLcdRestart ?? new FanLcdRestartOptions();
        string[] ids = [.. config.ScreenIds ?? []];
        if (!config.Enabled)
        {
            return Refuse("Disabled", ids, "the fan-LCD restart is not configured (UnifiedRgb:Screens:FanLcdRestart Enabled/ControllerId/ScreenIds); nothing was sent.");
        }

        if (!_options.OutputAllowed(ScreenFamily.TlLcd))
            return Refuse("Disabled", ids, "the screen master or TL output gate is closed; nothing was sent.");

        if (ConfigError(config) is { } error)
        {
            return Refuse("Precheck", ids, error + "; nothing was sent.");
        }

        if (_lcdRecovery is null)
        {
            return Refuse("Precheck", ids, "this Host has no Lian Li fan-LCD recovery; nothing was sent.");
        }

        if (Interlocked.CompareExchange(ref _restartBusy, 1, 0) != 0)
        {
            return Refuse("Busy", ids, "a fan-LCD restart is already in progress; nothing was sent.");
        }

        try
        {
            lock (_gate)
            {
                if (_lastRestartSend is { } last && Stopwatch.GetElapsedTime(last) is var since && since < RestartCooldown)
                {
                    var wait = Math.Ceiling((RestartCooldown - since).TotalSeconds);
                    return Refuse("Cooldown", ids, string.Create(CultureInfo.InvariantCulture,
                        $"the previous restart sent its command {since.TotalSeconds:0} s ago; wait {wait:0} s more. Nothing was sent."));
                }
            }

            try
            {
                _lcdRecovery.Validate(config.ControllerId, ids.Length);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Refuse("Precheck", ids, ex.Message + " Nothing was sent.");
            }

            return await RestartAsync(config.ControllerId, ids, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _restartBusy, 0);
        }
    }

    /// <summary>The mapping, without I/O: one Lian Li controller id and 1..8 distinct, valid TL-LCD screen ids.</summary>
    private static string? ConfigError(FanLcdRestartOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.ControllerId) || !config.ControllerId.StartsWith("lianli:rf:", StringComparison.Ordinal))
        {
            return $"FanLcdRestart:ControllerId '{config.ControllerId}' is not a Lian Li RF controller id (lianli:rf:<MAC>)";
        }

        var ids = config.ScreenIds ?? [];
        if (ids.Length is 0 or > 8)
        {
            return $"FanLcdRestart:ScreenIds must list the group's TL-LCD screens (1..8 ids, found {ids.Length})";
        }

        foreach (var id in ids)
        {
            if (!ScreenIds.TryParse(id, out var family, out _) || family.Family != ScreenFamily.TlLcd)
            {
                return $"FanLcdRestart:ScreenIds entry '{id}' is not a TL-LCD screen id (screen:tl-lcd:<instance>); H2 and TURZX have no verified reset";
            }
        }

        return ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ? "FanLcdRestart:ScreenIds contains a duplicate id" : null;
    }

    private async Task<ScreenRestartDto> RestartAsync(string controllerId, string[] ids, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(RestartTotal);
        var token = linked.Token;
        Interlocked.Increment(ref _mutationWaiters);
        bool entered;
        try
        {
            entered = await _mutation.WaitAsync(RestartGateWait, token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _mutationWaiters);
        }

        if (!entered)
        {
            return Refuse("Busy", ids, "a screen scene change or asset delete holds the screen gate; nothing was sent.");
        }

        FanLcdGroupResetResult? reset = null;
        string? failure = null;
        long? sentAt = null;
        var reconnectEnd = TimeSpan.Zero;
        try
        {
            ScreenWorker[] stopped;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureScene();
                _quiesced = new HashSet<string>(ids, StringComparer.Ordinal);
                stopped = [.. ids.Where(_workers.ContainsKey).Select(id => _workers[id])];
                foreach (var w in stopped)
                {
                    _workers.Remove(w.Device.Id);
                }
            }

            LogRestartQuiesced(_log, controllerId, stopped.Length, ids.Length);
            if (stopped.Length > 0)
            {
                // closes the handles before the screens drop off the bus; each Dispose aborts its transfer (bounded join)
                await Task.Run(() => Parallel.ForEach(stopped, w => w.Dispose()), CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                reset = await _lcdRecovery!.ResetAsync(controllerId, ids.Length, () =>
                {
                    var now = Stopwatch.GetTimestamp();
                    lock (_gate)
                    {
                        _lastRestartSend = now;
                    }

                    sentAt = now;
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException or InvalidDataException)
            {
                failure = ex.Message; // before any send (the recovery contract); after a send it returns a result instead
            }

            if (sentAt is not null)
            {
                var budget = RestartTotal - clock.Elapsed;
                reconnectEnd = clock.Elapsed + (RestartReconnectWait < budget ? RestartReconnectWait : budget);
                await Task.Delay(RestartSettle, token).ConfigureAwait(false);
                while (clock.Elapsed < reconnectEnd && !AllPresent(ids))
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                lock (_gate)
                {
                    _quiesced = null;
                }

                if (_options.Enabled)
                {
                    Reconcile(Present(TimeSpan.Zero)); // current assignments and the CURRENT pause flag; never unpauses
                }
            }
            finally
            {
                _mutation.Release();
            }
        }

        if (sentAt is not null)
        {
            while (clock.Elapsed < reconnectEnd && !Reconnected(ids))
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                if (_options.Enabled)
                {
                    Reconcile(Present(TimeSpan.Zero));
                }
            }
        }

        return Report(ids, reset, failure, sentAt is not null);
    }

    private bool AllPresent(string[] ids)
    {
        var present = Present(TimeSpan.Zero);
        return ids.All(id => present.Any(d => d.Id == id));
    }

    // Caller holds _gate. Output was running: gates open, not paused, an enabled assignment.
    private bool OutputRunning(string id) =>
        _options.OutputAllowed(ScreenFamily.TlLcd) && !_paused && AssignmentFor(id) is { Enabled: true };

    private bool Reconnected(string[] ids)
    {
        if (!AllPresent(ids))
        {
            return false;
        }

        lock (_gate)
        {
            return ids.All(id => !OutputRunning(id) || (_workers.TryGetValue(id, out var w) && w.Snapshot().FramesWritten > 0));
        }
    }

    private ScreenRestartDto Report(string[] ids, FanLcdGroupResetResult? reset, string? failure, bool sent)
    {
        if (!sent)
        {
            var reason = failure ?? reset?.Detail ?? "the recovery returned without sending";
            LogRestartRefused(_log, "Precheck", reason);
            return new ScreenRestartDto(false, false, 0, ids, "Precheck: " + reason + " Nothing was sent; the screens were restored.");
        }

        var present = Present(TimeSpan.Zero);
        var lines = new List<string>();
        var allPresent = true;
        var resumed = true;
        var enumerationOnly = new List<string>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                var here = present.Any(d => d.Id == id);
                allPresent &= here;
                var running = OutputRunning(id);
                if (!running)
                {
                    enumerationOnly.Add(id);
                }

                var snapshot = _workers.TryGetValue(id, out var w) ? w.Snapshot() : null;
                resumed &= !running || snapshot is { FramesWritten: > 0 };
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{id}: {(here ? "present" : "ABSENT")}, {snapshot?.State ?? (here ? "no worker" : ScreenStates.Disconnected)}, {snapshot?.FramesWritten ?? 0} new frames{(running ? string.Empty : " (output not running: enumeration only)")}{(snapshot?.LastError is { } e ? ", last error " + e : string.Empty)}"));
            }
        }

        var accepted = reset?.Accepted == true;
        var reconnected = allPresent && resumed;
        var outcome = !accepted ? "NotAccepted" : reconnected ? "Restarted" : "Partial";
        var text = new StringBuilder(outcome).Append(": RF ").Append(accepted ? "acknowledged" : "NOT acknowledged").Append(" (")
            .Append(reset?.Detail ?? failure ?? "no result").Append("); screens ")
            .Append(reconnected ? "reconnected" : allPresent ? "enumerated but not all resumed new frames" : "NOT all enumerated").Append(". ");
        if (enumerationOnly.Count > 0)
        {
            text.Append(enumerationOnly.Count).Append(" screen(s) were not sending (paused, unassigned or output gate closed): enumeration only, still not sending. ");
        }

        text.Append(string.Join("; ", lines)).Append(". Not optical proof.");
        LogRestartDone(_log, outcome, reset?.Attempts ?? 0, reconnected);
        return new ScreenRestartDto(accepted, reconnected, reset?.Attempts ?? 0, ids, text.ToString());
    }

    private ScreenRestartDto Refuse(string outcome, string[] ids, string reason)
    {
        LogRestartRefused(_log, outcome, reason);
        return new ScreenRestartDto(false, false, 0, ids, outcome + ": " + reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fan-LCD restart of {Controller}: quiesced {Stopped} worker(s) of {Mapped} mapped screen(s)")]
    private static partial void LogRestartQuiesced(ILogger logger, string controller, int stopped, int mapped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fan-LCD restart refused ({Outcome}): {Reason}")]
    private static partial void LogRestartRefused(ILogger logger, string outcome, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fan-LCD restart finished: {Outcome}, {Attempts} RF send(s), screens reconnected {Reconnected}")]
    private static partial void LogRestartDone(ILogger logger, string outcome, int attempts, bool reconnected);
}
