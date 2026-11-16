using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Host.Services;

/// <summary>One discovered controller and what the engine is allowed to do with it.</summary>
public sealed class ControllerEntry(ControllerDescriptor descriptor, IDeviceAdapter adapter)
{
    public ControllerDescriptor Descriptor { get; } = descriptor;

    public IDeviceAdapter Adapter { get; } = adapter;

    public GuardVerdict Verdict { get; internal set; } = GuardVerdict.Clear;

    public DeviceWriter? Writer { get; internal set; }

    public bool IsActive => Writer is not null;

    internal bool OpenedOnce { get; set; }
    internal IPreciseSleeper? Sleeper { get; set; }
    internal long? LastRecoveryAttempt { get; set; }
    internal bool RecoveryClosing { get; set; }

    /// <summary>
    /// Owner of the bring-up session holding this controller (NATIVE design §6), or null. While set the writer (if any) stays
    /// paused and the status is BringUp.
    /// </summary>
    public string? BringupLease { get; internal set; }
}

/// <summary>
/// Creates adapters from the registered factories, discovers controllers, asks WriterGuard before opening anything,
/// and owns one DeviceWriter (thread + sleeper) per opened controller.
/// </summary>
public sealed partial class AdapterHost(
    IEnumerable<IDeviceAdapterFactory> factories,
    IServiceProvider services,
    IWriterGuard guard,
    FrameBus bus,
    Func<IPreciseSleeper> sleeperFactory,
    ILogger<AdapterHost> logger,
    UnifiedRgbOptions? options = null,
    PeripheralConfigurationGate? configurationGate = null) : IAsyncDisposable
{
    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;

    private readonly List<IDeviceAdapter> _adapters = [];
    private readonly Dictionary<IDeviceAdapterFactory, IDeviceAdapter> _adapterFactories = new(ReferenceEqualityComparer.Instance);
    // Active discovery skipped by the guard (or not yet successful) must run once after unblock, even with no entries yet.
    private readonly HashSet<IDeviceAdapter> _deferredDiscovery = new(ReferenceEqualityComparer.Instance);
    // Only typed, pre-write mouse-startup timeouts qualify. Rechecking one receiver must not retry unrelated terminal opens.
    private readonly HashSet<ControllerId> _mouseStartupRetries = [];
    private readonly List<ControllerEntry> _entries = [];
    private const int RecoveryFailureThreshold = 3;
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly ConcurrentQueue<ControllerId> _arrivals = new();
    private readonly ConcurrentQueue<ControllerId> _removals = new();
    private int _rescanRequested;
    private bool _stopping;

    /// <summary>Optional test seam overriding the per-writer shutdown timeout in <see cref="DeviceWriter"/>.</summary>
    internal TimeSpan? WriterStopTimeout { get; init; }

    /// <summary>Raised on a writer thread after each completed loop upload (UploadedLoop controllers).</summary>
    public event Action<ControllerId, PendingLoop, LoopUploadStats?>? LoopUploaded;

    /// <summary>Raised once per adapter after it is created, before its first discovery (Lian Li mounting overrides are seeded here).</summary>
    public event Action<IDeviceAdapter>? AdapterCreated;

    internal bool RogAcquisitionAllowed => configurationGate?.RogAcquisitionAllowed ?? true;

    /// <summary>Controllers whose device re-appeared (hotplug Arrived) since the last call; the engine re-publishes their loops.</summary>
    public IReadOnlyList<ControllerId> TakeArrivals()
    {
        var list = new List<ControllerId>();
        while (_arrivals.TryDequeue(out var id))
        {
            list.Add(id);
        }

        return list;
    }

    public IReadOnlyList<ControllerEntry> Controllers
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Set by adapter hotplug events; the guard monitor runs <see cref="RescanAsync"/> when it sees it.</summary>
    public bool RescanRequested => Volatile.Read(ref _rescanRequested) != 0;

    public async Task StartAsync(CancellationToken ct)
    {
        if (options?.StartupRecoveryNotice is not null) return; // no factory/driver/adapter construction in repair mode
        foreach (var factory in factories)
        {
            if (factory.Class == DeviceClass.AsusRog && !RogAcquisitionAllowed) continue;
            AddAdapter(factory);
        }

        await RescanAsync(ct).ConfigureAwait(false);
    }

    private IDeviceAdapter AddAdapter(IDeviceAdapterFactory factory)
    {
        var adapter = factory.Create(services);
        adapter.Hotplug += OnHotplug;
        lock (_gate)
        {
            _adapters.Add(adapter);
            _adapterFactories.Add(factory, adapter);
            if (adapter is IActiveDiscovery) _deferredDiscovery.Add(adapter);
        }
        AdapterCreated?.Invoke(adapter);
        return adapter;
    }

    /// <summary>
    /// ARCHITECTURE §7.1 for every adapter: Takeover (if enabled) → WriterGuard → discovery → open what is Clear. Already
    /// known controllers keep their writers; newly found ones are added. Returns true when anything changed.
    /// </summary>
    public Task<bool> RescanAsync(CancellationToken ct) => configurationGate is null
        ? RescanCoreAsync(ct)
        : configurationGate.RunExclusiveAsync(RescanCoreAsync, ct);

    private async Task<bool> RescanCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _rescanRequested, 0);
        var changed = false;
        var removed = DrainRemovals();
        if (removed.Count > 0)
        {
            ControllerEntry[] toDetach;
            lock (_gate)
            {
                toDetach = _entries.Where(e => removed.Contains(e.Descriptor.Id) && e.IsActive && e.BringupLease is null).ToArray();
            }

            foreach (var entry in toDetach)
            {
                if (Volatile.Read(ref _stopping)) break;
                await DetachAsync(entry, GuardVerdict.Clear).ConfigureAwait(false);
                changed = true;
            }
        }

        foreach (var adapter in Adapters)
        {
            if (Volatile.Read(ref _stopping)) break;
            var verdict = await VerdictAsync(adapter.Class, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopping)) break;
            if (adapter is IActiveDiscovery && verdict.State != GuardState.Clear)
            {
                LogDiscoveryBlocked(adapter.Vendor, verdict.Writer ?? "?");
                changed |= await ApplyAsync(adapter, verdict, retryOpen: false, ct).ConfigureAwait(false);
                continue;
            }

            changed |= await DiscoverAndApplyAsync(adapter, ct, removed: removed).ConfigureAwait(false);
        }

        return changed;
    }

    private HashSet<ControllerId> DrainRemovals()
    {
        HashSet<ControllerId>? set = null;
        while (_removals.TryDequeue(out var id))
        {
            (set ??= []).Add(id);
        }

        return set ?? [];
    }

    /// <summary>
    /// The 5 s guard check (ARCHITECTURE §7.5): a reappearing vendor pauses our writers unless Takeover can stop it. On unblock,
    /// deferred active discovery runs before opening controllers; successful adapters are not rediscovered by guard checks.
    /// </summary>
    public Task<bool> RecheckAsync(CancellationToken ct) => configurationGate is null
        ? RecheckCoreAsync(ct)
        : configurationGate.RunExclusiveAsync(RecheckCoreAsync, ct);

    private async Task<bool> RecheckCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var changed = false;
        foreach (var adapter in Adapters)
        {
            if (Volatile.Read(ref _stopping)) break;
            var verdict = await VerdictAsync(adapter.Class, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopping)) break;
            bool deferred;
            HashSet<ControllerId> mouseRetries;
            lock (_gate)
            {
                deferred = _deferredDiscovery.Contains(adapter);
                mouseRetries = _entries.Where(e => ReferenceEquals(e.Adapter, adapter) && _mouseStartupRetries.Contains(e.Descriptor.Id))
                    .Select(e => e.Descriptor.Id).ToHashSet();
            }

            changed |= deferred && verdict.State == GuardState.Clear
                ? await DiscoverAndApplyAsync(adapter, ct).ConfigureAwait(false)
                : mouseRetries.Count > 0 && verdict.State == GuardState.Clear
                    ? await DiscoverAndApplyAsync(adapter, ct, retryOnly: mouseRetries).ConfigureAwait(false)
                : await ApplyAsync(adapter, verdict, retryOpen: false, ct).ConfigureAwait(false);
        }

        return changed;
    }

    public async Task ReleaseForPeripheralConfigurationAsync(CancellationToken ct)
    {
        var errors = new List<Exception>();
        var entries = Controllers.Where(e => e.Descriptor.Class == DeviceClass.AsusRog).ToArray();
        foreach (var entry in entries)
        {
            try
            {
                if (entry.Writer is { } writer)
                {
                    await writer.QuiesceAsync().ConfigureAwait(false);
                    await writer.StopAsync().ConfigureAwait(false);
                    bus.Unregister(entry.Descriptor.Id);
                    writer.Dispose();
                    entry.Writer = null;
                    ReleaseSleeper(entry);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        }

        if (errors.Count != 0) throw new AggregateException("ROG writers did not become quiescent; adapter disposal was refused.", errors);
        var adapters = Adapters.Where(a => a.Class == DeviceClass.AsusRog).ToArray();
        foreach (var adapter in adapters)
        {
            try
            {
                if (adapter is UnifiedRgb.Devices.AsusRog.IRogPeripheralConfigurationLifetime configuration)
                    await configuration.ReleaseForConfigurationAsync(ct).ConfigureAwait(false);
                else if (adapter is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    (adapter as IDisposable)?.Dispose();
                adapter.Hotplug -= OnHotplug;
                lock (_gate)
                {
                    _adapters.Remove(adapter);
                    foreach (var pair in _adapterFactories.Where(p => ReferenceEquals(p.Value, adapter)).ToArray())
                        _adapterFactories.Remove(pair.Key);
                    _deferredDiscovery.Remove(adapter);
                    foreach (var entry in _entries.Where(e => ReferenceEquals(e.Adapter, adapter))) _mouseStartupRetries.Remove(entry.Descriptor.Id);
                    _entries.RemoveAll(e => ReferenceEquals(e.Adapter, adapter));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException("One or more ROG adapter resources did not release cleanly; failed participants remain retained for explicit recovery.", errors);
    }

    public async Task ResumeAfterPeripheralConfigurationAsync(CancellationToken ct)
    {
        var added = new List<IDeviceAdapter>();
        var errors = new List<Exception>();
        IDeviceAdapterFactory[] pending;
        lock (_gate) pending = factories.Where(f => f.Class == DeviceClass.AsusRog && !_adapterFactories.ContainsKey(f)).ToArray();
        foreach (var factory in pending)
        {
            try { added.Add(AddAdapter(factory)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        }
        foreach (var adapter in added)
        {
            try { await DiscoverAndApplyAsync(adapter, ct, throwOnError: true).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex); }
        }
        HashSet<ControllerId> deferredSleepable;
        lock (_gate) deferredSleepable = [.. _mouseStartupRetries];
        foreach (var entry in Controllers.Where(e => added.Contains(e.Adapter, ReferenceEqualityComparer.Instance)
                     && e.Writer is null && e.Adapter is INativeBringup native && native.IsControlVerified(e.Descriptor.Id)))
        {
            if (deferredSleepable.Contains(entry.Descriptor.Id))
            {
                LogMouseResumeDeferred(entry.Descriptor.Id.Value);
                continue;
            }

            errors.Add(new IOException($"Fresh ROG resource {entry.Descriptor.Id.Value} is present but could not open: {entry.Verdict.Writer ?? "offline, asleep or busy"}."));
        }
        if (errors.Count != 0) throw new AggregateException("Fresh ROG adapter construction, discovery or current-settings open was incomplete.", errors);
    }

    /// <summary>
    /// Post-sleep / S0ix resume recovery: closes and reopens Direct-mode adapters (so dropped Direct mode, stale GPU/HID
    /// handles and static-frame dedup caches are reset) and performs a full rescan while preserving current Host configuration.
    /// </summary>
    internal Task<bool> RecoverAfterPowerResumeAsync(CancellationToken ct) => configurationGate is null
        ? RecoverAfterPowerResumeCoreAsync(ct)
        : configurationGate.RunExclusiveAsync(RecoverAfterPowerResumeCoreAsync, ct);

    private async Task<bool> RecoverAfterPowerResumeCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopping)) return false;
        var changed = false;
        if (options?.Bringup.HoldBlackOnRelease != true)
        {
            ControllerEntry[] directEntries;
            lock (_gate)
            {
                directEntries = _entries
                    .Where(e => e.IsActive && e.BringupLease is null
                        && e.Descriptor.Capabilities.Delivery == Delivery.Streamed
                        && e.Descriptor.Class != DeviceClass.AsusRog)
                    .ToArray();
            }

            foreach (var entry in directEntries)
            {
                if (Volatile.Read(ref _stopping)) break;
                await DetachAsync(entry, GuardVerdict.Clear).ConfigureAwait(false);
                changed = true;
            }
        }

        changed |= await RescanCoreAsync(ct).ConfigureAwait(false);
        return changed;
    }

    internal void ReserveBringup(ControllerEntry entry, string owner)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (entry.RecoveryClosing || Volatile.Read(ref _stopping))
                throw new InvalidOperationException($"{entry.Descriptor.Id}: controller recovery or Host shutdown owns the connection.");
            entry.BringupLease = owner;
        }
    }

    public void PauseForBringup(ControllerEntry entry, string owner)
    {
        ReserveBringup(entry, owner);
        entry.Writer?.Quiesce();
    }

    /// <summary>
    /// Ends the bring-up hold. The session's release ended the device's native open state (GPU: 0x06 &lt;- 1), so a writer
    /// resumes only after its adapter is opened again (GPU: the enter is re-sent) and the class verdict is Clear; a class that
    /// is not Clear, or an open that fails, detaches the writer and the normal scan opens it later. When
    /// <paramref name="relinquish"/> is true (Host stop, takeover switched off) nothing is reopened: the writer is detached and
    /// the entry is Blocked(<paramref name="reason"/>). Never throws; failures are logged and leave the controller closed.
    /// </summary>
    public async Task ResumeAfterBringupAsync(ControllerEntry entry, bool relinquish, string reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = entry.Descriptor.Id;
        if (relinquish || Volatile.Read(ref _stopping))
        {
            await DetachAsync(entry, GuardVerdict.Blocked(reason)).ConfigureAwait(false);
        }
        else if (entry.Writer is not null)
        {
            var verdict = guard.Check(entry.Descriptor.Class);
            if (verdict.State != GuardState.Clear)
            {
                await DetachAsync(entry, verdict).ConfigureAwait(false); // opened afresh once the class is Clear again
            }
            else
            {
                try
                {
                    await entry.Adapter.OpenAsync(id, CancellationToken.None).ConfigureAwait(false);
                    entry.Verdict = GuardVerdict.Clear;
                }
                catch (DeviceBlockedException ex)
                {
                    LogOpenBlocked(id.Value, ex.Reason);
                    await DetachAsync(entry, GuardVerdict.Blocked(ex.Reason)).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // a controller that fails to reopen is listed Offline until the next rescan, never fatal
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogOpenFailed(id.Value, ex);
                    await DetachAsync(entry, GuardVerdict.Clear).ConfigureAwait(false);
                }
            }
        }

        lock (_gate)
        {
            entry.BringupLease = null;
            if (entry.Writer is { } w)
            {
                w.Paused = entry.Verdict.State != GuardState.Clear;
            }
        }
    }

    /// <summary>
    /// Takeover switched off for <paramref name="cls"/>: every native (INativeBringup) writer of the class is quiesced, stopped
    /// and closed before the caller restores any vendor service, and stays Blocked(<paramref name="reason"/>) while takeover is
    /// off (OpenAsync refuses native writers then). Other classes and the Lian Li adapter are unchanged. Returns the closed count.
    /// </summary>
    public async Task<int> CloseClassAsync(DeviceClass cls, string reason)
    {
        ControllerEntry[] those;
        lock (_gate)
        {
            those = _entries.Where(e => e.Descriptor.Class == cls && e.Adapter is INativeBringup && e.Writer is not null).ToArray();
        }

        foreach (var e in those)
        {
            await DetachAsync(e, GuardVerdict.Blocked(reason)).ConfigureAwait(false);
        }

        return those.Length;
    }

    /// <summary>Host stop has begun: no writer is opened or reopened from here on.</summary>
    public void BeginStop() => Volatile.Write(ref _stopping, true);

    /// <summary>Close order on stop (overnight addendum §4): GPU, then ENE, then Aura, then the rest in discovery order.</summary>
    public static int StopOrder(DeviceClass cls) => cls switch
    {
        DeviceClass.MsiGpu => 0,
        DeviceClass.EneDram => 1,
        DeviceClass.AsusAura => 2,
        _ => 3,
    };

    private readonly object _stopGate = new();
    private Task? _stopTask;
    public Task StopAsync()
    {
        lock (_stopGate) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        BeginStop();
        ControllerEntry[] entries;
        lock (_gate)
        {
            entries = _entries.OrderBy(e => StopOrder(e.Descriptor.Class)).ToArray();
            _entries.Clear();
        }

        // Bound all writers together. If any writer fails/times out, isolate its adapter (and all sibling entries sharing
        // that physical adapter) from CloseAsync/DisposeAsync, and retain the failure so foreign writers are not restored.
        var activeEntries = entries.Where(e => e.Writer is not null).ToArray();
        var stopTasks = activeEntries.Select(e => e.Writer!.StopAsync()).ToArray();
        try
        {
            await Task.WhenAll(stopTasks).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // per-entry failures are inspected and rethrown after healthy adapters clean up
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        var stopFailures = new List<Exception>();
        var uncertainAdapters = new HashSet<IDeviceAdapter>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < activeEntries.Length; i++)
        {
            var t = stopTasks[i];
            if (t.IsFaulted)
            {
                var ex = t.Exception?.InnerExceptions.Count == 1
                    ? t.Exception.InnerExceptions[0]
                    : (Exception?)t.Exception ?? new InvalidOperationException($"Writer {activeEntries[i].Descriptor.Id} stop faulted.");
                stopFailures.Add(ex);
                uncertainAdapters.Add(activeEntries[i].Adapter);
                LogWriterStopFailed(activeEntries[i].Descriptor.Id.Value, ex);
            }
            else if (t.IsCanceled)
            {
                var ex = new OperationCanceledException($"Writer {activeEntries[i].Descriptor.Id} stop was cancelled.");
                stopFailures.Add(ex);
                uncertainAdapters.Add(activeEntries[i].Adapter);
                LogWriterStopFailed(activeEntries[i].Descriptor.Id.Value, ex);
            }
        }

        foreach (var e in activeEntries)
        {
            bus.Unregister(e.Descriptor.Id);
            if (uncertainAdapters.Contains(e.Adapter))
            {
                continue;
            }

            try
            {
                await e.Adapter.CloseAsync(e.Descriptor.Id).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // best-effort release on shutdown
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCloseFailed(e.Descriptor.Id.Value, ex);
            }

            e.Writer?.Dispose();
            e.Writer = null;
            if (stopFailures.Count == 0)
            {
                ReleaseSleeper(e);
            }
        }

        IDeviceAdapter[] adapters;
        lock (_gate)
        {
            adapters = _adapters.OrderBy(a => StopOrder(a.Class)).ToArray();
        }

        foreach (var a in adapters)
        {
            a.Hotplug -= OnHotplug;
        }

        foreach (var a in adapters.OfType<IAsyncDisposable>())
        {
            if (uncertainAdapters.Contains((IDeviceAdapter)a))
            {
                continue;
            }

            try
            {
                await a.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // best-effort release on shutdown (e.g. stopping the supervised OpenRGB)
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCloseFailed(((IDeviceAdapter)a).Vendor, ex);
            }
        }

        lock (_gate)
        {
            _adapters.Clear();
            _adapterFactories.Clear();
            _deferredDiscovery.Clear();
        }

        if (stopFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(stopFailures[0]).Throw();
        }

        if (stopFailures.Count > 1)
        {
            throw new AggregateException("One or more device writers did not stop cleanly; uncertain adapters were left unclosed.", stopFailures);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private IDeviceAdapter[] Adapters
    {
        get
        {
            lock (_gate)
            {
                return _adapters.ToArray();
            }
        }
    }

    private void OnHotplug(HotplugEvent e)
    {
        if (e.Kind == HotplugKind.Arrived)
        {
            _arrivals.Enqueue(e.Id);
        }
        else if (e.Kind == HotplugKind.Removed)
        {
            _removals.Enqueue(e.Id);
        }

        RequestRescan();
    }

    internal void RequestRescan() => Interlocked.Exchange(ref _rescanRequested, 1);

    /// <summary>Guard verdict; when blocked and the class has Takeover on, stop the vendor services and check again.</summary>
    private async Task<GuardVerdict> VerdictAsync(DeviceClass cls, CancellationToken ct)
    {
        var verdict = guard.Check(cls);
        if (verdict.State == GuardState.Clear || !guard.IsTakeoverEnabled(cls))
        {
            return verdict;
        }

        var r = await guard.TakeoverAsync(cls, ct).ConfigureAwait(false);
        if (r.StoppedServices.Count > 0 || r.Error is not null)
        {
            LogTakeover(cls.ToString(), string.Join(", ", r.StoppedServices), string.Join(", ", r.ProcessesToClose), r.Error ?? "none");
        }

        return guard.Check(cls);
    }

    /// <summary>Discovery admitted by the guard; only a completed, uncancelled discovery consumes a deferred retry.</summary>
    private async Task<bool> DiscoverAndApplyAsync(IDeviceAdapter adapter, CancellationToken ct, bool throwOnError = false,
        IReadOnlySet<ControllerId>? retryOnly = null, IReadOnlySet<ControllerId>? removed = null)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopping)) return false;
        IReadOnlyList<ControllerDescriptor> found;
        try
        {
            found = await adapter.DiscoverAsync(ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // one failing vendor must not stop the others; deferred discovery stays eligible to retry
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogDiscoveryFailed(adapter.Vendor, ex);
            if (throwOnError) throw;
            return false;
        }

        ct.ThrowIfCancellationRequested();
        var changed = false;
        var foundIds = new HashSet<ControllerId>(found.Select(d => d.Id));
        lock (_gate)
        {
            if (Volatile.Read(ref _stopping)) return false;
            _deferredDiscovery.Remove(adapter); // an empty result is also a successful discovery
            foreach (var d in found)
            {
                if (_entries.Any(e => e.Descriptor.Id == d.Id)) continue;
                _entries.Add(new ControllerEntry(d, adapter) { Verdict = GuardVerdict.Blocked("not yet checked") });
                changed = true;
            }
        }

        HashSet<ControllerId>? skipReopen = null;
        if (removed is { Count: > 0 })
        {
            foreach (var id in removed)
            {
                if (!foundIds.Contains(id)) (skipReopen ??= []).Add(id);
            }
        }

        // Discovery may have awaited while another writer appeared: recheck before any controller open.
        return await ApplyAsync(adapter, guard.Check(adapter.Class), retryOpen: true, ct, retryOnly, skipReopen).ConfigureAwait(false) || changed;
    }

    private async Task<bool> ApplyAsync(IDeviceAdapter adapter, GuardVerdict verdict, bool retryOpen, CancellationToken ct,
        IReadOnlySet<ControllerId>? retryOnly = null, HashSet<ControllerId>? skipReopen = null)
    {
        ControllerEntry[] mine;
        lock (_gate)
        {
            mine = _entries.Where(e => ReferenceEquals(e.Adapter, adapter)).ToArray();
        }

        var changed = false;
        foreach (var e in mine)
        {
            var before = (e.Verdict, e.IsActive, e.Writer?.Paused);
            var reopen = false;
            var recoveryDue = e.LastRecoveryAttempt is not { } last || Stopwatch.GetElapsedTime(last) >= RecoveryInterval;
            if (e.Descriptor.Class != DeviceClass.AsusRog &&
                verdict.State == GuardState.Clear && e.Writer is { ConsecutiveIoFailures: >= RecoveryFailureThreshold } &&
                recoveryDue && TryBeginIoRecovery(e))
            {
                // Keep the old writer owned and paused until both its thread and adapter close are confirmed.
                // Scene output is regenerated through the normal rescan path from the current scene.
                try { await DetachAsync(e, verdict, requireClose: true).ConfigureAwait(false); }
#pragma warning disable CA1031 // failed close/join keeps this writer paused and owned; other devices can recover
                catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
                {
                    LogRecoveryFailed(e.Descriptor.Id.Value, ex);
                    changed = true;
                    continue;
                }
                reopen = true;
                changed = true;
            }
            if (e.RecoveryClosing && !reopen) continue;
            if (e.Writer is { } w)
            {
                var contended = verdict.State != GuardState.Clear;
                if (contended && !w.Paused)
                {
                    LogContended(e.Descriptor.Id.Value, verdict.Writer ?? "?");
                }

                w.Paused = contended || e.BringupLease is not null;
                e.Verdict = contended ? GuardVerdict.Contended(verdict.Writer ?? "?") : GuardVerdict.Clear;
            }
            else if (verdict.State == GuardState.Clear)
            {
                if (skipReopen is not null && skipReopen.Contains(e.Descriptor.Id)) continue;
                if (retryOnly is not null && !retryOnly.Contains(e.Descriptor.Id)) continue;
                var previous = e.Verdict;
                e.Verdict = GuardVerdict.Clear;
                if (e.LastRecoveryAttempt.HasValue && recoveryDue && e.BringupLease is null)
                {
                    e.LastRecoveryAttempt = Stopwatch.GetTimestamp();
                    reopen = true;
                }
                if (previous.State != GuardState.Clear || retryOpen || reopen)
                {
                    try { await OpenAsync(e, previous, ct).ConfigureAwait(false); }
                    finally
                    {
                        if (e.RecoveryClosing)
                            lock (_gate) e.RecoveryClosing = false;
                    }
                    if (e.IsActive && e.LastRecoveryAttempt.HasValue) _arrivals.Enqueue(e.Descriptor.Id);
                }
            }
            else
            {
                if (e.Verdict != verdict)
                {
                    LogBlocked(e.Descriptor.Id.Value, verdict.Writer ?? "?");
                }

                e.Verdict = verdict;
            }

            changed |= before != (e.Verdict, e.IsActive, e.Writer?.Paused);
        }

        return changed;
    }

    private bool TryBeginIoRecovery(ControllerEntry entry)
    {
        lock (_gate)
        {
            if (entry.BringupLease is not null || Volatile.Read(ref _stopping)) return false;
            entry.LastRecoveryAttempt = Stopwatch.GetTimestamp();
            entry.RecoveryClosing = true;
            return true;
        }
    }

    private async Task OpenAsync(ControllerEntry entry, GuardVerdict previous, CancellationToken ct)
    {
        var id = entry.Descriptor.Id;
        if (Volatile.Read(ref _stopping))
        {
            entry.Verdict = previous; // Host stop: nothing opens; the entry keeps the verdict it had
            return;
        }

        if (entry.Descriptor.Class == DeviceClass.AsusRog && !RogAcquisitionAllowed && configurationGate?.FreshResumeAllowed != true)
        {
            entry.Verdict = GuardVerdict.Blocked("peripheral configuration handoff");
            return;
        }

        if (entry.BringupLease is not null)
        {
            entry.Verdict = previous; // Codex review: a leased controller's hardware belongs to its session; no writer opens under it
            return;
        }

        if (entry.Adapter is INativeBringup && !guard.IsTakeoverEnabled(entry.Descriptor.Class))
        {
            // Codex review: a native writer runs only while its class is taken over; switching takeover off closes it
            entry.Verdict = GuardVerdict.Blocked($"takeover off for {entry.Descriptor.Class}");
            if (previous != entry.Verdict)
            {
                LogOpenBlocked(id.Value, entry.Verdict.Writer!);
            }

            return;
        }

        if (entry.Adapter is INativeBringup native && !native.IsControlVerified(id))
        {
            // NATIVE design §2/§6: discoverable, openable only by a leased bring-up session, never a scene writer
            entry.Verdict = GuardVerdict.Blocked(native.UnverifiedReason(id));
            if (previous != entry.Verdict)
            {
                LogOpenBlocked(id.Value, entry.Verdict.Writer!);
            }

            return;
        }

        try
        {
            await entry.Adapter.OpenAsync(id, ct).ConfigureAwait(false);
        }
        catch (UnifiedRgb.Devices.AsusRog.RogMouseStartupNotReadyException ex) when (!entry.OpenedOnce
            && entry.Descriptor.Class == DeviceClass.AsusRog && entry.Descriptor.DeviceType == DeviceType.Mouse
            && string.Equals(ex.ControllerId, id.Value, StringComparison.Ordinal))
        {
            lock (_gate) _mouseStartupRetries.Add(id);
            LogOpenFailed(id.Value, ex);
            return;
        }
        catch (DeviceBlockedException ex)
        {
            lock (_gate) _mouseStartupRetries.Remove(id);
            // a standing block (e.g. "yuz.dll missing"): shown as Blocked(reason), logged when it first appears or changes
            entry.Verdict = GuardVerdict.Blocked(ex.Reason);
            if (previous != entry.Verdict)
            {
                LogOpenBlocked(id.Value, ex.Reason);
            }

            return;
        }
#pragma warning disable CA1031 // a controller that fails to open is listed Offline, never fatal
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            lock (_gate) _mouseStartupRetries.Remove(id);
            LogOpenFailed(id.Value, ex);
            return;
        }

        entry.OpenedOnce = true;
        lock (_gate) _mouseStartupRetries.Remove(id);

        var sleeper = sleeperFactory();
        entry.Sleeper = sleeper;

        var mailbox = bus.Register(entry.Descriptor);
        var writer = new DeviceWriter(mailbox, entry.Adapter, sleeper, OnWriteError, (c, l, s) => LoopUploaded?.Invoke(c, l, s), WriterStopTimeout);
        entry.Writer = writer;
        writer.Start();
        LogOpened(id.Value, entry.Descriptor.Name, writer.MaxFps);
    }

    /// <summary>
    /// Takes the writer off <paramref name="entry"/> (waits out its in-flight write, stops the thread, unregisters the mailbox),
    /// closes the controller once (idempotent after a session release: the GPU sends nothing more) and sets the verdict.
    /// </summary>
    private async Task DetachAsync(ControllerEntry entry, GuardVerdict verdict, bool requireClose = false)
    {
        DeviceWriter? writer;
        lock (_gate)
        {
            writer = entry.Writer;
            entry.Verdict = verdict;
        }

        if (writer is null)
        {
            return;
        }

        await writer.QuiesceAsync().ConfigureAwait(false);
        await writer.StopAsync().ConfigureAwait(false);
        try
        {
            await entry.Adapter.CloseAsync(entry.Descriptor.Id).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // best-effort close; the controller is listed with its verdict either way
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCloseFailed(entry.Descriptor.Id.Value, ex);
            if (requireClose) throw;
        }

        bus.Unregister(entry.Descriptor.Id);
        entry.Writer = null;
        if (!requireClose) entry.RecoveryClosing = false;
        writer.Dispose();
        ReleaseSleeper(entry);
    }

    private static void ReleaseSleeper(ControllerEntry entry)
    {
        (entry.Sleeper as IDisposable)?.Dispose();
        entry.Sleeper = null;
    }

    private void OnWriteError(ControllerId id, Exception ex)
    {
        ControllerEntry? entry;
        lock (_gate) entry = _entries.Find(c => c.Descriptor.Id == id);
        var writer = entry?.Writer;
        if (entry is not null && entry.Descriptor.Class != DeviceClass.AsusRog &&
            writer?.ConsecutiveIoFailures == RecoveryFailureThreshold)
            RequestRescan();
        var errors = writer?.GetStats().Errors ?? 1;
        if (errors == 1 || errors % 600 == 0)
        {
            LogWriteError(id.Value, errors, ex);
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Discovery failed for adapter {Vendor}")]
    private partial void LogDiscoveryFailed(string vendor, Exception ex);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Warning, Message = "Automatic recovery could not close controller {Id}; writer remains paused")]
    private partial void LogRecoveryFailed(string id, Exception ex);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Controller {Id} blocked by {Writer}; not opened")]
    private partial void LogBlocked(string id, string writer);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Open failed for {Id}")]
    private partial void LogOpenFailed(string id, Exception ex);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Opened {Id} ({Name}), writer at {MaxFps} fps")]
    private partial void LogOpened(string id, string name, int maxFps);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Warning, Message = "Write error on {Id} (total {Errors})")]
    private partial void LogWriteError(string id, long errors, Exception ex);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Warning, Message = "Adapter {Vendor} not started: blocked by {Writer} (its discovery touches hardware)")]
    private partial void LogDiscoveryBlocked(string vendor, string writer);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning, Message = "Controller {Id} contended by {Writer}; writer paused")]
    private partial void LogContended(string id, string writer);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Warning, Message = "Takeover {Class}: stopped [{Stopped}], close manually [{Processes}], error {Error}")]
    private partial void LogTakeover(string @class, string stopped, string processes, string error);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "Controller {Id} blocked: {Reason}")]
    private partial void LogOpenBlocked(string id, string reason);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Warning, Message = "Close failed for {Id}")]
    private partial void LogCloseFailed(string id, Exception ex);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Error, Message = "Writer {Id} did not stop cleanly; leaving its adapter unclosed")]
    private partial void LogWriterStopFailed(string id, Exception ex);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information, Message = "Sleepable ROG peripheral {Id} deferred for wake recovery after configuration resume")]
    private partial void LogMouseResumeDeferred(string id);
}
