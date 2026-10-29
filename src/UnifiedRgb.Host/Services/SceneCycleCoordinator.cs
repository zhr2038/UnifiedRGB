using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Scene cycle over an ordered list (<see cref="SceneCycleListFile"/>). M2 wires it through <see cref="KeyboardSceneCycleBridge"/>
/// only (validated native Up/Down steps, DI, OLED caption); center stays the existing CasePowerCoordinator Toggle, and the bridge
/// asks for an explicit apply of a held selection only after that Toggle restored the case.
/// Only existing application commands are used: GetScene, ListScenePackages, GetScenePackageBindings, ApplyScenePackageChecked,
/// GetScenePackageEditor (a read of the hash-pinned binding, after a lost reply only), ListProfiles, LoadProfile, ListControllers,
/// ListEffects and ApplyProfile. ISceneLibraryService is maintenance only and is not used.
/// <para>
/// <see cref="Move"/> changes only the selection, synchronously. One bounded worker per instance sends at most one request at a
/// time, always for the newest selection after <see cref="SceneCycleOptions.SettleDelay"/>; intermediate selections are never
/// sent, and a press never cancels a request in flight. Every attempt reads the running scene before it prepares anything and
/// again right before its write: a difference means a newer choice by another client or control, so nothing is written
/// (<see cref="SceneCyclePhase.Conflict"/>), or the case went dark (<see cref="SceneCyclePhase.LightsOff"/>). IRgbService has no
/// compare-and-set, so a write by another client between that last read and the Host's commit (and during a request in flight)
/// cannot be excluded; the re-read narrows that window, it does not close it.
/// </para>
/// <para>
/// Packages: a fresh Ready binding is read and passed to the checked transactional apply (the GUI pattern); the original ZIP, assets,
/// hashes, v1 semantics, shared timeline and local rotations are handled by the existing Host path only. A lost reply stays unknown
/// unless the readback shows exactly the requested content, binding and playback contract, reported as
/// <see cref="SceneCyclePhase.ReadbackMatches"/> (current state, not proof). Saved scenes (profiles) are lenient in the Host and carry
/// no kind flag, so only RGB-only saved scenes whose every target is present and active now are sent; others are Unavailable.
/// An unknown outcome is never retried or rolled back and halts automatic sending until a new explicit Move or ApplySelected.
/// </para>
/// While the case is dark the selection stays pending and nothing is written: it is held (<see cref="SceneCycleStatus.Held"/>)
/// until a new press, an explicit <see cref="ApplySelectedAsync"/> or <see cref="ReleaseHold"/>. <see cref="Defer"/> lets a center
/// action take precedence over a selection that was not written yet (a write in flight is never cancelled). Dispose stops the
/// worker, reports a request in flight as Unknown (never a known result) and permits no later request.
/// </summary>
public sealed partial class SceneCycleCoordinator : IDisposable
{
    private static readonly Result Superseded = new(SceneCyclePhase.Selecting, "superseded");
    private static readonly Result Stopped = new(SceneCyclePhase.Closed, "stopped");

    private readonly IRgbService _rgb;
    private readonly IScenePackageService _packages;
    private readonly Func<SceneDto, bool> _isBlackout;
    private readonly Func<CancellationToken, Task<SceneCycleList>> _loadList;
    private readonly TimeSpan _settle;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;
    private readonly Func<SceneCycleStatus, CancellationToken, Task>? _beforeApply;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();

    // Everything below is guarded by _sync.
    private SceneCycleEntry[] _entries = [];
    private string[] _names = [];
    private string?[] _notes = [];
    private bool _hasList;
    private int _cursor = -1;
    private long _generation;
    private long _handled;
    private bool _explicitRequest;
    private long _explicitSerial;
    private bool _immediate;
    private bool _halted;
    private bool _held;
    private bool _closed;
    private bool _cancelIssued;
    private bool _stopDisposed;
    private long _refreshSerial;
    private long _revision;
    private Task? _worker;
    private Target? _inFlight;
    private bool _writing;
    private SceneCycleOutcome? _last;
    private SceneCycleOutcome? _acknowledged;
    private SceneCyclePhase _phase = SceneCyclePhase.NoList;
    private string _detail = "No scene cycle list has been loaded; nothing is selectable.";

    public SceneCycleCoordinator(IRgbService rgb, IScenePackageService packages, Func<SceneDto, bool> isBlackout,
        Func<CancellationToken, Task<SceneCycleList>> loadList, SceneCycleOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay = null, ILogger? log = null)
        : this(rgb, packages, isBlackout, loadList, options, delay, log, null)
    {
    }

    internal SceneCycleCoordinator(IRgbService rgb, IScenePackageService packages, Func<SceneDto, bool> isBlackout,
        Func<CancellationToken, Task<SceneCycleList>> loadList, SceneCycleOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay, ILogger? log, Func<SceneCycleStatus, CancellationToken, Task>? beforeApply)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(isBlackout);
        ArgumentNullException.ThrowIfNull(loadList);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SettleDelay, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.SettleDelay, SceneCycleOptions.MaxSettleDelay, nameof(options));
        _rgb = rgb;
        _packages = packages;
        _isBlackout = isBlackout;
        _loadList = loadList;
        _settle = options.SettleDelay;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _log = log ?? NullLogger.Instance;
        _beforeApply = beforeApply;
    }

    /// <summary>Raised after every state change, outside the lock; snapshots from different threads may arrive out of order (see Revision).</summary>
    public event EventHandler<SceneCycleStatus>? StatusChanged;

    public SceneCycleStatus Current
    {
        get
        {
            lock (_sync) return SnapshotLocked();
        }
    }

    /// <summary>Completes when the worker is idle (tests and diagnostics). Never faults.</summary>
    public Task Settled
    {
        get
        {
            lock (_sync) return _worker ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Loads and validates the list, reads package names and saved-scene presence for display, and places the selection on the entry
    /// of the running package (else none). Never applies anything. A rejected list fails closed (<see cref="SceneCyclePhase.NoList"/>).
    /// A selection still pending when the refresh commits is kept (and still sent) when its entry is in the new list.
    /// </summary>
    public async Task<SceneCycleStatus> RefreshAsync(CancellationToken ct = default)
    {
        long serial;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            serial = ++_refreshSerial;
        }

        SceneCycleList list;
#pragma warning disable CA1031 // any loader or validation failure rejects the whole list (fail closed); cancellation propagates
        try
        {
            list = SceneCycleListFile.Validate(await _loadList(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogListRejected(ex.Message);
            return CommitNoList(serial, $"The scene cycle list was rejected ({ex.Message}); nothing is selectable and nothing will be sent.");
        }

        IReadOnlyList<ScenePackageInfoDto>? installed = null;
        IReadOnlyList<ProfileInfoDto>? saved = null;
        SceneDto? running = null;
        string? installedProblem = null;
        string? savedProblem = null;
        try
        {
            installed = await _packages.ListScenePackagesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            installedProblem = ex.Message;
        }

        try
        {
            saved = await _rgb.ListProfilesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            savedProblem = ex.Message;
        }

        try
        {
            running = await _rgb.GetSceneAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            running = null; // no running package known: no initial selection
        }
#pragma warning restore CA1031
        ct.ThrowIfCancellationRequested();

        var entries = list.Entries;
        var names = new string[entries.Count];
        var notes = new string?[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.IsPackage)
            {
                var info = installed?.FirstOrDefault(p => p is not null && string.Equals(p.ContentId, entry.ContentId, StringComparison.Ordinal));
                names[i] = info is { Name: { } shown } && !string.IsNullOrWhiteSpace(shown) ? shown : "package " + Short(entry.ContentId);
                notes[i] = installed is null ? $"Installed packages could not be listed ({installedProblem})."
                    : info is null ? "Not installed at the last refresh; it will be refused until installed." : null;
            }
            else
            {
                names[i] = entry.Name!;
                var exists = saved?.Any(p => p is not null && string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                notes[i] = saved is null ? $"Saved scenes could not be listed ({savedProblem})."
                    : exists != true ? "Saved scene not found at the last refresh; it will be refused until it exists." : null;
            }
        }

        var runningIndex = await RunningIndexAsync(entries, running, ct).ConfigureAwait(false);

        SceneCycleStatus status;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (serial != _refreshSerial)
            {
                return SnapshotLocked(); // a newer refresh owns the list
            }

            var unsent = _handled != _generation || _explicitRequest;
            var pending = _hasList && _cursor >= 0 && (unsent || _held);
            var keep = pending ? IndexOf(entries, _entries[_cursor]) : -1;
            _entries = [.. entries];
            _names = names;
            _notes = notes;
            _hasList = true;
            _generation++; // any attempt prepared against the old list is superseded before it can write
            var unavailable = notes.Count(n => n is not null);
            var halted = _halted ? " Automatic apply stays halted after an unknown outcome until the next explicit press." : "";
            if (keep >= 0)
            {
                _cursor = keep;
                if (!unsent)
                {
                    _handled = _generation; // a held selection waits for an explicit action; a refresh never sends it
                }

                SetLocked(SceneCyclePhase.Selecting, _held && !unsent
                    ? $"Loaded {entries.Count} entries ({unavailable} with a refresh note); the held selection {Describe(SelectionLocked()!)} is kept and waits for an explicit restore or press.{halted}"
                    : $"Loaded {entries.Count} entries ({unavailable} with a refresh note); the pending selection {Describe(SelectionLocked()!)} is kept.{halted}");
                if (HasWorkLocked())
                {
                    EnsureWorkerLocked();
                }
            }
            else
            {
                _cursor = runningIndex;
                _handled = _generation;
                _explicitRequest = false;
                _held = false;
                SetLocked(SceneCyclePhase.Idle, runningIndex >= 0
                    ? $"Loaded {entries.Count} entries ({unavailable} with a refresh note); the running package is {Describe(SelectionLocked()!)}.{halted}"
                    : $"Loaded {entries.Count} entries ({unavailable} with a refresh note); the running scene is not a listed package, nothing is selected.{halted}");
            }

            status = SnapshotLocked();
        }

        Publish(status);
        return status;
    }

    /// <summary>
    /// Moves the selection with wrap-around and asks the worker to send the newest selection. Synchronous: never awaits a call.
    /// An undefined move value throws before any change; without a valid list this is a no-op (<see cref="SceneCyclePhase.NoList"/>).
    /// A press after an unknown outcome is a new explicit action and lifts the halt.
    /// </summary>
    public SceneCycleStatus Move(SceneCycleMove move) => Move(move, null);

    private SceneCycleStatus Move(SceneCycleMove move, Action<SceneCycleStatus>? selectionReady)
    {
        var step = move switch
        {
            SceneCycleMove.Previous => -1,
            SceneCycleMove.Next => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(move), move, "Unknown scene cycle move; nothing was selected or requested."),
        };
        SceneCycleStatus status;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_hasList)
            {
                return SnapshotLocked();
            }

            var count = _entries.Length;
            _cursor = _cursor < 0 ? (step > 0 ? 0 : count - 1) : (_cursor + step + count) % count;
            _generation++;
            _halted = false;
            _held = false;
            var selection = SelectionLocked()!;
            SetLocked(SceneCyclePhase.Selecting, _inFlight is { } running
                ? $"Selected {Describe(selection)}; not sent yet. The request for {Describe(running.Selection)} is still in flight and is not cancelled."
                : $"Selected {Describe(selection)}; not sent yet.");
            status = SnapshotLocked();
        }

        Publish(status);
        try
        {
            selectionReady?.Invoke(status);
        }
        finally
        {
            lock (_sync)
            {
                if (HasWorkLocked()) EnsureWorkerLocked();
            }
        }

        return status;
    }

    /// <summary>Starts an idle browse from the actual running scene, including ordinary saved RGB scenes. Pending steps keep their order.</summary>
    public Task<SceneCycleStatus> MoveAsync(SceneCycleMove move, CancellationToken ct = default) => MoveAsync(move, null, ct);

    internal async Task<SceneCycleStatus> MoveAsync(SceneCycleMove move, Action<SceneCycleStatus>? selectionReady, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(move))
        {
            throw new ArgumentOutOfRangeException(nameof(move), move, "Unknown scene cycle move; nothing was selected or requested.");
        }

        long generation;
        SceneCycleEntry[] entries;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            generation = _generation;
            entries = _hasList && !HasWorkLocked() && !_held && _inFlight is null ? _entries : [];
        }

        if (entries.Length > 0)
        {
#pragma warning disable CA1031 // an unavailable read keeps the last selection; applying still requires the existing fresh reads
            try
            {
                var running = await _rgb.GetSceneAsync(ct).ConfigureAwait(false);
                var index = await RunningIndexAsync(entries, running, ct).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_generation == generation && !HasWorkLocked() && !_held && _inFlight is null)
                    {
                        _cursor = index;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                // Move still performs the normal checked apply and reports its own outcome.
            }
#pragma warning restore CA1031
        }

        ct.ThrowIfCancellationRequested();
        return Move(move, selectionReady);
    }

    private async Task<int> RunningIndexAsync(IReadOnlyList<SceneCycleEntry> entries, SceneDto? running, CancellationToken ct)
    {
        if (running?.Package?.ContentId is { } runningId)
        {
            return IndexOf(entries, SceneCycleEntry.Package(runningId));
        }

        if (running is null)
        {
            return -1;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsPackage) continue;
#pragma warning disable CA1031 // a missing or unreadable saved scene cannot identify the running selection
            try
            {
                var profile = await _rgb.LoadProfileAsync(entries[i].Name!, ct).ConfigureAwait(false);
                if (profile?.Scene is { } saved && SameJson(saved, saved.Screens is null ? running with { Screens = null } : running,
                    ContractsJsonContext.Default.SceneDto))
                {
                    return i;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
            }
#pragma warning restore CA1031
        }

        return -1;
    }

    /// <summary>
    /// Explicitly asks for the current selection to be sent now (no settle delay), lifting a halt after an unknown outcome. The
    /// keyboard bridge calls it only for a held selection, after the center Toggle restored the case. The attempt reads the scene
    /// fresh as always. Dark → LightsOff, nothing sent, the selection stays held. <paramref name="ct"/> only stops waiting.
    /// </summary>
    public async Task<SceneCycleStatus> ApplySelectedAsync(CancellationToken ct = default)
    {
        Task worker;
        SceneCycleStatus status;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_hasList || _cursor < 0)
            {
                return SnapshotLocked();
            }

            _halted = false;
            _explicitRequest = true;
            _explicitSerial++;
            _immediate = true;
            SetLocked(SceneCyclePhase.Selecting, $"Explicit apply requested for {Describe(SelectionLocked()!)}.");
            worker = EnsureWorkerLocked();
            status = SnapshotLocked();
        }

        Publish(status);
        await worker.WaitAsync(ct).ConfigureAwait(false);
        return Current;
    }

    /// <summary>
    /// A center action was accepted: a selection that was chosen but not written yet yields to it. Its attempt is superseded before
    /// any write (a request already being written is never cancelled) and the selection is held until an explicit restore or press.
    /// Returns whether a selection was held. Sends and reads nothing; after Dispose it does nothing and returns false.
    /// </summary>
    public bool Defer()
    {
        SceneCycleStatus status;
        lock (_sync)
        {
            var unsent = HasWorkLocked() || (_inFlight is { } preparing && !_writing && preparing.Selection.Generation == _generation);
            if (_closed || !unsent || SelectionLocked() is not { } selection)
            {
                return false;
            }

            _generation++; // the attempt for the old generation is refused at BeginWrite
            _handled = _generation;
            _explicitRequest = false;
            _immediate = false;
            _held = true;
            SetLocked(SceneCyclePhase.Selecting,
                $"{Describe(selection)} is held for the center action; nothing was sent for it. It is applied only after an explicit restore, or replaced by a new press.");
            status = SnapshotLocked();
        }

        Publish(status);
        return true;
    }

    /// <summary>
    /// Ends a held selection without sending it, recorded for that selection as <see cref="SceneCyclePhase.Conflict"/> (a fresh
    /// read showed a newer scene choice, which is kept) or <see cref="SceneCyclePhase.Unavailable"/> (a check could not be
    /// completed). Returns false when nothing is held (or after Dispose). Sends nothing.
    /// </summary>
    public bool ReleaseHold(SceneCyclePhase phase, string reason)
    {
        if (phase is not (SceneCyclePhase.Conflict or SceneCyclePhase.Unavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "A held selection ends only as Conflict or Unavailable.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        SceneCycleStatus status;
        SceneCycleOutcome recorded;
        lock (_sync)
        {
            if (_closed || !_held || SelectionLocked() is not { } selection)
            {
                return false;
            }

            _held = false;
            recorded = new SceneCycleOutcome(phase, selection, reason);
            _last = recorded;
            SetLocked(phase, reason);
            status = SnapshotLocked();
        }

        var t = recorded.Target;
        LogOutcome(t.Kind, t.Name, t.Index, t.Generation, recorded.Phase, recorded.Detail);
        Publish(status);
        return true;
    }

    public void Dispose()
    {
        SceneCycleStatus status;
        string? inFlightDetail = null;
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_inFlight is { } target && _writing)
            {
                inFlightDetail = $"Shut down while the request for {Describe(target.Selection)} was in flight; its outcome is unknown. It is neither retried nor read back.";
                _last = new SceneCycleOutcome(SceneCyclePhase.Unknown, target.Selection, inFlightDetail);
                SetLocked(SceneCyclePhase.Closed, inFlightDetail);
            }
            else
            {
                SetLocked(SceneCyclePhase.Closed, _inFlight is { } preparing
                    ? $"Shut down before anything was sent for {Describe(preparing.Selection)}; nothing more will be sent."
                    : "Shut down; nothing more will be sent.");
            }

            status = SnapshotLocked();
        }

        _stop.Cancel();
        bool idle;
        lock (_sync)
        {
            _cancelIssued = true;
            idle = _worker is null && !_stopDisposed;
            _stopDisposed |= idle;
        }

        if (idle)
        {
            _stop.Dispose(); // otherwise the worker disposes it once it has observed the cancellation and stopped
        }

        if (inFlightDetail is not null)
        {
            LogClosedInFlight(inFlightDetail);
        }

        Publish(status);
    }

    private bool HasWorkLocked() =>
        !_closed && _hasList && _cursor >= 0 && !_halted && (_handled != _generation || _explicitRequest);

    private Task EnsureWorkerLocked()
    {
        if (_worker is null)
        {
            var stop = _stop.Token;
            _worker = Task.Run(() => RunAsync(stop), CancellationToken.None);
        }

        return _worker;
    }

    private void DisposeStopWhenIdleLocked()
    {
        if (_closed && _cancelIssued && _worker is null && !_stopDisposed)
        {
            _stopDisposed = true;
            _stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var released = false;
        try
        {
            while (true)
            {
                bool immediate;
                long explicitSerial;
                lock (_sync)
                {
                    if (!HasWorkLocked())
                    {
                        // Released in the same lock as the decision, so a concurrent press always finds or starts a worker.
                        _worker = null;
                        released = true;
                        DisposeStopWhenIdleLocked();
                        return;
                    }

                    immediate = _immediate;
                    _immediate = false;
                    explicitSerial = _explicitSerial;
                }

                await CycleAsync(immediate, explicitSerial, stop).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!released)
            {
                lock (_sync)
                {
                    _worker = null;
                    DisposeStopWhenIdleLocked();
                }
            }
        }
    }

    /// <summary>One attempt for the newest selection. Records exactly one outcome for it, or none when superseded or stopped.</summary>
    private async Task CycleAsync(bool immediate, long explicitSerial, CancellationToken stop)
    {
        Target? target = null;
        Result result;
#pragma warning disable CA1031 // a failed read or an unexpected failure becomes an honest outcome; the worker never dies silently
        try
        {
            // Before any work: the scene as it is now. Every later difference is a newer choice made by someone else.
            SceneDto? baseline = null;
            string? unreadable = null;
            try
            {
                baseline = await _rgb.GetSceneAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                unreadable = ex.Message;
            }

            stop.ThrowIfCancellationRequested();
            string? delayProblem = null;
            if (!immediate)
            {
                try
                {
                    await _delay(_settle, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    delayProblem = ex.Message;
                }

                stop.ThrowIfCancellationRequested();
            }

            SceneCycleStatus started;
            lock (_sync)
            {
                if (!HasWorkLocked())
                {
                    return;
                }

                if (_explicitRequest && _explicitSerial != explicitSerial)
                {
                    return; // an explicit request arrived after this attempt's first read: the next attempt starts from fresh reads
                }

                var selection = SelectionLocked()!;
                target = new Target(_entries[_cursor], selection);
                _handled = _generation;
                _explicitRequest = false;
                _held = false; // this attempt decides again from fresh reads
                _inFlight = target;
                SetLocked(SceneCyclePhase.Applying, $"Preparing {Describe(selection)}.");
                started = SnapshotLocked();
            }

            Publish(started);
            if (delayProblem is not null)
            {
                result = new(SceneCyclePhase.Unavailable, $"The settle delay failed ({delayProblem}); nothing was sent.");
            }
            else if (baseline is null)
            {
                result = new(SceneCyclePhase.Unavailable, $"The running scene could not be read ({unreadable ?? "empty reply"}); nothing was sent.");
            }
            else
            {
                var dark = DarkCheck(baseline, "The case is dark; the selection stays pending and nothing was sent.");
                if (dark is not null)
                {
                    result = dark;
                }
                else
                {
                    if (_beforeApply is not null) await _beforeApply(started, stop).ConfigureAwait(false);
                    stop.ThrowIfCancellationRequested();
                    result = target.Entry.IsPackage
                        ? await PackageAsync(target, baseline, stop).ConfigureAwait(false)
                        : await ProfileAsync(target, baseline, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            result = Stopped;
        }
        catch (Exception ex)
        {
            bool writing;
            lock (_sync) writing = _writing;
            result = writing
                ? new(SceneCyclePhase.Unknown, $"The request failed unexpectedly after it was sent ({ex.Message}); its outcome is unknown and it is not retried.")
                : new(SceneCyclePhase.Unavailable, $"The request failed unexpectedly before anything was sent ({ex.Message}).");
        }
#pragma warning restore CA1031

        if (target is not null)
        {
            Finish(target, result);
        }
    }

    private async Task<Result> PackageAsync(Target target, SceneDto baseline, CancellationToken stop)
    {
        var contentId = target.Entry.ContentId!;
        ScenePackageBindingStateDto? binding;
#pragma warning disable CA1031 // a failed read before the write is a known no-write; cancellation propagates
        try
        {
            binding = await _packages.GetScenePackageBindingsAsync(contentId, stop).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return new(SceneCyclePhase.Unavailable, $"The package is not installed or not readable ({ex.Message}); nothing was sent.");
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unavailable, $"The package binding could not be read ({ex.Message}); nothing was sent.");
        }
#pragma warning restore CA1031

        stop.ThrowIfCancellationRequested();
        if (binding is null || !string.Equals(binding.ContentId, contentId, StringComparison.Ordinal))
        {
            return new(SceneCyclePhase.Unavailable, "The binding reply does not describe this package; nothing was sent.");
        }

        if (!binding.Ready || !ScenePackageFormat.IsSha256Hex(binding.BindingId))
        {
            return new(SceneCyclePhase.Unavailable,
                $"The package binding is not ready now ({Problems(binding.Problems)}); nothing was sent. Press again once its devices are available.");
        }

        var bindingId = binding.BindingId!;
        if (await ExternalChangeAsync(baseline, stop).ConfigureAwait(false) is { } changed)
        {
            return changed;
        }

        if (BeginWrite(target) is { } notWritten)
        {
            return notWritten;
        }

        ScenePackageApplyDto? reply;
#pragma warning disable CA1031 // any failure of the write leaves its outcome unknown: read back, never resend
        try
        {
            reply = await _packages.ApplyScenePackageCheckedAsync(new ScenePackageApplyRequestDto(contentId, bindingId), stop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await AfterLostApplyAsync(contentId, bindingId, ex, stop).ConfigureAwait(false);
        }
#pragma warning restore CA1031

        if (reply is null)
        {
            return await AfterLostApplyAsync(contentId, bindingId, new InvalidDataException("empty reply"), stop).ConfigureAwait(false);
        }

        if (!reply.Applied)
        {
            return new(SceneCyclePhase.Refused, $"The Host refused the checked apply; per its contract nothing changed. {reply.Detail}");
        }

        return string.Equals(reply.ContentId, contentId, StringComparison.Ordinal) && string.Equals(reply.BindingId, bindingId, StringComparison.Ordinal)
            ? new(SceneCyclePhase.Applied, $"The Host applied the package transactionally with binding {Short(bindingId)}. {reply.Detail}")
            : new(SceneCyclePhase.Unknown,
                $"The Host reported Applied for content {Short(reply.ContentId)} binding {Short(reply.BindingId)}, not the requested {Short(contentId)}/{Short(bindingId)}; the outcome is unknown and nothing is retried.");
    }

    /// <summary>
    /// A package write without a usable reply. Unknown unless the readback shows exactly the requested content, binding and playback
    /// contract; even then this is the current state, not proof that this request ran. No resend, no rollback.
    /// </summary>
    private async Task<Result> AfterLostApplyAsync(string contentId, string bindingId, Exception lost, CancellationToken stop)
    {
        if (stop.IsCancellationRequested)
        {
            return Stopped;
        }

        SceneDto? now;
#pragma warning disable CA1031 // a failed readback keeps the outcome unknown
        try
        {
            now = await _rgb.GetSceneAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unknown,
                $"The checked apply got no usable reply ({lost.Message}) and the readback failed ({ex.Message}); the outcome is unknown. Nothing was retried or rolled back.");
        }

        stop.ThrowIfCancellationRequested();
        var package = now?.Package;
        if (package is null || !string.Equals(package.ContentId, contentId, StringComparison.Ordinal)
            || !string.Equals(package.BindingId, bindingId, StringComparison.Ordinal))
        {
            return new(SceneCyclePhase.UnknownObservedDifferent,
                $"The checked apply got no usable reply ({lost.Message}). The current readback shows {Observed(now)}, not content {Short(contentId)} binding {Short(bindingId)}. "
                + "The outcome stays unknown (the request may not have run, or a later change may have replaced it); nothing was retried or rolled back.");
        }

        string expected;
        try
        {
            expected = await ExpectedContractAsync(contentId, bindingId, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unknown,
                $"The checked apply got no usable reply ({lost.Message}). The readback shows the requested content and binding, but its playback contract could not be confirmed ({ex.Message}); the outcome is unknown. Nothing was retried.");
        }
#pragma warning restore CA1031

        stop.ThrowIfCancellationRequested();
        if (!string.Equals(package.PlaybackContract, expected, StringComparison.Ordinal))
        {
            return new(SceneCyclePhase.UnknownObservedDifferent,
                $"The checked apply got no usable reply ({lost.Message}). The readback shows the requested content and binding with playback contract '{package.PlaybackContract}', not '{expected}'; the outcome is unknown. Nothing was retried or rolled back.");
        }

        return new(SceneCyclePhase.ReadbackMatches,
            $"The checked apply got no usable reply ({lost.Message}). The current readback matches the requested content {Short(contentId)}, binding {Short(bindingId)} and contract '{expected}'. "
            + "This describes the current state only; it is not proof that this request performed the change. Nothing was retried.");
    }

    // The Host takes the contract from the immutable binding snapshot (BindingSnapshot.PlaybackContract): identity playback → the
    // package format's contract, an edited playback → ScenePackagePlayback.EditedContract. The snapshot is pinned by its hash, so
    // reading it after the write returns the playback the write used. Any mismatch only keeps the result unknown.
    private async Task<string> ExpectedContractAsync(string contentId, string bindingId, CancellationToken stop)
    {
        var state = await _packages.GetScenePackageEditorAsync(contentId, bindingId, stop).ConfigureAwait(false);
        if (state is null || !string.Equals(state.ContentId, contentId, StringComparison.Ordinal)
            || !string.Equals(state.BindingId, bindingId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("the binding snapshot reply does not describe the requested binding");
        }

        return ScenePackagePlayback.IsIdentity(state.Playback) ? ScenePackageFormat.PlaybackContract : ScenePackagePlayback.EditedContract;
    }

    private async Task<Result> ProfileAsync(Target target, SceneDto baseline, CancellationToken stop)
    {
        var name = target.Entry.Name!;
        if (SceneCycleListFile.IsReservedProfileName(name))
        {
            return new(SceneCyclePhase.Unavailable, "This name is reserved for an internal scene; nothing was sent.");
        }

        ProfileDto? profile;
        IReadOnlyList<ControllerDto>? controllers;
        IReadOnlyList<EffectDto>? effects;
#pragma warning disable CA1031 // a failed read before the write is a known no-write; cancellation propagates
        try
        {
            profile = await _rgb.LoadProfileAsync(name, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unavailable, $"The saved scene could not be loaded ({ex.Message}); nothing was sent.");
        }

        stop.ThrowIfCancellationRequested();
        if (ProfileProblem(name, profile) is { } problem)
        {
            return new(SceneCyclePhase.Unavailable, problem + " Nothing was sent.");
        }

        var loaded = profile!;
        try
        {
            controllers = await _rgb.ListControllersAsync(stop).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            effects = await _rgb.ListEffectsAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unavailable, $"The current devices or effects could not be read ({ex.Message}); nothing was sent.");
        }

        stop.ThrowIfCancellationRequested();
        if (TargetProblem(loaded.Scene, controllers, effects) is { } missing)
        {
            return new(SceneCyclePhase.Unavailable, missing + " Nothing was sent.");
        }

        // ApplyProfile loads the file again in the Host; a change after this re-read cannot be excluded (no pinned apply exists).
        ProfileDto? again;
        try
        {
            again = await _rgb.LoadProfileAsync(name, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unavailable, $"The saved scene could not be re-read before sending ({ex.Message}); nothing was sent.");
        }
#pragma warning restore CA1031

        stop.ThrowIfCancellationRequested();
        if (again is null || !SameJson(loaded, again, ContractsJsonContext.Default.ProfileDto))
        {
            return new(SceneCyclePhase.Conflict, "The saved scene changed while it was being checked; nothing was sent. Press again to check the new version.");
        }

        if (await ExternalChangeAsync(baseline, stop).ConfigureAwait(false) is { } changed)
        {
            return changed;
        }

        if (BeginWrite(target) is { } notWritten)
        {
            return notWritten;
        }

#pragma warning disable CA1031 // any failure of the write leaves its outcome unknown: saved scenes have no exact readback identity
        try
        {
            await _rgb.ApplyProfileAsync(name, stop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return stop.IsCancellationRequested ? Stopped : new(SceneCyclePhase.Unknown,
                $"ApplyProfile failed or got no usable reply ({ex.Message}); the outcome is unknown. A saved scene has no exact readback identity, so no readback claim is made; nothing was retried or rolled back.");
        }
#pragma warning restore CA1031

        return new(SceneCyclePhase.Accepted,
            "The Host accepted the saved scene. This is software acceptance only (ApplyProfile has no transactional acknowledgement); it is not physical confirmation.");
    }

    /// <summary>
    /// Why a loaded saved scene is not directly applicable by this M1 cycle, or null. Saved scenes carry no kind flag and ApplyProfile
    /// keeps zones of absent controllers for later, so eligibility is narrowed to what existing data can check: an RGB-only, valid,
    /// non-lights-off scene without package or frame-set content.
    /// </summary>
    internal static string? ProfileProblem(string name, ProfileDto? profile)
    {
        if (profile?.Scene?.Assignments is not { } assignments)
        {
            return "The saved scene reply is empty or malformed.";
        }

        if (!string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return "The saved scene reply names a different scene.";
        }

        if (profile.SchemaVersion is < 1 or > ProfileDto.CurrentSchema)
        {
            return $"The saved scene schema {profile.SchemaVersion} is not supported.";
        }

        if (profile.Scene.Screens is not null)
        {
            return "This saved scene includes screen content; M1 sends RGB-only saved scenes because screen readiness cannot be checked with the existing client commands. List a scene package for lighting plus screens.";
        }

        if (LightingEngine.IsPackageScene(profile.Scene))
        {
            return "This saved scene pins scene-package content; list the package itself so it goes through the checked transactional apply.";
        }

        if (assignments.Count == 0)
        {
            return "This saved scene has no lighting assignments.";
        }

        foreach (var a in assignments)
        {
            if (a is null)
            {
                return "The saved scene has an empty assignment.";
            }

            if (a.FrameSetId is not null || string.Equals(a.Strategy, "Frames", StringComparison.OrdinalIgnoreCase))
            {
                return "This saved scene uses frame-set content whose readiness cannot be checked here.";
            }
        }

        try
        {
            _ = DtoMapper.FromDto(profile.Scene);
        }
        catch (ArgumentException ex)
        {
            return $"The saved scene is not valid ({ex.Message}).";
        }

        return CaseStatusSummary.IsBlackout(profile.Scene)
            ? "This saved scene is a lights-off scene; lights-off stays with the existing center control."
            : null;
    }

    /// <summary>Every target must be a zone of an Active controller now and every effect must exist, so nothing is applied partially.</summary>
    internal static string? TargetProblem(SceneDto scene, IReadOnlyList<ControllerDto>? controllers, IReadOnlyList<EffectDto>? effects)
    {
        var active = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var c in controllers ?? [])
        {
            if (c?.Id is { } id && string.Equals(c.State, ControllerStates.Active, StringComparison.Ordinal) && c.Zones is { } zones)
            {
                active[id] = zones.Where(z => z is not null).Select(z => z.Id).ToHashSet();
            }
        }

        var known = (effects ?? []).Where(e => e?.Id is not null).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var a in scene.Assignments)
        {
            foreach (var t in a.Targets)
            {
                if (t?.ControllerId is null || !active.TryGetValue(t.ControllerId, out var zones) || !zones.Contains(t.ZoneId))
                {
                    return $"Target {t?.ControllerId ?? "?"}/{t?.ZoneId} is not present and active now; ApplyProfile would keep it for later, so this saved scene would be applied only partially.";
                }
            }

            if (!known.Contains(a.EffectId))
            {
                return $"Effect '{a.EffectId}' is not available on this Host.";
            }
        }

        return null;
    }

    /// <summary>Re-reads the scene right before a write; any difference from the baseline means a newer choice, so nothing is written.</summary>
    private async Task<Result?> ExternalChangeAsync(SceneDto baseline, CancellationToken stop)
    {
        SceneDto? latest;
#pragma warning disable CA1031 // a failed re-read before the write is a known no-write
        try
        {
            latest = await _rgb.GetSceneAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new(SceneCyclePhase.Unavailable, $"The running scene could not be re-read before sending ({ex.Message}); nothing was sent.");
        }
#pragma warning restore CA1031

        stop.ThrowIfCancellationRequested();
        if (latest is not null && SameJson(baseline, latest, ContractsJsonContext.Default.SceneDto))
        {
            return null;
        }

        return latest is null
            ? new(SceneCyclePhase.Unavailable, "The running scene re-read was empty; nothing was sent.")
            : DarkCheck(latest, "The case was turned off while this selection was prepared; nothing was sent and the selection stays pending.")
                ?? new(SceneCyclePhase.Conflict,
                    "The running scene changed after this selection was prepared (another client or control chose a scene); nothing was sent and the newer scene was kept. Press again to choose over it.");
    }

    private Result? DarkCheck(SceneDto scene, string darkDetail)
    {
        bool dark;
#pragma warning disable CA1031 // an injected predicate failure is a known no-write
        try
        {
            dark = _isBlackout(scene);
        }
        catch (Exception ex)
        {
            return new(SceneCyclePhase.Unavailable, $"Whether the case is dark could not be decided ({ex.Message}); nothing was sent.");
        }
#pragma warning restore CA1031

        return dark ? new(SceneCyclePhase.LightsOff, darkDetail) : null;
    }

    /// <summary>The last check before a write, in the lock: after Dispose nothing is written, and a newer press supersedes this target.</summary>
    private Result? BeginWrite(Target target)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return Stopped;
            }

            if (_generation != target.Selection.Generation)
            {
                return Superseded;
            }

            _writing = true;
            return null;
        }
    }

    private void Finish(Target target, Result result)
    {
        SceneCycleStatus status;
        SceneCycleOutcome? recorded = null;
        lock (_sync)
        {
            _inFlight = null;
            _writing = false;
            if (_closed)
            {
                return; // Dispose already reported this attempt; a later result never becomes a known outcome
            }

            if (ReferenceEquals(result, Superseded) || ReferenceEquals(result, Stopped))
            {
                // Nothing was sent. A newer press keeps the worker going; a refresh that consumed the work keeps its own status.
                if (HasWorkLocked())
                {
                    SetLocked(SceneCyclePhase.Selecting, $"A newer selection replaced {Describe(target.Selection)} before anything was sent.");
                }
                else
                {
                    SetLocked(_phase, _detail);
                }
            }
            else
            {
                recorded = new SceneCycleOutcome(result.Phase, target.Selection, result.Detail);
                _last = recorded;
                if (result.Phase is SceneCyclePhase.Applied or SceneCyclePhase.Accepted or SceneCyclePhase.ReadbackMatches)
                {
                    _acknowledged = recorded;
                }

                if (result.Phase is SceneCyclePhase.Unknown or SceneCyclePhase.UnknownObservedDifferent)
                {
                    _halted = true;
                }

                var current = _generation == target.Selection.Generation;
                if (current)
                {
                    // Dark: the selection is held. An explicit request made during that attempt (the case was restored meanwhile)
                    // still runs once from fresh reads; any other result served it.
                    _held = result.Phase == SceneCyclePhase.LightsOff;
                    if (!_held)
                    {
                        _explicitRequest = false; // an explicit request for this selection made during the attempt was served by it
                    }
                }

                if (!_hasList)
                {
                    SetLocked(_phase, _detail); // the list was rejected meanwhile: NoList stays visible, the result is LastOutcome
                }
                else if (_halted && !current)
                {
                    SetLocked(result.Phase, SelectionLocked() is { } newer
                        ? $"{result.Detail} The newer selection {Describe(newer)} was not sent automatically; press again or apply it explicitly."
                        : result.Detail);
                }
                else if (!current && HasWorkLocked())
                {
                    SetLocked(SceneCyclePhase.Selecting,
                        $"{Describe(SelectionLocked()!)} is pending; the earlier request for {Describe(target.Selection)} ended {result.Phase}: {result.Detail}");
                }
                else
                {
                    SetLocked(result.Phase, result.Detail);
                }
            }

            status = SnapshotLocked();
        }

        if (recorded is not null)
        {
            var t = recorded.Target;
            if (recorded.Phase is SceneCyclePhase.Unknown or SceneCyclePhase.UnknownObservedDifferent)
            {
                LogUnknown(t.Kind, t.Name, t.Index, t.Generation, recorded.Phase, recorded.Detail);
            }
            else
            {
                LogOutcome(t.Kind, t.Name, t.Index, t.Generation, recorded.Phase, recorded.Detail);
            }
        }

        Publish(status);
    }

    private SceneCycleStatus CommitNoList(long serial, string detail)
    {
        SceneCycleStatus status;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (serial != _refreshSerial)
            {
                return SnapshotLocked();
            }

            _hasList = false;
            _entries = [];
            _names = [];
            _notes = [];
            _cursor = -1;
            _generation++;
            _handled = _generation;
            _explicitRequest = false;
            _held = false;
            SetLocked(SceneCyclePhase.NoList, detail);
            status = SnapshotLocked();
        }

        Publish(status);
        return status;
    }

    private void SetLocked(SceneCyclePhase phase, string detail)
    {
        _phase = phase;
        _detail = detail;
        _revision++;
    }

    private SceneCycleSelection? SelectionLocked() =>
        _hasList && _cursor >= 0 && _cursor < _entries.Length
            ? new SceneCycleSelection(_cursor + 1, _names[_cursor], _entries[_cursor].Kind, _generation, _notes[_cursor])
            : null;

    private SceneCycleStatus SnapshotLocked() =>
        new(_revision, _hasList ? _entries.Length : 0, _generation, SelectionLocked(), _phase, _detail, _inFlight?.Selection, _last, _acknowledged, _halted,
            _held);

    private void Publish(SceneCycleStatus status)
    {
        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

#pragma warning disable CA1031 // a failing status subscriber must not break browsing or the worker
        try
        {
            handler(this, status);
        }
        catch (Exception ex)
        {
            LogSubscriberFailed(ex);
        }
#pragma warning restore CA1031
    }

    private static int IndexOf(IReadOnlyList<SceneCycleEntry> entries, SceneCycleEntry wanted)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (wanted.IsPackage ? e.IsPackage && string.Equals(e.ContentId, wanted.ContentId, StringComparison.Ordinal)
                : !e.IsPackage && string.Equals(e.Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Wire-form comparison: every reply is a fresh copy, and record equality compares lists by reference.
    private static bool SameJson<T>(T left, T right, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(left, type).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, type));

    private static string Describe(SceneCycleSelection s) => $"{s.Kind} '{s.Name}' (#{s.Index})";

    private static string Short(string? id) => id is null ? "(none)" : id.Length <= 12 ? id : id[..12];

    private static string Problems(IReadOnlyList<string>? problems)
    {
        if (problems is null || problems.Count == 0)
        {
            return "no reason given";
        }

        var text = string.Join("; ", problems.Take(4));
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    private static string Observed(SceneDto? scene) => scene switch
    {
        null => "no scene",
        { Package: { } pk } => $"package {Short(pk.ContentId)} binding {Short(pk.BindingId)} ({pk.PlaybackContract})",
        _ => "a scene without package provenance",
    };

    private sealed record Target(SceneCycleEntry Entry, SceneCycleSelection Selection);

    private sealed record Result(SceneCyclePhase Phase, string Detail);

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information, Message = "Scene cycle {Kind} '{Name}' (#{Index}, generation {Generation}): {Phase}. {Detail}")]
    private partial void LogOutcome(string kind, string name, int index, long generation, SceneCyclePhase phase, string detail);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning,
        Message = "Scene cycle {Kind} '{Name}' (#{Index}, generation {Generation}) has an unknown outcome ({Phase}); automatic apply is halted until a new explicit action. {Detail}")]
    private partial void LogUnknown(string kind, string name, int index, long generation, SceneCyclePhase phase, string detail);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Warning, Message = "Scene cycle list rejected: {Reason}")]
    private partial void LogListRejected(string reason);

    [LoggerMessage(EventId = 4204, Level = LogLevel.Warning, Message = "Scene cycle status subscriber failed")]
    private partial void LogSubscriberFailed(Exception exception);

    [LoggerMessage(EventId = 4205, Level = LogLevel.Warning, Message = "Scene cycle shut down with a request in flight: {Detail}")]
    private partial void LogClosedInFlight(string detail);
}
