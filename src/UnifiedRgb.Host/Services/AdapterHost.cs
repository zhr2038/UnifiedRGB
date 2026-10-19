using System.Collections.Concurrent;
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
    ILogger<AdapterHost> logger) : IAsyncDisposable
{
    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;

    private readonly List<IDeviceAdapter> _adapters = [];
    // Active discovery skipped by the guard (or not yet successful) must run once after unblock, even with no entries yet.
    private readonly HashSet<IDeviceAdapter> _deferredDiscovery = new(ReferenceEqualityComparer.Instance);
    private readonly List<ControllerEntry> _entries = [];
    private readonly List<IPreciseSleeper> _sleepers = [];
    private readonly object _gate = new();
    private readonly ConcurrentQueue<ControllerId> _arrivals = new();
    private int _rescanRequested;
    private bool _stopping;

    /// <summary>Raised on a writer thread after each completed loop upload (UploadedLoop controllers).</summary>
    public event Action<ControllerId, PendingLoop, LoopUploadStats?>? LoopUploaded;

    /// <summary>Raised once per adapter after it is created, before its first discovery (Lian Li mounting overrides are seeded here).</summary>
    public event Action<IDeviceAdapter>? AdapterCreated;

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
        foreach (var factory in factories)
        {
            var adapter = factory.Create(services);
            adapter.Hotplug += OnHotplug;
            lock (_gate)
            {
                _adapters.Add(adapter);
                if (adapter is IActiveDiscovery)
                {
                    _deferredDiscovery.Add(adapter);
                }
            }

            AdapterCreated?.Invoke(adapter);
        }

        await RescanAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ARCHITECTURE §7.1 for every adapter: Takeover (if enabled) → WriterGuard → discovery → open what is Clear. Already
    /// known controllers keep their writers; newly found ones are added. Returns true when anything changed.
    /// </summary>
    public async Task<bool> RescanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _rescanRequested, 0);
        var changed = false;
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

            changed |= await DiscoverAndApplyAsync(adapter, ct).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>
    /// The 5 s guard check (ARCHITECTURE §7.5): a reappearing vendor pauses our writers unless Takeover can stop it. On unblock,
    /// deferred active discovery runs before opening controllers; successful adapters are not rediscovered by guard checks.
    /// </summary>
    public async Task<bool> RecheckAsync(CancellationToken ct)
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
            lock (_gate)
            {
                deferred = _deferredDiscovery.Contains(adapter);
            }

            changed |= deferred && verdict.State == GuardState.Clear
                ? await DiscoverAndApplyAsync(adapter, ct).ConfigureAwait(false)
                : await ApplyAsync(adapter, verdict, retryOpen: false, ct).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>
    /// A bring-up session took <paramref name="entry"/>: its writer (a verified native controller has one) is paused and the
    /// write or upload already in flight is waited out, so no scene frame reaches the device until
    /// <see cref="ResumeAfterBringupAsync"/> and no marker interleaves with one (Codex review, writer quiescence).
    /// </summary>
    public void PauseForBringup(ControllerEntry entry, string owner)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DeviceWriter? writer;
        lock (_gate)
        {
            entry.BringupLease = owner;
            writer = entry.Writer;
            if (writer is not null)
            {
                writer.Paused = true;
            }
        }

        writer?.Quiesce();
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

    public async Task StopAsync()
    {
        BeginStop();
        ControllerEntry[] entries;
        lock (_gate)
        {
            entries = _entries.OrderBy(e => StopOrder(e.Descriptor.Class)).ToArray();
            _entries.Clear();
        }

        foreach (var e in entries)
        {
            e.Writer?.Stop();
        }

        foreach (var e in entries.Where(e => e.IsActive))
        {
            bus.Unregister(e.Descriptor.Id);
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
            foreach (var s in _sleepers)
            {
                (s as IDisposable)?.Dispose();
            }

            _sleepers.Clear();
            _adapters.Clear();
            _deferredDiscovery.Clear();
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

        Interlocked.Exchange(ref _rescanRequested, 1);
    }

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
    private async Task<bool> DiscoverAndApplyAsync(IDeviceAdapter adapter, CancellationToken ct)
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
            return false;
        }

        ct.ThrowIfCancellationRequested();
        var changed = false;
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

        // Discovery may have awaited while another writer appeared: recheck before any controller open.
        return await ApplyAsync(adapter, guard.Check(adapter.Class), retryOpen: true, ct).ConfigureAwait(false) || changed;
    }

    private async Task<bool> ApplyAsync(IDeviceAdapter adapter, GuardVerdict verdict, bool retryOpen, CancellationToken ct)
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
                var previous = e.Verdict;
                e.Verdict = GuardVerdict.Clear;
                if (previous.State != GuardState.Clear || retryOpen)
                {
                    await OpenAsync(e, previous, ct).ConfigureAwait(false); // a failed open stays Offline until the next rescan
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

    private async Task OpenAsync(ControllerEntry entry, GuardVerdict previous, CancellationToken ct)
    {
        var id = entry.Descriptor.Id;
        if (Volatile.Read(ref _stopping))
        {
            entry.Verdict = previous; // Host stop: nothing opens; the entry keeps the verdict it had
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
        catch (DeviceBlockedException ex)
        {
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
            LogOpenFailed(id.Value, ex);
            return;
        }

        var sleeper = sleeperFactory();
        lock (_gate)
        {
            _sleepers.Add(sleeper);
        }

        var mailbox = bus.Register(entry.Descriptor);
        var writer = new DeviceWriter(mailbox, entry.Adapter, sleeper, OnWriteError, (c, l, s) => LoopUploaded?.Invoke(c, l, s));
        entry.Writer = writer;
        writer.Start();
        LogOpened(id.Value, entry.Descriptor.Name, writer.MaxFps);
    }

    /// <summary>
    /// Takes the writer off <paramref name="entry"/> (waits out its in-flight write, stops the thread, unregisters the mailbox),
    /// closes the controller once (idempotent after a session release: the GPU sends nothing more) and sets the verdict.
    /// </summary>
    private async Task DetachAsync(ControllerEntry entry, GuardVerdict verdict)
    {
        DeviceWriter? writer;
        lock (_gate)
        {
            writer = entry.Writer;
            entry.Writer = null;
            entry.Verdict = verdict;
        }

        if (writer is null)
        {
            return;
        }

        writer.Quiesce();
        writer.Stop();
        bus.Unregister(entry.Descriptor.Id);
        try
        {
            await entry.Adapter.CloseAsync(entry.Descriptor.Id).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // best-effort close; the controller is listed with its verdict either way
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCloseFailed(entry.Descriptor.Id.Value, ex);
        }

        writer.Dispose();
    }

    private void OnWriteError(ControllerId id, Exception ex)
    {
        var errors = Controllers.FirstOrDefault(c => c.Descriptor.Id == id)?.Writer?.GetStats().Errors ?? 1;
        if (errors == 1 || errors % 600 == 0)
        {
            LogWriteError(id.Value, errors, ex);
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Discovery failed for adapter {Vendor}")]
    private partial void LogDiscoveryFailed(string vendor, Exception ex);

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
}
