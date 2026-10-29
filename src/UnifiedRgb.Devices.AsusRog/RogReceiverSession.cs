using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Devices.Safety;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// Single owner and transaction lock for all vendor channels of one receiver. No input/feature APIs and no raw-send API.
/// A failed/short/mismatched exchange invalidates the generation; queued work cannot reuse it. No retries or save command.
/// </summary>
public sealed class RogReceiverSession : IAsyncDisposable
{
    private readonly IHidReportDevices _devices;
    private readonly RogReceiver _receiver;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<byte, HidReportTransport> _channels = [];
    private readonly int _timeoutMs;
    private long _generation = 1;
    private bool _closed;
    private bool _faulted;
    private IReadOnlyList<RogPairedDevice> _paired = [];
    private readonly HashSet<RogPairedDevice> _staticMouseRead = [];
    private readonly Dictionary<RogPairedDevice, RogPowerState> _powerRead = [];
    private readonly Dictionary<RogPairedDevice, RogKeyboardStaticState> _keyboardStaticRead = [];
    private readonly HashSet<RogPairedDevice> _keyboardEffectRead = [];
    private readonly HashSet<RogPairedDevice> _keyboardNativeRead = [];

    public RogReceiverSession(IHidReportDevices devices, RogReceiver receiver, int timeoutMs = 250)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 2000);
        _timeoutMs = timeoutMs;
    }

    public long Generation => Interlocked.Read(ref _generation);
    public bool IsAvailable => !Volatile.Read(ref _closed) && !Volatile.Read(ref _faulted);
    public string? PairingReplyHex { get; private set; }
    public string? KeyboardEffectReplyHex { get; private set; }
    public byte? KeyboardProfileIndex { get; private set; }

    public async Task<IReadOnlyList<RogPairedDevice>> QueryPairingAsync(CancellationToken ct)
    {
        var generation = Generation;
        return await TransactionAsync(generation, async token =>
        {
            var reply = await ExchangeAsync(RogProtocol.PairingQuery(), token).ConfigureAwait(false);
            _paired = RogProtocol.ParsePairing(reply);
            PairingReplyHex = Convert.ToHexString(reply);
            return _paired;
        }, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> QueryKeyboardAsync(RogPairedDevice target, bool layout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var generation = Generation;
        return await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            if (!target.IsAzothX) throw new InvalidOperationException("Keyboard queries require an identified Azoth X route.");
            var reply = await ExchangeAsync(layout ? RogProtocol.KeyboardLayoutQuery(target.ReportId)
                : RogProtocol.KeyboardVersionQuery(target.ReportId), token).ConfigureAwait(false);
            if (layout && !RogProtocol.IsKeyboardLayout(reply, target.ReportId))
                throw new InvalidDataException("ROG keyboard layout is zero or unavailable (possibly asleep).");
            return reply;
        }, ct).ConfigureAwait(false);
    }

    public Task<RogLeverModeState> QueryCurrentLeverAsync(RogPairedDevice target, CancellationToken ct) =>
        TransactionAsync(Generation, async token =>
        {
            RequireLeverTarget(target);
            return RogLeverProtocol.ParseCurrentMode(await ExchangeAsync(RogLeverProtocol.CurrentModeQuery(target.ReportId), token,
                rejectStatus: true, maximumReports: RogLeverProtocol.MaximumGetterReads).ConfigureAwait(false), target.ReportId);
        }, ct);

    public Task<RogLeverEnabledModes> QueryEnabledLeverModesAsync(RogPairedDevice target, CancellationToken ct) =>
        TransactionAsync(Generation, async token =>
        {
            RequireLeverTarget(target);
            return RogLeverProtocol.ParseEnabledModes(await ExchangeAsync(RogLeverProtocol.EnabledModesQuery(target.ReportId), token,
                rejectStatus: true, maximumReports: RogLeverProtocol.MaximumGetterReads).ConfigureAwait(false), target.ReportId);
        }, ct);

    private void RequireLeverTarget(RogPairedDevice target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequirePaired(target);
        if (_receiver.ProductId != RogKeyboardInputContract.ReceiverProductId || !RogKeyboardInputContract.IsVerifiedTarget(target))
            throw new DeviceBlockedException("Lever reads require the verified 1ACE/1C25/RID2/notify4 route.");
    }

    public async Task<byte[]> QueryMouseLightingAsync(RogPairedDevice target, ushort area, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var generation = Generation;
        return await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            if (!target.IsHarpeEdition20) throw new InvalidOperationException("Mouse lighting queries require an identified P725 route.");
            var reply = await ExchangeAsync(RogProtocol.MouseLightingQuery(target.ReportId, area), token).ConfigureAwait(false);
            if (reply[3] != (byte)area || reply[4] != (byte)(area >> 8)) throw new InvalidDataException("ROG mouse area reply mismatch.");
            if (area == 0)
            {
                RogProtocol.ParseMouseStatic(reply, target.ReportId);
                _staticMouseRead.Add(target);
            }
            return reply;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RogKeyboardStaticState> QueryKeyboardStaticAsync(RogPairedDevice target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return await TransactionAsync(Generation, async token =>
        {
            RequirePaired(target);
            if (!target.IsAzothX) throw new DeviceBlockedException("Keyboard lighting readback requires Azoth X.");
            var info = await ExchangeAsync(RogProtocol.KeyboardVersionQuery(target.ReportId), token).ConfigureAwait(false);
            // Exact-model GearLink uses BasicInfo P[9] (Windows W[10]) for the ACTIVE lighting effect.
            if (info[9] is < 1 or > 6 || info[11] is < 1 or > 6 || info[10] != 0)
                throw new DeviceBlockedException("Active keyboard effect is not proven Static; refusing an incomplete restore snapshot.");
            await Task.Delay(150, token).ConfigureAwait(false);
            var state = RogProtocol.ParseKeyboardStatic(await ExchangeAsync(RogProtocol.KeyboardStaticQuery(target.ReportId), token).ConfigureAwait(false), target.ReportId);
            _keyboardStaticRead[target] = state;
            return state;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RogKeyboardEffectState> QueryKeyboardEffectAsync(RogPairedDevice target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return await TransactionAsync(Generation, async token =>
        {
            RequirePaired(target);
            if (!target.IsAzothX) throw new DeviceBlockedException("Keyboard effect readback requires Azoth X.");
            var basic = await ExchangeAsync(RogProtocol.KeyboardVersionQuery(target.ReportId), token).ConfigureAwait(false);
            if (basic[9] is < 1 or > 6 || basic[11] is < 1 or > 6) throw new DeviceBlockedException("Keyboard active profile is unavailable.");
            var query = RogProtocol.KeyboardEffectQuery(target.ReportId, basic[10]);
            await Task.Delay(150, token).ConfigureAwait(false);
            var reply = await ExchangeAsync(query, token).ConfigureAwait(false);
            KeyboardEffectReplyHex = Convert.ToHexString(reply);
            var state = RogProtocol.ParseKeyboardEffect(reply, target.ReportId, basic[10]);
            _keyboardEffectRead.Add(target);
            return state;
        }, ct).ConfigureAwait(false);
    }

    public async Task<RogKeyboardNativeState> QueryKeyboardNativeAsync(RogPairedDevice target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return await TransactionAsync(Generation, async token =>
        {
            RequirePaired(target);
            if (!target.IsAzothX) throw new DeviceBlockedException("Keyboard effect snapshot requires Azoth X.");
            var basic = await ExchangeAsync(RogProtocol.KeyboardVersionQuery(target.ReportId), token).ConfigureAwait(false);
            if (basic[9] is < 1 or > 6 || basic[11] is < 1 or > 6) throw new DeviceBlockedException("Keyboard active profile is unavailable.");
            var effect = basic[10];
            KeyboardProfileIndex = basic[11];
            var layout = RogProtocol.KeyboardPayloadLengths(effect);
            var payloads = new List<string>(layout.Count);
            for (var i = 0; i < layout.Count; i++)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                var reply = await ExchangeAsync(RogProtocol.KeyboardNativeQuery(target.ReportId, effect, i), token).ConfigureAwait(false);
                KeyboardEffectReplyHex = Convert.ToHexString(reply);
                payloads.Add(RogProtocol.ParseKeyboardNativePayload(reply, target.ReportId, effect, i));
            }
            _keyboardNativeRead.Add(target);
            return new RogKeyboardNativeState(effect, payloads.AsReadOnly());
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Snapshot a known inactive parameter bank too, so a temporary static test does not overwrite its RAM settings.</summary>
    public async Task<RogKeyboardNativeState> QueryKeyboardBankAsync(RogPairedDevice target, byte effectType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var layout = RogProtocol.KeyboardPayloadLengths(effectType);
        return await TransactionAsync(Generation, async token =>
        {
            RequirePaired(target);
            if (!target.IsAzothX || !_keyboardNativeRead.Contains(target)) throw new DeviceBlockedException("Read the active keyboard effect before its parameter banks.");
            var payloads = new List<string>(layout.Count);
            for (var i = 0; i < layout.Count; i++)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                var reply = await ExchangeAsync(RogProtocol.KeyboardNativeQuery(target.ReportId, effectType, i), token).ConfigureAwait(false);
                payloads.Add(RogProtocol.ParseKeyboardNativePayload(reply, target.ReportId, effectType, i));
            }
            return new RogKeyboardNativeState(effectType, payloads.AsReadOnly());
        }, ct).ConfigureAwait(false);
    }

    public async Task WriteKeyboardNativeAsync(RogPairedDevice target, RogKeyboardNativeState state, bool lightingAllowed,
        long generation, CancellationToken ct, FrameBudget? frameBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!lightingAllowed || !target.IsAzothX) throw new DeviceBlockedException("Temporary keyboard lighting is not enabled.");
        var reports = RogProtocol.KeyboardNativeReports(target.ReportId, state);
        await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            if (!_keyboardNativeRead.Contains(target)) throw new DeviceBlockedException("Snapshot the complete active keyboard effect before output.");
            for (var i = 0; i < reports.Count; i++)
            {
                frameBudget?.CountOperation();
                await Channel(target.ReportId).SendAsync(reports[i], token).ConfigureAwait(false);
                if (i + 1 < reports.Count) await Task.Delay(10, token).ConfigureAwait(false);
            }
            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task WriteKeyboardEffectAsync(RogPairedDevice target, RogKeyboardEffectState state, bool lightingAllowed,
        long generation, CancellationToken ct, FrameBudget? frameBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!lightingAllowed || !target.IsAzothX) throw new DeviceBlockedException("Temporary keyboard lighting is not enabled.");
        var report = RogProtocol.KeyboardEffectReport(target.ReportId, state);
        await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            if (!_keyboardEffectRead.Contains(target)) throw new DeviceBlockedException("Snapshot the active keyboard effect before sending temporary lighting.");
            frameBudget?.CountOperation();
            await Channel(target.ReportId).SendAsync(report, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Single-colour static write, no ACK/profile save. A current active-static snapshot is mandatory.</summary>
    public async Task WriteKeyboardStaticAsync(RogPairedDevice target, RogKeyboardStaticState state, bool lightingAllowed,
        long generation, CancellationToken ct, FrameBudget? frameBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!lightingAllowed || !target.IsAzothX) throw new DeviceBlockedException("Azoth X temporary whole-colour output is not enabled.");
        var report = RogProtocol.KeyboardStaticReport(target.ReportId, state);
        await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            if (!_keyboardStaticRead.ContainsKey(target)) throw new DeviceBlockedException("A current active-static keyboard snapshot is required.");
            frameBudget?.CountOperation();
            await Channel(target.ReportId).SendAsync(report, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task WriteKeyboardAsync(RogPairedDevice target, IReadOnlyList<RogKeyColor> keys,
        bool controlVerified, long generation, CancellationToken ct, FrameBudget? frameBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keys);
        if (!controlVerified) throw new DeviceBlockedException("ROG keyboard control has not been verified.");
        if (!target.IsAzothX) throw new DeviceBlockedException("Direct keyboard lighting requires an identified Azoth X.");
        var reports = RogProtocol.KeyboardFrame(target.ReportId, keys.ToArray());
        await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            foreach (var report in reports)
            {
                frameBudget?.CountOperation();
                await ExchangeAsync(report, token).ConfigureAwait(false);
            }
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Write-only exact-model area0/static setter. Completion is transport delivery, not an invented device ACK.</summary>
    public async Task WriteMouseStaticAsync(RogPairedDevice target, RogMouseStaticState state,
        bool lightingAllowed, long generation, CancellationToken ct, FrameBudget? frameBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!lightingAllowed || !target.IsHarpeEdition20) throw new DeviceBlockedException("P725 temporary mouse lighting is not enabled for this identified route.");
        var report = RogProtocol.MouseStaticReport(target.ReportId, state);
        await TransactionAsync(generation, async token =>
        {
            RequirePaired(target);
            if (!_staticMouseRead.Contains(target)) throw new DeviceBlockedException("A valid current mouse area0/static snapshot is required before output.");
            frameBudget?.CountOperation();
            await Channel(report[0]).SendAsync(report, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    private void RequirePaired(RogPairedDevice target)
    {
        if (!_paired.Contains(target) || !_receiver.Channels.ContainsKey(target.ReportId))
            throw new DeviceBlockedException("ROG route has not been identified on this receiver session.");
    }

    public async Task<RogPowerState> QueryPowerAsync(RogPairedDevice target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return await TransactionAsync(Generation, async token =>
        {
            RequirePaired(target);
            var state = RogPowerProtocol.Parse(target, await ExchangeAsync(RogPowerProtocol.Query(target), token).ConfigureAwait(false));
            _powerRead[target] = state;
            return state;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Explicit owner policy action only. No save/profile commit, and all non-sleep power settings are verified.</summary>
    public async Task<RogPowerChange> SetIdleTimeoutAsync(RogPairedDevice target, byte idleCode, bool ownerAuthorized, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ownerAuthorized) throw new DeviceBlockedException("Changing peripheral sleep requires an explicit owner action.");
        if (!RogPowerProtocol.IsKnownIdle(idleCode)) throw new ArgumentOutOfRangeException(nameof(idleCode));
        return await TransactionAsync(Generation, async token =>
        {
            RequirePaired(target);
            if (!_powerRead.TryGetValue(target, out var before)) throw new DeviceBlockedException("Read the current power settings before changing sleep.");
            await ExchangeAsync(RogPowerProtocol.IdleReport(target, before, idleCode), token).ConfigureAwait(false);
            var after = RogPowerProtocol.Parse(target, await ExchangeAsync(RogPowerProtocol.Query(target), token).ConfigureAwait(false));
            if (after.IdleCode != idleCode || !before.SameOtherSettings(after)) throw new InvalidDataException("ROG sleep readback differs from the requested isolated setting.");
            _powerRead[target] = after;
            return new RogPowerChange(before, after);
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> TransactionAsync<T>(long generation, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // One complete keyboard frame has at most ten packets; queue wait and every exchange share this finite deadline.
        bound.CancelAfter(_timeoutMs * 12);
        await _gate.WaitAsync(bound.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_faulted || generation != Generation) throw new DeviceBlockedException("ROG connection generation is stale; rediscovery is required.");
            try { return await operation(bound.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException
                or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                _faulted = true;
                Interlocked.Increment(ref _generation);
                CloseChannels();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private HidReportTransport Channel(byte rid)
    {
        if (!_channels.TryGetValue(rid, out var channel))
        {
            var collection = _receiver.Channels[rid];
            _channels[rid] = channel = HidReportTransport.Open(_devices, collection, _receiver.Rule(rid), _timeoutMs);
        }
        return channel;
    }

    private async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken ct, bool rejectStatus = false, int maximumReports = 64)
    {
        var channel = Channel(request[0]);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(_timeoutMs);
        try
        {
            await channel.SendAsync(request, bound.Token).ConfigureAwait(false);
            var reply = new byte[RogProtocol.ReportLength];
            // The owner rig queued four 51 2C echoes before the valid query reply. Drain a finite HID ring,
            // rather than confusing an old lighting echo with a failed query. The same total deadline still applies.
            for (var received = 0; received < maximumReports; received++)
            {
                var length = await channel.ReadAsync(reply, bound.Token).ConfigureAwait(false);
                if (length != reply.Length) throw new InvalidDataException("ROG returned a short vendor report.");
                if (rejectStatus && RogLeverProtocol.IsRefusal(reply, request[0]))
                    throw new InvalidDataException("The lever getter returned FFAA without a valid echo; no retry.");
                if (reply[0] == request[0] && reply[1] == request[1] && reply[2] == request[2]) return reply;
                // Status/FFAA/old write echoes are never accepted as a query response and are never resent.
                if (received == maximumReports - 1) RogProtocol.RequireEcho(reply, request[0], request[1], request[2]);
            }
            throw new InvalidDataException("ROG command echo was not received.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("ROG command echo deadline expired; no command was resent.");
        }
    }

    private void CloseChannels()
    {
        foreach (var channel in _channels.Values) channel.Dispose();
        _channels.Clear();
        _paired = [];
        _staticMouseRead.Clear();
        _powerRead.Clear();
        _keyboardStaticRead.Clear();
        _keyboardEffectRead.Clear();
        _keyboardNativeRead.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            Interlocked.Increment(ref _generation);
            CloseChannels(); // no release/save/black packet: closing is not a claim of restored lighting.
        }
        finally { _gate.Release(); }
    }
}
