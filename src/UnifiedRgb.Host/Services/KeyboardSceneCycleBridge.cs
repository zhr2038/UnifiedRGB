using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// The Host-local bridge from validated native Up/Down steps (<see cref="NativeMenuSceneBrowse"/>, admitted by
/// <see cref="NativeMenuService"/>) to one <see cref="SceneCycleCoordinator"/> on this Host's own RGB and scene-package services,
/// plus the keyboard OLED caption (<see cref="KeyboardSceneCycleOverlay"/>). It never sees raw input and never sends a case action.
/// <para>
/// List: the optional user-ordered file <c>peripherals\scene-cycle.json</c> (<see cref="SceneCycleListFile"/>, kept in its order and
/// fail-closed); without it, the installed scene packages whose current binding is valid and Ready, in the Host's package order,
/// plus ordinary saved RGB scenes whose targets and effects are available, bounded to <see cref="SceneCycleListFile.MaxEntries"/>.
/// Internal recovery scenes and blackouts are excluded. Nothing is read before the first Up/Down step.
/// </para>
/// <para>
/// One serial worker handles every step in order; queued steps are bounded (<see cref="MaxQueuedBrowses"/>) and the coordinator sends
/// at most one checked package apply at a time, for the newest selection only. While the case is dark a step only moves the
/// selection, which is then held. Center stays the existing NativeMenuPress/CasePower Toggle; this bridge only hears of it: a
/// selection that was not written yet yields to an accepted center action, and after a center Toggle that restored the case the
/// held selection is applied once through the existing checked transaction, unless fresh reads show a newer scene choice (the
/// recovery point changed since the selection was held, or the running scene is not the restored recovery point). Nothing is applied
/// at startup, and an unknown outcome is never resent.
/// </para>
/// </summary>
public sealed partial class KeyboardSceneCycleBridge : IHostedService, IAsyncDisposable
{
    public const string ListFileName = "scene-cycle.json";
    public const int MaxQueuedBrowses = 8;
    public const int MaxCandidates = 128;
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CaptionFor = TimeSpan.FromSeconds(4);
    private const int TraceLimit = 64;
    private const int MaxShownName = 48;
    private const string InitialSummary = "上下拨在就绪的场景包和已保存灯光场景之间切换（第一次上下拨时读取列表）；熄灯时只记录选择，按下恢复后才应用。";

    private readonly Func<IRgbService> _rgb;
    private readonly Func<IScenePackageService> _packages;
    private readonly KeyboardSceneCycleOverlay _overlay;
    private readonly TimeProvider _time;
    private readonly SceneCycleOptions _cycleOptions;
    private readonly ILogger _log;
    private readonly string _listPath;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly Queue<string> _trace = new(); // guarded by _sync
    private Task? _worker; // guarded by _sync
    private bool _closed; // guarded by _sync
    private int _queuedBrowses;
    private int _statusQueued;
    private long _captionRevision;
    private long _captionDelayedGeneration;
    private SceneCycleCoordinator? _cycle; // created by the worker, read through Volatile
    private string _summary = InitialSummary; // read and written through Volatile

    // Worker only.
    private DateTimeOffset? _refreshedAt;
    private bool _configured;
    private string? _listProblem;
    private string? _listProblemShort;
    private bool _engaged;
    private bool _heldSeen;
    private long? _deferredBy;
    private string? _capturedRecovery;
    private string? _shownText;
    private ITimer? _hideTimer;

    public KeyboardSceneCycleBridge(UnifiedRgbOptions options, Func<IRgbService> rgb, Func<IScenePackageService> packages,
        KeyboardSceneCycleOverlay overlay, TimeProvider? time = null, ILogger? log = null, SceneCycleOptions? cycleOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rgb = rgb ?? throw new ArgumentNullException(nameof(rgb));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _cycleOptions = cycleOptions ?? new SceneCycleOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_cycleOptions.SettleDelay, TimeSpan.Zero, nameof(cycleOptions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_cycleOptions.SettleDelay, SceneCycleOptions.MaxSettleDelay, nameof(cycleOptions));
        _listPath = Path.Combine(options.ResolvedDataDirectory, "peripherals", ListFileName);
    }

    /// <summary>Short status for the native-menu detail: list source and size, the selection and its reported state, never more.</summary>
    public string Summary => Volatile.Read(ref _summary);

    /// <summary>The coordinator's current status, or null before the first step (tests and diagnostics).</summary>
    internal SceneCycleStatus? Current => Volatile.Read(ref _cycle)?.Current;

    /// <summary>Bounded snapshot of what this bridge did, newest last (tests and diagnostics).</summary>
    internal string[] Trace
    {
        get { lock (_sync) return [.. _trace]; }
    }

    /// <summary>Queues one admitted Up/Down step; false when the bounded queue is full or the bridge stopped. Never blocks or throws.</summary>
    public bool Browse(SceneCycleMove move)
    {
        if (!Enum.IsDefined(move))
        {
            return false;
        }

        if (Interlocked.Increment(ref _queuedBrowses) > MaxQueuedBrowses)
        {
            Interlocked.Decrement(ref _queuedBrowses);
            Record($"browse {move} dropped: {MaxQueuedBrowses} steps already queued");
            return false;
        }

        if (Post(new BrowseCommand(move)))
        {
            return true;
        }

        Interlocked.Decrement(ref _queuedBrowses);
        return false;
    }

    /// <summary>
    /// A center press was admitted for the existing case Toggle: a selection that was chosen but not written yet yields to it (held,
    /// its attempt superseded before any write). Synchronous and bounded; sends and reads nothing; never throws.
    /// </summary>
    public void CenterAccepted(long ticket)
    {
        var held = false;
#pragma warning disable CA1031 // the center path must never fail because of the scene cycle
        try
        {
            held = Volatile.Read(ref _cycle)?.Defer() == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogStepFailed(ex);
        }
#pragma warning restore CA1031

        if (held)
        {
            Record($"center #{ticket} accepted: the unsent selection is held for it");
        }

        Post(new CenterAcceptedCommand(ticket, held));
    }

    /// <summary>The outcome of an admitted center press (as reported, never upgraded). Never blocks or throws.</summary>
    public void CenterCompleted(long ticket, NativeMenuCaseOutcome outcome, bool latest)
    {
        if (outcome is null)
        {
            return;
        }

        Post(new CenterCompletedCommand(ticket, outcome.ResolvedAction ?? outcome.Intent.Action, outcome.State, outcome.Stale, latest));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops the worker and the coordinator: a request in flight stays unknown, nothing more is sent, the caption is removed.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => CloseAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await CloseAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task CloseAsync(CancellationToken ct)
    {
        Task? worker;
        bool first;
        lock (_sync)
        {
            first = !_closed;
            _closed = true;
            worker = _worker;
            if (first)
            {
                _commands.Writer.TryComplete();
            }
        }

        if (first)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        var joined = worker is null;
        if (worker is not null)
        {
            try
            {
#pragma warning disable VSTHRD003 // this bridge owns and cancels its one worker task
                await worker.WaitAsync(ct).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                joined = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        if (!first)
        {
            return;
        }

        Volatile.Read(ref _cycle)?.Dispose(); // an unanswered write stays unknown and is neither retried nor read back
        _overlay.Show(null);
        if (joined)
        {
            if (_hideTimer is { } timer)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }

            _stop.Dispose();
        }
    }

    private bool Post(Command command)
    {
        lock (_sync)
        {
            if (_closed || !_commands.Writer.TryWrite(command))
            {
                return false;
            }

            if (_worker is null)
            {
                var stop = _stop.Token;
                _worker = Task.Run(() => RunAsync(stop), CancellationToken.None);
            }

            return true;
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
#pragma warning disable CA1031 // one failed step is reported; the worker keeps serving later steps
                try
                {
                    await HandleAsync(command, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Record("step failed: " + ex.GetType().Name);
                    LogStepFailed(ex);
                }
#pragma warning restore CA1031
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(Command command, CancellationToken stop)
    {
        switch (command)
        {
            case BrowseCommand browse:
                Interlocked.Decrement(ref _queuedBrowses);
                await BrowseAsync(browse.Move, stop).ConfigureAwait(false);
                break;
            case CenterAcceptedCommand accepted:
                if (accepted.Held)
                {
                    // Held for this center action: its dark period, if any, starts with that action, not before it.
                    _deferredBy = accepted.Ticket;
                    _heldSeen = true;
                    _capturedRecovery = null;
                }

                break;
            case CenterCompletedCommand completed:
                await CenterCompletedAsync(completed, stop).ConfigureAwait(false);
                break;
            case StatusCommand:
                Interlocked.Exchange(ref _statusQueued, 0);
                await StatusAsync(stop).ConfigureAwait(false);
                break;
            case TransmitCaptionCommand transmit:
                _engaged = true;
                Show(transmit.Status);
                transmit.Ready.TrySetResult(_overlay.Current);
                break;
            case HideCommand hide:
                if (hide.Revision == Interlocked.Read(ref _captionRevision))
                {
                    _engaged = false;
                    _shownText = null;
                    _overlay.Show(null);
                }

                break;
        }
    }

    private async Task BrowseAsync(SceneCycleMove move, CancellationToken stop)
    {
        var cycle = EnsureCycle();
        _engaged = true;
        if (cycle.Current.Phase == SceneCyclePhase.NoList || _refreshedAt is not { } at || _time.GetUtcNow() - at >= RefreshAfter)
        {
            await RefreshAsync(cycle, stop).ConfigureAwait(false);
        }

        // Publish the selected name/index on this worker before any new scene apply starts. A queued Current read can miss Selecting.
        var status = await cycle.MoveAsync(move, Show, stop).ConfigureAwait(false);
        Record($"browse {move}: {Describe(status)}");
        RequestStatus(); // also when Move changed nothing (no list), so the caption tells why
    }

    private async Task RefreshAsync(SceneCycleCoordinator cycle, CancellationToken stop)
    {
        using var deadline = new CancellationTokenSource(ReadTimeout, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stop);
        try
        {
            var status = await cycle.RefreshAsync(bound.Token).ConfigureAwait(false);
            _refreshedAt = _time.GetUtcNow();
            Record(status.Phase == SceneCyclePhase.NoList ? "list refreshed: nothing selectable" : $"list refreshed: {status.Count} entries");
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _listProblem = "读取场景列表超时：上下拨暂不切换，稍后再试。";
            _listProblemShort = "场景列表读取超时";
            Record("list refresh timed out; the previous list (if any) stays");
        }
    }

    /// <summary>The coordinator's list loader (called inside RefreshAsync on this worker).</summary>
    private async Task<SceneCycleList> LoadListAsync(CancellationToken ct)
    {
        _listProblem = null;
        _listProblemShort = null;
        if (File.Exists(_listPath))
        {
            _configured = true;
            try
            {
                return SceneCycleListFile.Load(_listPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _listProblem = $"本机场景循环列表 {_listPath} 无效或无法读取（{ex.Message}）：上下拨不切换；修正或删除该文件后恢复自动列表。";
                _listProblemShort = "场景循环列表无效";
                throw;
            }
        }

        _configured = false;
        var packages = _packages();
        IReadOnlyList<ScenePackageInfoDto> installed;
#pragma warning disable CA1031 // a failed read makes nothing eligible (fail closed); cancellation propagates
        try
        {
            installed = await packages.ListScenePackagesAsync(ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        {
            installed = []; // saved RGB scenes remain usable even if the package list cannot be read
        }

        var entries = new List<SceneCycleEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var checkedCount = 0;
        foreach (var info in installed)
        {
            if (entries.Count == SceneCycleListFile.MaxEntries || checkedCount == MaxCandidates)
            {
                break;
            }

            if (info is null || !ScenePackageFormat.IsSha256Hex(info.ContentId) || !seen.Add(info.ContentId))
            {
                continue;
            }

            checkedCount++;
            ScenePackageBindingStateDto? binding;
            try
            {
                binding = await packages.GetScenePackageBindingsAsync(info.ContentId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                continue; // not readable now: not eligible
            }

            // Directly applicable now: a saved, hash-identified binding whose targets are present (the GUI's Ready).
            if (binding is { Ready: true } && string.Equals(binding.ContentId, info.ContentId, StringComparison.Ordinal)
                && ScenePackageFormat.IsSha256Hex(binding.BindingId))
            {
                entries.Add(SceneCycleEntry.Package(info.ContentId));
            }
        }

        if (entries.Count < SceneCycleListFile.MaxEntries)
        {
            try
            {
                var rgb = _rgb();
                var profiles = await rgb.ListProfilesAsync(ct).ConfigureAwait(false);
                var controllers = await rgb.ListControllersAsync(ct).ConfigureAwait(false);
                var effects = await rgb.ListEffectsAsync(ct).ConfigureAwait(false);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var info in profiles.Take(MaxCandidates))
                {
                    if (entries.Count == SceneCycleListFile.MaxEntries) break;
                    if (info is null || !ProfileStore.IsValidName(info.Name) || SceneCycleListFile.IsReservedProfileName(info.Name)
                        || !names.Add(info.Name)) continue;
                    try
                    {
                        var profile = await rgb.LoadProfileAsync(info.Name, ct).ConfigureAwait(false);
                        if (SceneCycleCoordinator.ProfileProblem(info.Name, profile) is null
                            && SceneCycleCoordinator.TargetProblem(profile.Scene, controllers, effects) is null)
                        {
                            entries.Add(SceneCycleEntry.Profile(info.Name));
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
                    {
                        // This saved scene is not readable now; later usable scenes remain selectable.
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                // Package scenes remain usable when ordinary saved scenes cannot be checked now.
            }
        }
#pragma warning restore CA1031

        if (entries.Count == 0)
        {
            _listProblem = "没有可切换的就绪场景：请保存灯光场景并确认设备在线，或为已安装场景包保存就绪绑定。";
            _listProblemShort = "没有就绪的场景";
            throw new InvalidOperationException("No scene package or saved RGB scene is ready now.");
        }

        return new SceneCycleList(SceneCycleListFile.CurrentVersion, entries);
    }

    private async Task StatusAsync(CancellationToken stop)
    {
        if (Volatile.Read(ref _cycle) is not { } cycle)
        {
            return;
        }

        var status = cycle.Current;
        if (!status.Held)
        {
            _heldSeen = false;
            _deferredBy = null;
            _capturedRecovery = null;
        }
        else if (!_heldSeen)
        {
            // Held because the case was dark: remember this dark period's recovery point. A newer scene chosen meanwhile and then
            // saved by a later lights-off changes it, and the held selection is then never applied over that choice.
            _heldSeen = true;
            _capturedRecovery = await ReadRecoveryAsync(stop).ConfigureAwait(false);
            status = cycle.Current;
        }

        Show(status);
    }

    private async Task CenterCompletedAsync(CenterCompletedCommand completed, CancellationToken stop)
    {
        var deferred = _deferredBy == completed.Ticket;
        if (Volatile.Read(ref _cycle) is not { } cycle)
        {
            return;
        }

        var status = cycle.Current;
        var restored = completed.Action == DesiredAction.EnsureOn && completed.State == NativeMenuCaseOutcomeState.Applied;
        if (!status.Held)
        {
            if (deferred)
            {
                _deferredBy = null;
            }

            // A step made while this restore ran may still hold a dark first read: it is decided again from fresh reads.
            if (restored && !completed.Stale && completed.Latest && Undecided(status))
            {
                Record($"center #{completed.Ticket} restored the case: the undecided selection is decided again from fresh reads");
                _ = ObserveApplyAsync(cycle.ApplySelectedAsync(stop));
            }

            return;
        }

        if (restored)
        {
            if (completed.Stale || !completed.Latest)
            {
                Record($"center #{completed.Ticket} restored the case, but a newer center press or context decides; the selection stays held");
                return;
            }

            _engaged = true;
            await ApplyHeldAsync(cycle, deferred, completed.Ticket, stop).ConfigureAwait(false);
            return;
        }

        if (!deferred)
        {
            return; // another center outcome: the recovery-point check at the next restore covers what changed meanwhile
        }

        _deferredBy = null;
        if (!completed.Stale && completed.Action == DesiredAction.EnsureOff
            && completed.State is NativeMenuCaseOutcomeState.Applied or NativeMenuCaseOutcomeState.Unchanged)
        {
            // The center action that took precedence turned the lights off: this dark period starts now.
            _capturedRecovery = await ReadRecoveryAsync(stop).ConfigureAwait(false);
            Record($"center #{completed.Ticket} lights-off: the held selection waits for an explicit restore");
            return;
        }

        _engaged = true;
        cycle.ReleaseHold(SceneCyclePhase.Unavailable,
            $"The center action that took precedence ended {completed.State} without a restore or lights-off; the held selection was not sent. Press up or down again to choose.");
    }

    private async Task ApplyHeldAsync(SceneCycleCoordinator cycle, bool deferred, long ticket, CancellationToken stop)
    {
        string? recovery;
        string? running;
        using (var deadline = new CancellationTokenSource(ReadTimeout, _time))
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stop))
        {
#pragma warning disable CA1031 // a failed fresh read is a known no-write
            try
            {
                var rgb = _rgb();
                var profile = await rgb.LoadProfileAsync(CasePowerCoordinator.BackupProfileName, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
                var scene = await rgb.GetSceneAsync(bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
                recovery = RgbJson(profile?.Scene);
                running = RgbJson(scene);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !stop.IsCancellationRequested)
            {
                cycle.ReleaseHold(SceneCyclePhase.Unavailable,
                    $"After the restore the recovery point or the running scene could not be read ({ex.Message}); the held selection was not sent.");
                return;
            }
#pragma warning restore CA1031
        }

        if (recovery is null || running is null)
        {
            cycle.ReleaseHold(SceneCyclePhase.Unavailable, "After the restore the recovery point or the running scene was empty; the held selection was not sent.");
            return;
        }

        if (!deferred && !string.Equals(recovery, _capturedRecovery, StringComparison.Ordinal))
        {
            if (_capturedRecovery is null)
            {
                cycle.ReleaseHold(SceneCyclePhase.Unavailable,
                    "The recovery point could not be read when the selection was held, so a newer scene choice cannot be excluded; nothing was sent.");
            }
            else
            {
                cycle.ReleaseHold(SceneCyclePhase.Conflict,
                    "The recovery point changed after the selection was held (a newer scene was chosen meanwhile); that scene was kept and nothing was sent.");
            }

            return;
        }

        if (!string.Equals(running, recovery, StringComparison.Ordinal))
        {
            cycle.ReleaseHold(SceneCyclePhase.Conflict,
                "The running scene is not the restored recovery point (a newer scene was chosen after the restore); it was kept and nothing was sent.");
            return;
        }

        _capturedRecovery = null;
        _deferredBy = null;
        Record($"center #{ticket} restored the case: applying the held selection through the checked apply");
        _ = ObserveApplyAsync(cycle.ApplySelectedAsync(stop));
    }

    // The recovery point's RGB part as wire JSON, or null when it is absent or unreadable.
    private async Task<string?> ReadRecoveryAsync(CancellationToken stop)
    {
        using var deadline = new CancellationTokenSource(ReadTimeout, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stop);
#pragma warning disable CA1031 // unreadable means no snapshot: the held selection is then never applied automatically
        try
        {
            var profile = await _rgb().LoadProfileAsync(CasePowerCoordinator.BackupProfileName, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            return RgbJson(profile?.Scene);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !stop.IsCancellationRequested)
        {
            Record("recovery point unreadable while held: " + ex.GetType().Name);
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task ObserveApplyAsync(Task<SceneCycleStatus> apply)
    {
#pragma warning disable CA1031 // the apply outcome is the coordinator's; this only records how waiting for it ended
        try
        {
            var status = await apply.ConfigureAwait(false);
            Record($"explicit apply finished: {Describe(status)}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            Record("explicit apply stopped with the Host; its outcome stays as reported");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogStepFailed(ex);
        }
#pragma warning restore CA1031
    }

    private SceneCycleCoordinator EnsureCycle()
    {
        if (Volatile.Read(ref _cycle) is { } existing)
        {
            return existing;
        }

        var cycle = new SceneCycleCoordinator(_rgb(), _packages(), CaseStatusSummary.IsBlackout, LoadListAsync, _cycleOptions,
            (wait, ct) => wait <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(wait, _time, ct), _log, TransmitSelectionAsync);
        cycle.StatusChanged += OnStatus;
        Volatile.Write(ref _cycle, cycle);
        return cycle;
    }

    // The single display owner acknowledges the exact overlaid frame after its existing upload completes. This is transmission,
    // not optical proof. The existing 10s read bound exceeds its 8s upload bound; unavailable feedback never disables scene control.
    private async Task TransmitSelectionAsync(SceneCycleStatus status, CancellationToken stop)
    {
        var ready = new TaskCompletionSource<KeyboardSceneCycleCaption?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new TransmitCaptionCommand(status, ready))) return;
        using var deadline = new CancellationTokenSource(ReadTimeout, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stop);
        try
        {
            var caption = await ready.Task.WaitAsync(bound.Token).ConfigureAwait(false);
            if (caption is not null && await _overlay.WaitForTransmissionAsync(caption.Revision, bound.Token).ConfigureAwait(false))
            {
                Record($"caption #{caption.Revision} transmission completed before scene apply");
                return;
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
        }

        stop.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _captionDelayedGeneration, status.Generation);
        Record($"caption transmission unavailable or delayed for scene generation {status.Generation}; continuing the existing scene action without display-first confirmation");
        RequestStatus();
    }

    private void OnStatus(object? sender, SceneCycleStatus status) => RequestStatus();

    private void RequestStatus()
    {
        if (Interlocked.Exchange(ref _statusQueued, 1) == 0 && !Post(new StatusCommand()))
        {
            Interlocked.Exchange(ref _statusQueued, 0);
        }
    }

    // Worker only: the native-menu summary always, the OLED caption only after a step or a restore (for CaptionFor).
    private void Show(SceneCycleStatus status)
    {
        Volatile.Write(ref _summary, Summarize(status));
        if (!_engaged)
        {
            return;
        }

        if (status.Phase == SceneCyclePhase.Closed)
        {
            _shownText = null;
            _overlay.Show(null);
            return;
        }

        var (left, right, index, name) = Compose(status);
        var text = right.Length == 0 ? left : left + " · " + right;
        if (string.Equals(text, _shownText, StringComparison.Ordinal) && _overlay.Current is not null)
        {
            return;
        }

        byte[] strip = [];
#pragma warning disable CA1031 // a caption that cannot be rendered is not drawn; the scene cycle itself is unaffected
        try
        {
            strip = KeyboardSceneCycleOverlay.RenderStrip(left, right);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCaptionFailed(ex);
        }
#pragma warning restore CA1031

        var revision = Interlocked.Increment(ref _captionRevision);
        _overlay.Show(new KeyboardSceneCycleCaption(revision, index, status.Count, name, right, text, strip));
        _shownText = text;
        _hideTimer ??= _time.CreateTimer(_ => Post(new HideCommand(Interlocked.Read(ref _captionRevision))), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // A slow apply keeps its loading caption; the actual result starts the normal feedback timeout.
        _hideTimer.Change(status.Phase is SceneCyclePhase.Selecting or SceneCyclePhase.Applying && !status.Held
            ? Timeout.InfiniteTimeSpan : CaptionFor, Timeout.InfiniteTimeSpan);
    }

    private (string Left, string Right, int Index, string Name) Compose(SceneCycleStatus status)
    {
        if (status.Phase == SceneCyclePhase.NoList || status.Count == 0)
        {
            return (_listProblemShort ?? "没有可切换的场景", "未切换", 0, string.Empty);
        }

        if (status.Selection is not { } selection)
        {
            return ($"共 {status.Count} 个场景", "上下拨选择", 0, string.Empty);
        }

        var name = Shown(selection.Name);
        return ($"{selection.Index}/{status.Count} {name}", Tag(status), selection.Index, name);
    }

    private string Summarize(SceneCycleStatus status)
    {
        if (status.Phase == SceneCyclePhase.Closed)
        {
            return "上下拨场景切换已停止。";
        }

        if (status.Phase == SceneCyclePhase.NoList || status.Count == 0)
        {
            return _listProblem ?? "上下拨当前没有可切换的场景：请保存灯光场景，或在“场景包”页导入场景包并保存绑定。";
        }

        var text = _configured ? $"上下拨按本机列表在 {status.Count} 项之间切换" : $"上下拨在 {status.Count} 个就绪场景之间切换";
        if (status.Selection is { } selection)
        {
            var tag = Tag(status);
            text += $"；当前选择 {selection.Index}/{status.Count} {Shown(selection.Name)}" + (tag.Length == 0 ? string.Empty : $"（{tag}）");
        }

        return text + (Interlocked.Read(ref _captionDelayedGeneration) == status.Generation ? "；键盘提示发送延迟，已继续场景切换" : string.Empty)
            + "；熄灯时只记录选择，按下恢复后才应用。";
    }

    // What the coordinator reported for the shown selection: selected, held (pending), in flight or its own outcome, never more.
    private static string Tag(SceneCycleStatus status)
    {
        if (status.Selection is not { } selection)
        {
            return string.Empty;
        }

        if (status.InFlight is { } inFlight && inFlight.Generation == selection.Generation)
        {
            return "切换中";
        }

        if (status.Held)
        {
            return "恢复后应用";
        }

        if (status.LastOutcome is { } outcome && outcome.Target.Generation == selection.Generation)
        {
            return outcome.Phase switch
            {
                SceneCyclePhase.Applied => "已应用",
                SceneCyclePhase.ReadbackMatches => "回读一致",
                SceneCyclePhase.Accepted => "已接受",
                SceneCyclePhase.Refused => "被拒绝",
                SceneCyclePhase.Unavailable => "未应用",
                SceneCyclePhase.Conflict => "保留新场景",
                SceneCyclePhase.LightsOff => "熄灯未发送",
                SceneCyclePhase.Unknown or SceneCyclePhase.UnknownObservedDifferent => "结果未知",
                _ => string.Empty,
            };
        }

        return status.Phase switch
        {
            SceneCyclePhase.Idle => "当前场景",
            SceneCyclePhase.Selecting or SceneCyclePhase.Applying => "切换中",
            _ => string.Empty,
        };
    }

    // A selection that is pending (its attempt not decided yet) and not held.
    private static bool Undecided(SceneCycleStatus status) =>
        status.Phase is SceneCyclePhase.Selecting or SceneCyclePhase.Applying && !status.Held && status.Selection is { } selection
        && !(status.LastOutcome is { } outcome && outcome.Target.Generation == selection.Generation);

    // Wire form of the RGB part only (screens are never part of the recovery point), as CasePowerCoordinator compares it.
    private static string? RgbJson(SceneDto? scene) => scene is not { Assignments: { } assignments }
        ? null
        : JsonSerializer.Serialize(new SceneDto(assignments, Screens: null, Package: scene.Package), ContractsJsonContext.Default.SceneDto);

    private static string Describe(SceneCycleStatus status) =>
        status.Selection is { } s ? $"{status.Phase} #{s.Index}/{status.Count} '{s.Name}'{(status.Held ? " held" : string.Empty)}" : $"{status.Phase}";

    private static string Shown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var text = value.Length > MaxShownName ? value[..MaxShownName] + "…" : value;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    private void Record(string entry)
    {
        lock (_sync)
        {
            _trace.Enqueue(entry);
            while (_trace.Count > TraceLimit)
            {
                _trace.Dequeue();
            }
        }
    }

    [LoggerMessage(EventId = 4211, Level = LogLevel.Warning, Message = "Keyboard scene cycle step failed")]
    private partial void LogStepFailed(Exception exception);

    [LoggerMessage(EventId = 4212, Level = LogLevel.Warning, Message = "Keyboard scene caption could not be rendered; no caption is drawn")]
    private partial void LogCaptionFailed(Exception exception);

    private abstract record Command;

    private sealed record BrowseCommand(SceneCycleMove Move) : Command;

    private sealed record CenterAcceptedCommand(long Ticket, bool Held) : Command;

    private sealed record CenterCompletedCommand(long Ticket, DesiredAction Action, NativeMenuCaseOutcomeState State, bool Stale, bool Latest) : Command;

    private sealed record StatusCommand : Command;

    private sealed record TransmitCaptionCommand(SceneCycleStatus Status, TaskCompletionSource<KeyboardSceneCycleCaption?> Ready) : Command;

    private sealed record HideCommand(long Revision) : Command;
}
