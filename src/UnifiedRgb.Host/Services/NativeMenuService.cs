using System.Text.Json;
using Microsoft.Extensions.Hosting;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// One typed, already validated event of Root's trusted native-menu source. The program layer never sees raw input: build,
/// profile and session are the source's proof, never assumed here. Only this assembly defines event kinds.
/// </summary>
public abstract record NativeMenuSourceEvent
{
    private protected NativeMenuSourceEvent() { }
}

/// <summary>The source established a verified context; presses and neutral observations at or below the floor came before it.</summary>
public sealed record NativeMenuContextBegun(string SessionId, long ContextGeneration, long ProfileGeneration, long SequenceFloor)
    : NativeMenuSourceEvent;

/// <summary>The context ended (unavailable, asleep, firmware/profile/build change, fault); Disconnected ends its session for good.</summary>
public sealed record NativeMenuContextEnded(string SessionId, long ContextGeneration, bool Disconnected, string Reason) : NativeMenuSourceEvent;

/// <summary>One normalized press, in the source's own context generation.</summary>
public sealed record NativeMenuPress(NativeMenuCaseIntent Intent) : NativeMenuSourceEvent;

/// <summary>
/// One Host-local Up/Down scene-browse step (Up = <see cref="SceneCycleMove.Previous"/>, Down = <see cref="SceneCycleMove.Next"/>),
/// separate from <see cref="NativeMenuPress"/>: the source emits it only for a fresh edge its decoder accepted directly after a
/// validated neutral, in the same session, context, profile and cookie scope. It never carries a case intent, a public DTO value or
/// an RPC number, and only the scene bridge receives it.
/// </summary>
public sealed record NativeMenuSceneBrowse(string SessionId, long ContextGeneration, long ProfileGeneration, long Sequence,
    DateTimeOffset Timestamp, SceneCycleMove Move, long NeutralArmSequence) : NativeMenuSourceEvent;

/// <summary>
/// The one seam to Root's future trusted source. <see cref="NativeMenuService"/>, the single owner, connects once; deliver does
/// not block and takes one event at a time. Disposing the registration stops delivery.
/// </summary>
public interface INativeMenuSource
{
    /// <summary>False while no trusted source is installed.</summary>
    bool IsPresent { get; }

    /// <summary>Operational preference, separate from installation policy. Default sources perform no work.</summary>
    void RequestEnabled(bool enabled) { }

    IDisposable Connect(Action<NativeMenuSourceEvent> deliver);
}

/// <summary>Default until Root supplies a verified source: delivers nothing, so the menu stays Unavailable and never acts.</summary>
public sealed class UnavailableNativeMenuSource : INativeMenuSource
{
    public bool IsPresent => false;

    public IDisposable Connect(Action<NativeMenuSourceEvent> deliver) => Registration.None;

    private sealed class Registration : IDisposable
    {
        public static readonly Registration None = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Host owner of the native-menu case actions: one <see cref="NativeMenuCaseActionCoordinator"/> on this Host's own RGB and
/// screen services, fed only by the injected <see cref="INativeMenuSource"/>. The local preference (default off) is a request:
/// a context is armed only while it is on, the source holds a verified context and the ROG interfaces are not handed off for
/// peripheral configuration. Arming never acts; only a later fresh, neutral-armed press does, and every arm starts after the
/// presses already seen, so a press held across a disable or handoff needs a new neutral. Disabling, a handoff, the end of the
/// source's context and Stop disarm: queued intents are not sent, and the running one's outcome is reported as is (unknown when
/// unanswered), never resent. Fresh readbacks of the latest press go to the keyboard display's own artwork. Validated Up/Down
/// steps (<see cref="NativeMenuSceneBrowse"/>) go only to the optional <see cref="KeyboardSceneCycleBridge"/>, under the same
/// context, preference, handoff, sequence, neutral and freshness rules; the center path is unchanged and the bridge only hears of
/// its admissions and outcomes.
/// </summary>
public sealed class NativeMenuService : IHostedService, IAsyncDisposable, INativeMenuService
{
    private const int TraceLimit = 64;
    private const string UnreadablePreference = "原生菜单联动偏好无法读取";
    private readonly INativeMenuSource _source;
    private readonly KeyboardDisplayService? _display;
    private readonly PeripheralConfigurationGate? _gate;
    private readonly TimeProvider _time;
    private readonly KeyboardSceneCycleBridge? _sceneCycle;
    private readonly NativeMenuCaseActionCoordinator _menu;
    private readonly string _preferencePath;
    private readonly object _sync = new();
    private readonly Queue<string> _trace = new(); // guarded by _sync, like every field below
    private readonly List<Task> _observers = [];
    private IDisposable? _registration;
    private bool _desired, _preferenceReadable = true, _stopped;
    private SourceContext? _context; // the source's latest context, active or ended
    private Armed? _armed;
    private long _generation; // coordinator context generations: increasing for this owner's lifetime
    private long _admitted, _reported;
    private string? _lastOutcome, _lastError, _lastContextEndReason;
    private DateTimeOffset? _updatedAt;

    public NativeMenuService(UnifiedRgbOptions options, IRgbService rgb, IScreenService? screens, INativeMenuSource source,
        KeyboardDisplayService? display = null, PeripheralConfigurationGate? gate = null, TimeProvider? time = null,
        KeyboardSceneCycleBridge? sceneCycle = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rgb);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _display = display;
        _gate = gate;
        _time = time ?? TimeProvider.System;
        _sceneCycle = sceneCycle;
        _preferencePath = Path.Combine(options.ResolvedDataDirectory, "peripherals", "native-menu.json");
        var host = new NativeMenuCaseHost(rgb, screens);
        _menu = new NativeMenuCaseActionCoordinator(() => host, _time);
        LoadPreference();
        if (gate is not null) gate.RogAcquisitionChanged += OnAcquisitionChanged;
    }

    /// <summary>Bounded snapshot of what this owner did, newest last (tests and diagnostics).</summary>
    internal string[] Trace
    {
        get { lock (_sync) return [.. _trace]; }
    }

    private bool AcquisitionAllowed => _gate?.RogAcquisitionAllowed != false;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_stopped || _registration is not null) return Task.CompletedTask;
        }
        IDisposable? registration;
        // Outside the lock: a source may deliver during Connect.
        try { registration = _source.Connect(Deliver); _source.RequestEnabled(_desired); }
#pragma warning disable CA1031 // A failing source disables only the native menu, never the Host.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            lock (_sync)
            {
                _lastError = "菜单来源接入失败：" + ex.GetType().Name;
                Record("source connect failed: " + ex.GetType().Name);
            }
            return Task.CompletedTask;
        }
        lock (_sync)
        {
            if (!_stopped && _registration is null)
            {
                _registration = registration;
                registration = null;
                Record(_source.IsPresent ? "source connected" : "no trusted source installed");
            }
        }
        registration?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Disarms for good and joins the coordinator; an unanswered write stays unknown and is never resent.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IDisposable? registration;
        lock (_sync)
        {
            registration = _registration;
            _registration = null;
            _stopped = true;
            _armed = null;
            Record("stopping");
        }
        if (_gate is not null) _gate.RogAcquisitionChanged -= OnAcquisitionChanged;
        _source.RequestEnabled(false);
        registration?.Dispose();
        var stop = await _menu.StopAsync(cancellationToken).ConfigureAwait(false);
        Task[] observers;
        lock (_sync) observers = [.. _observers];
        if (stop.Joined)
        {
            try { await Task.WhenAll(observers).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        lock (_sync)
        {
            if (stop.UnansweredWrite != false) _lastError = "停止时仍有未答复的机箱请求：结果未知，未重发。";
            Record("stopped: " + stop.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _menu.DisposeAsync().ConfigureAwait(false);
    }

    public Task<NativeMenuStatusDto> GetNativeMenuStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync) return Task.FromResult(Status());
    }

    /// <summary>Persists the request, then arms or disarms; it never sends a case action by itself.</summary>
    public Task<NativeMenuStatusDto> SetNativeMenuEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_stopped) throw new InvalidOperationException("The Host is stopping; the native-menu preference was not changed.");
            KeyboardDisplayService.Save(_preferencePath, new Preference(1, enabled));
            _desired = enabled;
            _source.RequestEnabled(enabled);
            _preferenceReadable = true;
            _lastError = null;
            Record("preference saved: " + (enabled ? "on" : "off"));
            if (enabled) TryArm("enabled");
            else Disarm("disabled");
            return Task.FromResult(Status());
        }
    }

    private void LoadPreference()
    {
        try
        {
            if (!File.Exists(_preferencePath)) return; // Default off.
            var preference = JsonSerializer.Deserialize<Preference>(KeyboardDisplayService.ReadBytes(_preferencePath))
                ?? throw new InvalidDataException("Missing native-menu preference.");
            if (preference.Schema != 1) throw new InvalidDataException("Unknown native-menu preference schema.");
            _desired = preference.Enabled;
            Record("preference loaded: " + (_desired ? "on" : "off"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Fails closed for the native menu only: off, reported, and replaced only by an explicit new choice.
            _desired = false;
            _preferenceReadable = false;
            _lastError = UnreadablePreference + "：" + ex.GetType().Name;
            Record("preference unreadable, treated as off: " + ex.GetType().Name);
        }
    }

    private void Deliver(NativeMenuSourceEvent sourceEvent)
    {
        lock (_sync)
        {
            if (_stopped) { Record("ignored after stop: " + sourceEvent?.GetType().Name); return; }
            switch (sourceEvent)
            {
                case NativeMenuContextBegun begun: Begin(begun); break;
                case NativeMenuContextEnded ended: End(ended); break;
                case NativeMenuPress press: Press(press.Intent); break;
                case NativeMenuSceneBrowse browse: Browse(browse); break;
                default: Record("ignored unknown event"); break;
            }
        }
    }

    // Under _sync.
    private void Begin(NativeMenuContextBegun begun)
    {
        if (string.IsNullOrEmpty(begun.SessionId) || begun.SessionId.Length > NativeMenuCaseActionCoordinator.MaxSessionIdLength
            || begun.ContextGeneration <= 0 || begun.ProfileGeneration < 0 || begun.SequenceFloor < 0)
        {
            Disarm("malformed context");
            if (_context is not null) _context.Active = false;
            Record("rejected malformed context");
            return;
        }
        if (_context is { } last && last.Session == begun.SessionId && begun.ContextGeneration <= last.Generation)
        {
            Record($"ignored stale context {begun.ContextGeneration} (latest {last.Generation})");
            return;
        }
        Disarm($"context {begun.ContextGeneration} began");
        // A superseded session never comes back, armed or not; the coordinator remembers it.
        if (_context is { } previous && previous.Session != begun.SessionId) _menu.Invalidate(previous.Session, previous.Generation, disconnected: true);
        _context = new(begun.SessionId, begun.ContextGeneration, begun.ProfileGeneration) { HighWater = begun.SequenceFloor };
        _lastContextEndReason = null;
        Record($"context {begun.ContextGeneration} profile {begun.ProfileGeneration} floor {begun.SequenceFloor}");
        TryArm("context began");
    }

    // Under _sync.
    private void End(NativeMenuContextEnded ended)
    {
        if (_context is not { } current || current.Session != ended.SessionId
            || (!ended.Disconnected && (!current.Active || ended.ContextGeneration != current.Generation)))
        {
            Record($"ignored end of context {ended.ContextGeneration}: not current");
            return;
        }
        Disarm("source: " + ended.Reason);
        current.Active = false;
        _lastContextEndReason = ended.Reason;
        if (ended.Disconnected) _menu.Invalidate(current.Session, current.Generation, disconnected: true);
        Record($"context {current.Generation} ended{(ended.Disconnected ? ", session disconnected" : string.Empty)}: {ended.Reason}");
    }

    // Under _sync.
    private void Press(NativeMenuCaseIntent? intent)
    {
        if (intent is null) { Record("rejected empty press"); return; }
        var press = $"press #{intent.Sequence} {intent.Action}";
        var rejection = _context is not { } context ? "no context"
            : intent.SessionId != context.Session ? "other session"
            : intent.ContextGeneration < context.Generation || (intent.ContextGeneration == context.Generation && !context.Active) ? "stale context"
            : intent.ContextGeneration > context.Generation ? "unknown context" : null;
        if (rejection is not null) { Record($"{press} rejected: {rejection}"); return; }
        // In the source's current context: remember the press, so a later arm starts after it.
        _context!.HighWater = Math.Max(_context.HighWater, intent.Sequence);
        if (!_desired) { Record($"{press} ignored: preference off"); return; }
        if (!AcquisitionAllowed)
        {
            Disarm("configuration handoff");
            Record($"{press} ignored: configuration handoff");
            return;
        }
        if (_armed is not { } armed) { Record($"{press} ignored: not armed"); return; }
        var submitted = _menu.Submit(intent with { ContextGeneration = armed.Generation });
        if (submitted.Outcome is not { } outcome)
        {
            Record($"{press} rejected: {submitted.Rejection}");
            return;
        }
        var ticket = ++_admitted;
        Record($"{press} accepted");
        // Notification only: a scene selection that was not written yet yields to this center action (nothing is sent here).
        _sceneCycle?.CenterAccepted(ticket);
        _observers.RemoveAll(t => t.IsCompleted);
        _observers.Add(Task.Run(() => ObserveAsync(outcome, armed.Generation, ticket)));
    }

    // Under _sync. Up/Down never reach the case coordinator: an admitted step only moves the scene bridge's selection. Same context,
    // sequence and preference rules as a press; a step consumes its sequence (a later arm starts after it) whether admitted or not.
    private void Browse(NativeMenuSceneBrowse browse)
    {
        var step = $"browse #{browse.Sequence} {browse.Move}";
        var rejection = _context is not { } context ? "no context"
            : browse.SessionId != context.Session ? "other session"
            : browse.ContextGeneration < context.Generation || (browse.ContextGeneration == context.Generation && !context.Active) ? "stale context"
            : browse.ContextGeneration > context.Generation ? "unknown context"
            : browse.ProfileGeneration != context.Profile ? "profile mismatch"
            : !Enum.IsDefined(browse.Move) ? "unknown move"
            : browse.Sequence <= context.HighWater ? "replayed"
            : browse.Sequence - context.HighWater > NativeMenuCaseActionCoordinator.MaxSequenceAdvance ? "sequence out of range" : null;
        if (rejection is not null) { Record($"{step} rejected: {rejection}"); return; }
        var previous = _context!.HighWater;
        _context.HighWater = browse.Sequence;
        if (!_desired) { Record($"{step} ignored: preference off"); return; }
        if (!AcquisitionAllowed)
        {
            Disarm("configuration handoff");
            Record($"{step} ignored: configuration handoff");
            return;
        }
        if (_armed is null) { Record($"{step} ignored: not armed"); return; }
        if (browse.NeutralArmSequence <= previous || browse.NeutralArmSequence >= browse.Sequence)
        {
            Record($"{step} rejected: stale neutral arm");
            return;
        }
        var now = _time.GetUtcNow();
        if (browse.Timestamp > now + NativeMenuCaseActionCoordinator.MaxClockSkew) { Record($"{step} rejected: future timestamp"); return; }
        if (now - browse.Timestamp > NativeMenuCaseActionCoordinator.MaxIntentAge) { Record($"{step} rejected: expired"); return; }
        if (_sceneCycle is null) { Record($"{step} ignored: no scene cycle"); return; }
        Record(_sceneCycle.Browse(browse.Move) ? $"{step} accepted" : $"{step} dropped: scene cycle busy or stopped");
    }

    // Under _sync. Arming acts on nothing; only a later fresh press can.
    private void TryArm(string reason)
    {
        if (_stopped || _armed is not null || !_desired || _context is not { Active: true } context || !AcquisitionAllowed) return;
        var generation = ++_generation;
        var change = _menu.Begin(context.Session, generation, context.Profile, context.HighWater);
        if (!change.Applied) { Record("arm refused: " + change.Message); return; }
        _armed = new(context.Session, generation);
        Record($"armed context {context.Generation} as {generation} after #{context.HighWater} ({reason})");
    }

    // Under _sync.
    private void Disarm(string reason)
    {
        if (_armed is not { } armed) return;
        _armed = null;
        var change = _menu.Invalidate(armed.Session, armed.Generation, disconnected: false);
        Record($"disarmed ({reason}): {change.Suppressed} queued not sent"
            + (change.InFlight is { } running ? $", #{running.Sequence} in flight: outcome as answered, never resent" : string.Empty));
    }

    private void OnAcquisitionChanged()
    {
        lock (_sync)
        {
            if (_stopped) return;
            if (AcquisitionAllowed) TryArm("configuration handoff ended");
            else Disarm("configuration handoff");
        }
    }

    private async Task ObserveAsync(Task<NativeMenuCaseOutcome> pending, long generation, long ticket)
    {
        var outcome = await pending.ConfigureAwait(false);
        var sequence = outcome.Intent.Sequence;
        bool present, latest;
        lock (_sync)
        {
            if (ticket > _reported)
            {
                _reported = ticket;
                _lastOutcome = Summary(outcome);
            }
            Record($"outcome #{sequence}: {outcome.State}{(outcome.NotSentReason is { } why ? "/" + why : string.Empty)}"
                + $"{(outcome.Stale ? " stale" : string.Empty)}, readback {outcome.Readback?.State.ToString() ?? "none"}");
            latest = Latest(generation, ticket);
            present = outcome is { Stale: false, Readback: not null } && latest;
        }
        // Notification only, after the center outcome: a held scene selection is applied by the bridge only after a restore.
        _sceneCycle?.CenterCompleted(ticket, outcome, latest);
        if (!present || _display is null) return;
        bool shown;
        try { shown = _display.PresentCaseObservation(outcome.Readback!, () => { lock (_sync) return Latest(generation, ticket); }); }
#pragma warning disable CA1031 // Artwork is feedback only; it never changes the outcome.
        catch (Exception ex) when (ex is not OutOfMemoryException) { lock (_sync) Record($"artwork #{sequence} failed: {ex.GetType().Name}"); return; }
#pragma warning restore CA1031
        lock (_sync) Record(shown ? $"artwork #{sequence}: {outcome.Readback!.State}" : $"artwork #{sequence} refused");
    }

    // Under _sync: still armed in the same context, no newer press admitted, not stopping.
    private bool Latest(long generation, long ticket) => !_stopped && _armed?.Generation == generation && _admitted == ticket;

    // Under _sync.
    private NativeMenuStatusDto Status()
    {
        var (state, detail) = _stopped ? ("Stopped", "后台正在停止或已停止：原生菜单联动已解除，未执行的请求不会发送。")
            : !_preferenceReadable ? ("Error", "原生菜单联动偏好无法读取，已按关闭处理，不执行机箱动作；可重新选择开启或关闭。")
            : !_desired ? ("Off", "原生菜单联动未开启（默认关闭）：菜单操作不会触发机箱动作。")
            : !_source.IsPresent ? ("Unavailable", "已请求开启，但尚未接入经验证的菜单来源：不可用，不执行机箱动作。")
            : _context is not { Active: true } ? ("Unavailable", "已请求开启，菜单连接当前未就绪。" + (_lastContextEndReason is { } reason ? "连接结束原因：" + reason : "等待键盘进入光域档。"))
            : !AcquisitionAllowed ? ("Paused", "键鼠外设配置中：原生菜单联动已暂停；恢复联动后需要新的菜单操作。")
            : _armed is null ? ("Unavailable", "菜单来源的连接已失效或被替代：等待新的经验证连接，不执行机箱动作。")
            : ("Active", "经验证的菜单来源已就绪：按下切换开/关，每次按下按后台当前场景决定，结果以回读为准；"
                + (_sceneCycle?.Summary ?? "上下拨不执行任何动作。"));
        return new(_desired, _preferenceReadable, _source.IsPresent, state == "Active", state, detail, _lastOutcome, _updatedAt, _lastError);
    }

    private static string Summary(NativeMenuCaseOutcome outcome)
    {
        var action = (outcome.ResolvedAction ?? outcome.Intent.Action) switch
        {
            DesiredAction.EnsureOn => "确保亮灯", DesiredAction.EnsureOff => "确保熄灯", _ => "按下切换开/关",
        };
        var state = outcome.State switch
        {
            NativeMenuCaseOutcomeState.Applied => "后台已接受",
            NativeMenuCaseOutcomeState.Unchanged => "已是目标状态，未发送",
            NativeMenuCaseOutcomeState.Failed => "后台返回错误",
            NativeMenuCaseOutcomeState.Unknown => "结果未知，未自动重发",
            _ => "未发送",
        };
        return $"{action}：{state}" + (outcome.Readback is { } readback ? $"；回读：{readback.Title}" : string.Empty)
            + (outcome.Stale ? "（连接已失效，不作为当前反馈）" : string.Empty);
    }

    // Under _sync (or in the constructor).
    private void Record(string entry)
    {
        _updatedAt = _time.GetUtcNow();
        _trace.Enqueue(entry);
        while (_trace.Count > TraceLimit) _trace.Dequeue();
    }

    private sealed record Preference(int Schema, bool Enabled);
    private sealed record Armed(string Session, long Generation);

    private sealed class SourceContext(string session, long generation, long profile)
    {
        public string Session { get; } = session;
        public long Generation { get; } = generation;
        public long Profile { get; } = profile;
        public long HighWater { get; set; }
        public bool Active { get; set; } = true;
    }
}
