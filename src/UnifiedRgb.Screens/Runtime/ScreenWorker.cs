using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Screen states reported in <see cref="ScreenDeviceDto.State"/>.</summary>
public static class ScreenStates
{
    public const string Disabled = "Disabled";
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string Blocked = "Blocked";
    public const string Error = "Error";
    public const string Disconnected = "Disconnected";
    public const string Unsupported = "Unsupported";
    public const string Stopped = "Stopped";

    public const string HeldByAnotherProgram = "another program has the device open";

    public static string Name(ScreenFamilyInfo family, string instance)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(instance);
        return family.DisplayName + " " + (instance.Length > 4 ? instance[..4] : instance);
    }
}

/// <summary>Everything a display worker uses; none of it touches the RGB path.</summary>
internal sealed record ScreenWorkerContext(
    ScreensOptions Options,
    IScreenUsbOpener Opener,
    IScreenContentionProbe Contention,
    AssetStore Assets,
    Func<PcTelemetryDto?> Telemetry,
    TimeProvider Time,
    ILogger Log)
{
    /// <summary>The shared timeline provider for assignments with a TimelineId (null: strict screens show black and report blocked).</summary>
    public Func<IScreenTimeline?> Timeline { get; init; } = static () => null;

    /// <summary>The screen's local encoder policy and Indexed8 capability, from memory (never I/O: it is read for every frame).</summary>
    public Func<string, ScreenEncodingSelection> EncodingOf { get; init; } = static _ => ScreenEncodingSelection.Legacy;

    /// <summary>The frame encoder (the production <see cref="ScreenFrameEncoder"/>; a double in tests).</summary>
    public ScreenEncodeFunc Encode { get; init; } = static (f, c, r, q, i) => ScreenFrameEncoder.Encode(f, c, r, q, i);
    public Func<string, ScreenRateTrial?> TrialOf { get; init; } = static _ => null;
    public Action<string> AbortTrial { get; init; } = static _ => { };
}

internal sealed record WorkerCommand(ScreenAssignmentDto? Assignment, bool Paused, bool Stop = false);

/// <summary>
/// One display, one dedicated STA thread: compose → encode → frame → write → bounded read, at the assignment's FPS (static
/// content refreshes at most once a second). Commands arrive through a latest-wins mailbox. Every transfer is bounded by the
/// family timeout; a failure closes the handle and reopens after a doubling, capped backoff. A running vendor owner blocks the
/// display (handle closed, nothing forced); an endpoint identity mismatch marks it Unsupported and it is never written. Dispose
/// aborts a pending transfer and joins the thread. Exceptions never escape the thread (the RGB Host must not be affected).
/// <para>
/// H2 layer takeover: the H2 shows an independent PNG layer (command 102) over its JPEG layer (101). The vendor pushes its
/// sensor graphics into that PNG layer (il-H2Controller.cs UpdateSensorValues → UpdateLCD(isSendPng: true)) and clears it with a
/// transparent PNG before a new background (ClearPngLayer, called by ApplyTemplate). Our frames are opaque JPEGs, so a stale
/// vendor PNG would stay on top. After each successful open of an H2 channel the worker therefore sends ONE fully transparent
/// 480x480 PNG through the same image-push framing, before that channel's first JPEG. It is image content, not a control,
/// reset, clock or play command; it is not a content frame (not in FramesWritten/Fps) and is counted separately.
/// </para>
/// </summary>
internal sealed partial class ScreenWorker : IDisposable
{
    private const long FrameCacheBytes = 64L * 1024 * 1024;
    private const int BlockedRecheckMs = 2000;

    private readonly ScreenDeviceInfo _device;
    private readonly ScreenWorkerContext _ctx;
    private readonly LatestWins<WorkerCommand> _mailbox = new();
    private readonly Backoff _backoff;
    private readonly Thread _thread;
    private readonly object _stateGate = new();
    private readonly object _channelGate = new();
    private readonly Queue<long> _writes = new();
    private readonly ScreenPerformance _performance = new();
    private IScreenUsbChannel? _channel;
    private string _state = ScreenStates.Idle;
    private string? _blockedBy;
    private string? _lastError;
    private long _framesWritten;
    private long _dropped;
    private long _layerBootstraps;
    private volatile bool _stopping;
    private int _disposed;

    public ScreenWorker(ScreenDeviceInfo device, ScreenWorkerContext ctx, WorkerCommand initial, ScreenDeviceDto? previous = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(initial);
        if (!ctx.Options.OutputAllowed(device.Family.Family))
        {
            throw new InvalidOperationException($"{device.Id}: the output gate is closed; no worker may exist.");
        }

        _device = device;
        _ctx = ctx;
        _framesWritten = previous?.FramesWritten ?? 0;
        _dropped = previous?.Dropped ?? 0;
        _backoff = new Backoff(ctx.Options.BackoffInitialMs, ctx.Options.BackoffMaxMs);
        _mailbox.Post(initial);
        _thread = new Thread(Run) { IsBackground = true, Name = "screen " + device.Id };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public ScreenDeviceInfo Device => _device;
    internal bool HasExited => !_thread.IsAlive;
    internal ScreenPerformanceDto Performance => _performance.Snapshot(Snapshot());

    /// <summary>
    /// The labelled source of the last timeline-driven frame ("device-clock", "upload-anchor: ...", "apply-epoch", "unavailable",
    /// "blocked: ..."); null while the assignment has no TimelineId. Diagnostics/tests.
    /// </summary>
    internal string? TimelineSource => Volatile.Read(ref _timelineSource);

    private string? _timelineSource;

    /// <summary>
    /// The animation time of the next composed frame: the shared timeline's position for an assignment with a TimelineId (so every
    /// package screen samples the same authored instant as the package RGB), else the historical time since this worker's
    /// assignment started. An unknown or blocked timeline sets <paramref name="blocked"/> (the frame is then black, no overlay,
    /// labelled); it never falls back to the local start or shows a held content frame.
    /// </summary>
    private long ElapsedMs(ScreenAssignmentDto assignment, long animationStart, out string? blocked)
    {
        blocked = null;
        if (assignment.TimelineId is not { } id)
        {
            Volatile.Write(ref _timelineSource, null);
            return (long)_ctx.Time.GetElapsedTime(animationStart).TotalMilliseconds;
        }

        var sample = _ctx.Timeline()?.Position(id);
        if (sample is null)
        {
            Volatile.Write(ref _timelineSource, "unavailable");
            blocked = $"shared timeline {id} is unavailable";
            return 0;
        }

        if (sample.Blocked is { } why)
        {
            Volatile.Write(ref _timelineSource, "blocked: " + why);
            blocked = why;
            return 0;
        }

        Volatile.Write(ref _timelineSource, sample.Source);
        return double.IsFinite(sample.PositionMs) && sample.PositionMs > 0 ? (long)Math.Floor(sample.PositionMs) : 0;
    }

    /// <summary>Transparent PNG-layer clears written (H2 only, one per opened channel); never counted as frames.</summary>
    internal long LayerBootstraps
    {
        get
        {
            lock (_stateGate)
            {
                return _layerBootstraps;
            }
        }
    }

    /// <summary>Families with an independent PNG overlay layer that a new channel clears before its first frame: H2 only.</summary>
    internal static bool ClearsPngLayerOnOpen(ScreenFamily family) => family == ScreenFamily.H2;

    public void Post(ScreenAssignmentDto? assignment, bool paused)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _mailbox.Post(new WorkerCommand(assignment, paused));
        }
    }

    public ScreenDeviceDto Snapshot()
    {
        var f = _device.Family;
        lock (_stateGate)
        {
            return new ScreenDeviceDto(_device.Id, ScreenStates.Name(f, _device.Instance), f.Family.ToString(), f.Width, f.Height, _state,
                _state == ScreenStates.Blocked ? _blockedBy : null, _ctx.Options.For(f.Family).ControlVerified, _framesWritten, _dropped,
                _lastError, _state == ScreenStates.Running ? EffectiveFps() : 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping = true;
        _mailbox.Post(new WorkerCommand(null, false, Stop: true));
        lock (_channelGate)
        {
            _channel?.Abort();
        }

        if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(10)))
        {
            LogJoinTimeout(_ctx.Log, _device.Id);
            return; // the thread still owns the mailbox; it exits after its bounded transfer
        }

        _mailbox.Dispose();
    }

    private void Run()
    {
        var family = _device.Family;
        var compositor = new ScreenCompositor(_ctx.Assets, FrameCacheBytes);
        ScreenAssignmentDto? assignment = null;
        var paused = false;
        string? unsupported = null; // the endpoint-mismatch reason once seen: never opened again by this worker
        var animationStart = _ctx.Time.GetTimestamp();
        EncodedFrame? staticFrame = null;
        ScreenEncodingSelection? staticEncoding = null; // the policy staticFrame was encoded with
        ScreenTimestamp? stamp = null;
        var retryAt = 0L;
        var response = new byte[512];
        var clearLayer = false; // the open channel still owes its PNG-layer clear (H2)
        byte[]? transparentPng = null;
        var pacing = new ScreenPacing();
        string? lastTrial = null;
        long previousStart = 0;
        double waitedMs = 0;
        var lastSlow = false;
        var correlationDisabled = false;
        try
        {
            while (!_stopping)
            {
                if (_mailbox.TryTake() is { } cmd)
                {
                    if (cmd.Stop)
                    {
                        break;
                    }

                    if (!Equals(cmd.Assignment, assignment))
                    {
                        compositor.ClearCache();
                        staticFrame = null;
                        animationStart = _ctx.Time.GetTimestamp();
                    }

                    assignment = cmd.Assignment;
                    paused = cmd.Paused;
                    pacing.Reset();
                    lastSlow = false;
                }

                if (paused || assignment is not { Enabled: true })
                {
                    CloseChannel();
                    SetState(paused ? ScreenStates.Stopped : ScreenStates.Idle, null, keepError: false);
                    _mailbox.Wait(-1);
                    continue;
                }

                if (unsupported is not null)
                {
                    SetState(ScreenStates.Unsupported, null, keepError: false, unsupported); // also after a Stopped pause
                    _mailbox.Wait(-1); // until the device is re-plugged (a new worker) or the Host stops
                    continue;
                }

                if (_ctx.Contention.BlockedBy(family.Family) is { } owner)
                {
                    CloseChannel();
                    SetState(ScreenStates.Blocked, owner, keepError: false);
                    _mailbox.Wait(BlockedRecheckMs);
                    continue;
                }

                if (_channel is null)
                {
                    var wait = retryAt == 0 ? 0 : (int)Math.Ceiling((retryAt - _ctx.Time.GetTimestamp()) * 1000.0 / _ctx.Time.TimestampFrequency);
                    if (wait > 0)
                    {
                        _mailbox.Wait(wait);
                        continue;
                    }

                    try
                    {
                        var channel = _ctx.Opener.Open(_device);
                        lock (_channelGate)
                        {
                            _channel = channel;
                        }

                        if (_stopping)
                        {
                            break;
                        }

                        stamp = ScreenTimestamp.System(family.Family);
                        clearLayer = ClearsPngLayerOnOpen(family.Family);
                        LogOpened(_ctx.Log, _device.Id, channel.OutEndpoint, channel.InEndpoint);
                    }
                    catch (ScreenTransferException ex)
                    {
                        if (ex.Kind == ScreenTransferError.EndpointMismatch)
                        {
                            unsupported = ex.Message;
                            LogUnsupported(_ctx.Log, _device.Id, ex.Message);
                            continue;
                        }

                        retryAt = Fail(ex.Kind, ex.Message, countDrop: false);
                        continue;
                    }
                }

                var trial = _ctx.TrialOf(_device.Id);
                var targetFps = trial?.Info.Fps ?? assignment.Fps;
                var period = 1000.0 / targetFps;
                if (lastTrial != trial?.Info.TrialId) { pacing.Reset(); lastTrial = trial?.Info.TrialId; }
                if (lastSlow || staticFrame is not null || !compositor.IsDynamic(assignment)) period = Math.Max(period, 1000);
                if (trial?.Info.LegacyPacing != true)
                {
                    var wait = pacing.Remaining(_ctx.Time.GetTimestamp(), _ctx.Time.TimestampFrequency, period);
                    if (wait > 0)
                    {
                        var waitStart = _ctx.Time.GetTimestamp();
                        _mailbox.Wait(wait);
                        waitedMs += _ctx.Time.GetElapsedTime(waitStart).TotalMilliseconds;
                        continue; // recheck mailbox, output eligibility and current shared time AFTER waking
                    }
                }
                var frameStart = _ctx.Time.GetTimestamp();
                var intervalMs = previousStart == 0 ? 0 : _ctx.Time.GetElapsedTime(previousStart, frameStart).TotalMilliseconds;
                previousStart = frameStart;
                double composeMs = 0, encodeMs = 0;
                var bootstrapping = false;
                try
                {
                    EncodedFrame frame;
                    string? warning = null;
                    var slow = false;
                    if (staticFrame is not null && _ctx.EncodingOf(_device.Id) != staticEncoding)
                    {
                        staticFrame = null; // the local encoder policy changed: re-encode (the assignment and pause flag are unchanged)
                    }

                    if (staticFrame is not null)
                    {
                        frame = staticFrame;
                    }
                    else
                    {
                        var elapsed = ElapsedMs(assignment, animationStart, out var blocked);
                        var composeStart = _ctx.Time.GetTimestamp();
                        var composed = blocked is null
                            ? compositor.Compose(family, assignment, elapsed, _ctx.Telemetry(), _ctx.Time.GetUtcNow())
                            : ScreenCompositor.ComposeBlocked(family, blocked);
                        warning = composed.Warning;
                        composeMs = _ctx.Time.GetElapsedTime(composeStart).TotalMilliseconds;
                        var encoding = _ctx.EncodingOf(_device.Id);
                        var encodeStart = _ctx.Time.GetTimestamp();
                        frame = _ctx.Encode(family, composed.Canvas, _ctx.Options.TurzxLandscapeRotation, encoding.Quality, encoding.Indexed8Verified);
                        encodeMs = _ctx.Time.GetElapsedTime(encodeStart).TotalMilliseconds;
                        var still = !compositor.IsDynamic(assignment);
                        if (assignment.TimelineId is null)
                        {
                            if (still && warning is null)
                            {
                                staticFrame = frame;
                                staticEncoding = encoding;
                            }
                        }
                        else
                        {
                            // strict content is never cached: its timeline can block, so it is re-checked every period
                            slow = still || blocked is not null || warning?.StartsWith(ScreenCompositor.BlockedPrefix, StringComparison.Ordinal) == true;
                        }
                    }

                    // a static frame, a still strict frame or blocked strict content (black) needs no more than one frame a second
                    lastSlow = staticFrame is not null || slow;
                    period = lastSlow ? Math.Max(1000.0 / targetFps, 1000) : 1000.0 / targetFps;
                    if (trial?.Info.LegacyPacing != true) _ = pacing.Remaining(frameStart, _ctx.Time.TimestampFrequency, period);

                    if (clearLayer)
                    {
                        transparentPng ??= WpfCodec.EncodePng(new PixelFrame(family.Width, family.Height)); // all zero: alpha 0
                        bootstrapping = true;
                        _channel!.Write(ScreenProtocol.BuildImagePacket(family.Family, ScreenProtocol.PushPngCommand, transparentPng,
                            stamp!.Next()), family.WriteTimeoutMs);
                        var clearNote = ReadResponse(response, family.ReadTimeoutMs);
                        bootstrapping = false;
                        clearLayer = false;
                        lock (_stateGate)
                        {
                            _layerBootstraps++;
                        }

                        LogLayerCleared(_ctx.Log, _device.Id, family.Width, family.Height, transparentPng.Length, clearNote ?? "ok");
                        if (_stopping)
                        {
                            break; // stopped while clearing: no content frame follows
                        }
                    }

                    var packetStart = _ctx.Time.GetTimestamp();
                    var requestTimestamp = stamp!.Next();
                    var packet = ScreenProtocol.BuildImagePacket(family.Family, frame.Command, frame.Image, requestTimestamp);
                    (_channel as IScreenUsbDiagnostics)?.ExpectResponse(frame.Command, requestTimestamp);
                    var packetMs = _ctx.Time.GetElapsedTime(packetStart).TotalMilliseconds;
                    var writeStart = _ctx.Time.GetTimestamp();
                    _channel!.Write(packet, family.WriteTimeoutMs);
                    var writeMs = _ctx.Time.GetElapsedTime(writeStart).TotalMilliseconds;
                    var readStart = _ctx.Time.GetTimestamp();
                    if (_channel is IScreenUsbDiagnostics configured)
                    {
                        configured.ReplyQuietMs = trial?.Info.ReplyQuietMs ?? 10;
                        configured.CorrelatedReplies = correlationDisabled ? false : trial?.Info.CorrelatedReplies;
                    }
                    var readError = ReadResponse(response, family.Family == ScreenFamily.TlLcd ? trial?.Info.TlReadTimeoutMs ?? family.ReadTimeoutMs : family.ReadTimeoutMs);
                    var readMs = _ctx.Time.GetElapsedTime(readStart).TotalMilliseconds;
                    _backoff.Reset();
                    retryAt = 0;
                    Wrote(warning ?? readError);
                    var skipped = trial?.Info.LegacyPacing == true ? 0 : pacing.Completed(_ctx.Time.GetTimestamp(), _ctx.Time.TimestampFrequency);
                    var facts = (_channel as IScreenUsbDiagnostics)?.LastRead;
                    _performance.Add(new ScreenFrameWork(composeMs, encodeMs, packetMs, writeMs, readMs, waitedMs, intervalMs,
                        _ctx.Time.GetElapsedTime(frameStart).TotalMilliseconds, frame.Image.Length, packet.Length,
                        facts?.Reads ?? 0, facts?.Timeouts ?? 0, facts?.ZeroLength ?? 0, facts?.FirstMs ?? 0, facts?.DrainMs ?? 0,
                        facts?.Matched ?? 0, facts?.Correlated ?? false), targetFps, trial?.Info.TrialId,
                        (_channel as IScreenUsbDiagnostics)?.DeviceSpeedCode, skipped);
                    waitedMs = 0;
                }
                catch (ScreenTransferException ex)
                {
                    if (ex.Kind == ScreenTransferError.ResponseMismatch) correlationDisabled = true;
                    _ctx.AbortTrial(_device.Id);
                    CloseChannel();
                    if (_stopping)
                    {
                        break;
                    }

                    // a failed layer clear is not a dropped content frame; the reopened channel clears again
                    retryAt = bootstrapping
                        ? Fail(ex.Kind, "H2 layer clear: " + ex.Message, countDrop: false)
                        : Fail(ex.Kind, ex.Message, countDrop: true);
                    continue;
                }
#pragma warning disable CA1031 // compose/encode/asset failures (or anything unexpected) must never escape the worker thread
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _ctx.AbortTrial(_device.Id);
                    staticFrame = null;
                    retryAt = 0;
                    lock (_stateGate)
                    {
                        _dropped++;
                        _state = ScreenStates.Error;
                        _lastError = ex is ScreenEncodeException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                    }

                    LogFrameFailed(_ctx.Log, _device.Id, ex.Message);
                    _mailbox.Wait(Math.Max(1000, (int)period));
                    continue;
                }

                var remaining = period - _ctx.Time.GetElapsedTime(frameStart).TotalMilliseconds;
                if (trial?.Info.LegacyPacing == true && remaining >= 1)
                {
                    var waitStart = _ctx.Time.GetTimestamp();
                    _mailbox.Wait((int)remaining);
                    waitedMs += _ctx.Time.GetElapsedTime(waitStart).TotalMilliseconds;
                }
            }
        }
#pragma warning disable CA1031 // last resort: the worker ends in Error rather than taking the Host down
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetState(ScreenStates.Error, null, keepError: false, "worker ended: " + ex.Message);
            LogFrameFailed(_ctx.Log, _device.Id, ex.Message);
        }
        finally
        {
            CloseChannel();
            StaThread.ShutdownDispatcher();
        }
    }

    /// <summary>
    /// One bounded response read. A timeout or an unexpected read error is tolerated (the write already completed) and returned
    /// as the frame's LastError note; a disconnect or abort propagates like a write failure.
    /// </summary>
    private string? ReadResponse(byte[] buffer, int timeoutMs)
    {
        try
        {
            _channel!.Read(buffer, timeoutMs);
            return null;
        }
        catch (ScreenTransferException ex) when (ex.Kind is not (ScreenTransferError.Disconnected or ScreenTransferError.Aborted or ScreenTransferError.ReplyBacklog or ScreenTransferError.ResponseMismatch))
        {
            return "response read: " + ex.Message;
        }
    }

    /// <param name="note">A non-fatal note for LastError (a compose warning or a tolerated response-read error), else null.</param>
    private void Wrote(string? note)
    {
        var now = _ctx.Time.GetTimestamp();
        lock (_stateGate)
        {
            _framesWritten++;
            _writes.Enqueue(now);
            while (_writes.Count > 64 || (_writes.Count > 0 && _ctx.Time.GetElapsedTime(_writes.Peek(), now) > TimeSpan.FromSeconds(5)))
            {
                _writes.Dequeue();
            }

            if (_state != ScreenStates.Running)
            {
                LogRunning(_ctx.Log, _device.Id);
            }

            _state = ScreenStates.Running;
            _blockedBy = null;
            _lastError = note;
        }
    }

    /// <summary>Records an open/transfer failure and returns the timestamp before which no reopen is attempted.</summary>
    private long Fail(ScreenTransferError kind, string message, bool countDrop)
    {
        var delay = _backoff.Fail();
        var state = kind switch
        {
            ScreenTransferError.AccessDenied => ScreenStates.Blocked,
            ScreenTransferError.Disconnected => ScreenStates.Disconnected,
            _ => ScreenStates.Error,
        };
        lock (_stateGate)
        {
            if (countDrop)
            {
                _dropped++;
            }

            _state = state;
            _blockedBy = state == ScreenStates.Blocked ? ScreenStates.HeldByAnotherProgram : null;
            _lastError = message;
        }

        LogFailed(_ctx.Log, _device.Id, kind.ToString(), message, delay);
        return _ctx.Time.GetTimestamp() + (long)(delay / 1000.0 * _ctx.Time.TimestampFrequency);
    }

    private void SetState(string state, string? blockedBy, bool keepError, string? error = null)
    {
        lock (_stateGate)
        {
            if (_state != state)
            {
                LogState(_ctx.Log, _device.Id, state, blockedBy ?? error ?? string.Empty);
            }

            _state = state;
            _blockedBy = blockedBy;
            if (!keepError)
            {
                _lastError = error;
            }

            _writes.Clear();
        }
    }

    private double EffectiveFps()
    {
        if (_writes.Count < 2)
        {
            return 0;
        }

        var span = _ctx.Time.GetElapsedTime(_writes.Peek(), _writes.Last()).TotalSeconds;
        return span <= 0 ? 0 : Math.Round((_writes.Count - 1) / span, 2);
    }

    private void CloseChannel()
    {
        IScreenUsbChannel? channel;
        lock (_channelGate)
        {
            channel = _channel;
            _channel = null;
        }

        channel?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen {Id} opened (OUT 0x{Out:X2}, IN 0x{In:X2})")]
    private static partial void LogOpened(ILogger logger, string id, byte @out, byte @in);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Screen {Id} PNG layer cleared on open (transparent {Width}x{Height} PNG, {Bytes} B, command 102; not a content frame; response {Note})")]
    private static partial void LogLayerCleared(ILogger logger, string id, int width, int height, int bytes, string note);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen {Id} running (frames written; physical display unverified)")]
    private static partial void LogRunning(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen {Id} state {State} {Detail}")]
    private static partial void LogState(ILogger logger, string id, string state, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen {Id} {Kind}: {Message}; retry in {DelayMs} ms")]
    private static partial void LogFailed(ILogger logger, string id, string kind, string message, int delayMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen {Id} frame failed: {Message}")]
    private static partial void LogFrameFailed(ILogger logger, string id, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen {Id} is unsupported and will not be written: {Message}")]
    private static partial void LogUnsupported(ILogger logger, string id, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen {Id} worker did not stop within 10 s")]
    private static partial void LogJoinTimeout(ILogger logger, string id);
}
