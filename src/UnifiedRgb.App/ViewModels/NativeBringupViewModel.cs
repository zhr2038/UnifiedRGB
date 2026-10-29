using System.Collections.ObjectModel;
using System.Globalization;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Preview;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.ViewModels;

/// <summary>One native controller the bring-up expander can target.</summary>
public sealed record BringupTarget(ControllerDto Dto)
{
    public string Id => Dto.Id;

    public string Label => Dto.ControlVerified
        ? $"{Dto.Name} ({Dto.DeviceClass}, {Dto.State})"
        : $"{Dto.Name} ({Dto.DeviceClass}, {Dto.State}, control not verified)";
}

/// <summary>
/// Diagnostics › Native bring-up (NATIVE design §6, WORKORDER-NATIVE-0 scope 9). Every hardware step is an explicit button:
/// nothing is sent on construction, target/probe selection, controller updates or reconnect. The session belongs to this
/// pipe connection; the heartbeat renews only that session, stops on End, a failed renew, a session ended by a failed step,
/// a lost connection and Dispose, and never begins a session by itself. Dispose sends nothing: the Host ends a closed
/// connection's sessions. While the Host reports <c>HoldBlackOnRelease</c> only black markers are sent and the colour is
/// not editable; the default colour is black either way. Results are communication evidence only: control not verified,
/// playback unobserved, optical acceptance pending the owner.
/// </summary>
public sealed class NativeBringupViewModel : ObservableObject, IDisposable
{
    /// <summary>Device classes with a native bring-up adapter.</summary>
    public static readonly string[] NativeClasses = ["MsiGpu", "AsusAura", "EneDram", "AsusRog"];

    /// <summary>Design §6 default lease; the Host clamps to its MaxLeaseSeconds (600).</summary>
    public const int DefaultLeaseSeconds = 120;

    public const int MinLeaseSeconds = 15;

    public const int MaxLeaseSeconds = 600;

    public const string Black = "#000000";

    public const int MaxResults = 50;

    public const string EvidenceNote = "Communication evidence only: control not verified, playback unobserved, optical acceptance pending the owner.";

    private readonly IHostConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _onError;
    private readonly TimeProvider _time;
    private BringupTarget? _target;
    private BringupSessionDto? _session;
    private int _sessionConnection;
    private int _generation;
    private ITimer? _heartbeat;
    private int _leaseSeconds = DefaultLeaseSeconds;
    private string? _probe;
    private int _markerStart;
    private int _markerCount = 1;
    private int _markerChannel;
    private string _markerColor = Black;
    private string _sessionText = "No session.";
    private bool _disposed;

    public NativeBringupViewModel(IHostConnection connection, IUiDispatcher ui, Action<Exception> onError, TimeProvider? time = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _time = time ?? TimeProvider.System;
        BeginCommand = new AsyncCommand(_ => BeginAsync(), onError, _ => CanBegin);
        RunProbeCommand = new AsyncCommand(_ => RunProbeAsync(), onError, _ => HasSession && _probe is not null);
        MarkerCommand = new AsyncCommand(_ => MarkerAsync(), onError, _ => HasSession);
        EndMarkerCommand = new AsyncCommand(_ => EndMarkerAsync(), onError, _ => HasSession);
        RenewCommand = new AsyncCommand(_ => RenewAsync(), onError, _ => HasSession);
        EndCommand = new AsyncCommand(_ => EndAsync(), onError, _ => HasSession);
        StepBackCommand = new RelayCommand(_ => MarkerStart = Math.Max(0, _markerStart - _markerCount));
        StepForwardCommand = new RelayCommand(_ => MarkerStart = _markerStart + _markerCount);
        _connection.StateChanged += OnStateChanged;
    }

    public ObservableCollection<BringupTarget> Targets { get; } = [];

    public ObservableCollection<string> Probes { get; } = [];

    /// <summary>Newest first, at most <see cref="MaxResults"/>.</summary>
    public ObservableCollection<string> Results { get; } = [];

    public AsyncCommand BeginCommand { get; }

    public AsyncCommand RunProbeCommand { get; }

    public AsyncCommand MarkerCommand { get; }

    public AsyncCommand EndMarkerCommand { get; }

    public AsyncCommand RenewCommand { get; }

    public AsyncCommand EndCommand { get; }

    /// <summary>Moves the marker range back by its count; local only, nothing is sent until Marker is pressed.</summary>
    public RelayCommand StepBackCommand { get; }

    /// <summary>Moves the marker range forward by its count; local only, nothing is sent until Marker is pressed.</summary>
    public RelayCommand StepForwardCommand { get; }

    public BringupTarget? SelectedTarget
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                OnPropertyChanged(nameof(TargetText));
                RaiseCommands();
            }
        }
    }

    public int LeaseSeconds
    {
        get => _leaseSeconds;
        set => SetProperty(ref _leaseSeconds, Math.Clamp(value, MinLeaseSeconds, MaxLeaseSeconds));
    }

    public BringupSessionDto? Session
    {
        get => _session;
        private set
        {
            if (SetProperty(ref _session, value))
            {
                OnPropertyChanged(nameof(HasSession));
                OnPropertyChanged(nameof(HoldBlack));
                OnPropertyChanged(nameof(CanEditColor));
                if (HoldBlack)
                {
                    MarkerColor = Black;
                }

                RaiseCommands();
            }
        }
    }

    public bool HasSession => _session is not null;

    /// <summary>The Host's HoldBlackOnRelease for this session: only black markers, colour not editable.</summary>
    public bool HoldBlack => _session?.HoldBlackOnRelease == true;

    public bool CanEditColor => HasSession && !HoldBlack;

    public bool HeartbeatRunning => _heartbeat is not null;

    public bool CanBegin => !_disposed && _connection.State == ConnectionState.Connected && _target is not null && _session is null
        && _target.Dto.State != ControllerStates.BringUp;

    public string? SelectedProbe
    {
        get => _probe;
        set
        {
            if (SetProperty(ref _probe, value))
            {
                RaiseCommands();
            }
        }
    }

    public int MarkerStart
    {
        get => _markerStart;
        set => SetProperty(ref _markerStart, Math.Max(0, value));
    }

    public int MarkerCount
    {
        get => _markerCount;
        set => SetProperty(ref _markerCount, Math.Max(1, value));
    }

    public int MarkerChannel
    {
        get => _markerChannel;
        set => SetProperty(ref _markerChannel, Math.Max(0, value));
    }

    /// <summary>"#RRGGBB"; black by default and forced black while <see cref="HoldBlack"/>.</summary>
    public string MarkerColor
    {
        get => _markerColor;
        set => SetProperty(ref _markerColor, HoldBlack ? Black : value ?? Black);
    }

    public string SessionText
    {
        get => _sessionText;
        private set => SetProperty(ref _sessionText, value);
    }

    /// <summary>State of the selected target, with an honest gate label (never "close a process").</summary>
    public string TargetText => _target is null ? "Choose a native controller."
        : _target.Dto.State == ControllerStates.BringUp && _session?.ControllerId != _target.Id ? $"In another bring-up session ({_target.Dto.BlockedBy}); not available."
        : !_target.Dto.ControlVerified ? $"Control not verified ({_target.Dto.BlockedBy}): no scene frames reach it; only this session's explicit steps do. The class's Takeover must be on to begin."
        : $"{_target.Dto.State}: control verified by its class gate.";

    /// <summary>Heartbeat period for a lease: a third of it, at least one second.</summary>
    public static TimeSpan HeartbeatPeriod(int leaseSeconds) => TimeSpan.FromSeconds(Math.Max(1, leaseSeconds / 3.0));

    /// <summary>Keeps the native controllers only; the selection survives by id. Sends nothing.</summary>
    public void SetControllers(IReadOnlyList<ControllerDto> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        var keep = _target?.Id ?? _session?.ControllerId;
        Targets.Clear();
        foreach (var c in controllers.Where(c => NativeClasses.Contains(c.DeviceClass, StringComparer.OrdinalIgnoreCase)))
        {
            Targets.Add(new BringupTarget(c));
        }

        _target = Targets.FirstOrDefault(t => t.Id == keep);
        OnPropertyChanged(nameof(SelectedTarget));
        RaiseCommands();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.StateChanged -= OnStateChanged;
        _generation++;
        StopHeartbeat();
        _session = null;
    }

    private async Task BeginAsync()
    {
        if (!CanBegin)
        {
            throw new InvalidOperationException("Begin needs a connected Host, a native controller that no session holds, and no running session.");
        }

        var target = _target!;
        var connection = _connection.Connections;
        var s = await _connection.Require().BeginBringupAsync(target.Id, _leaseSeconds).ConfigureAwait(true);
        if (_disposed || _connection.Connections != connection || _connection.State != ConnectionState.Connected)
        {
            AddResult($"{target.Id}: the connection changed while beginning; the Host ended that connection's session.");
            return;
        }

        _generation++;
        _sessionConnection = connection;
        Session = s;
        Probes.Clear();
        foreach (var p in s.Probes)
        {
            Probes.Add(p);
        }

        SelectedProbe = Probes.FirstOrDefault();
        StartHeartbeat();
        UpdateSessionText();
        AddResult($"{s.ControllerId}: session begun, lease {s.LeaseSeconds} s{(s.HoldBlackOnRelease ? ", hold-black (black markers only)" : string.Empty)}. Opening sends no mode change.");
    }

    private async Task RunProbeAsync()
    {
        var s = RequireSession();
        var probe = _probe ?? throw new InvalidOperationException("Choose a probe.");
        var generation = _generation;
        Record(await _connection.Require().RunNativeProbeAsync(s.ControllerId, probe).ConfigureAwait(true), generation);
    }

    private async Task MarkerAsync()
    {
        var s = RequireSession();
        var rgb = HoldBlack ? Black : ColorText.TryParse(_markerColor, out var c) ? ColorText.Format(c) : throw new FormatException($"'{_markerColor}' is not #RRGGBB.");
        var generation = _generation;
        Record(await _connection.Require().BeginMarkerAsync(new MarkerDto(s.ControllerId, _markerStart, _markerCount, rgb, _markerChannel)).ConfigureAwait(true), generation);
    }

    private async Task EndMarkerAsync()
    {
        var s = RequireSession();
        var generation = _generation;
        Record(await _connection.Require().EndMarkerAsync(s.ControllerId).ConfigureAwait(true), generation);
    }

    private async Task RenewAsync()
    {
        var s = RequireSession();
        var generation = _generation;
        var renewed = await _connection.Require().RenewBringupAsync(s.ControllerId).ConfigureAwait(true);
        if (generation == _generation && !_disposed)
        {
            Session = renewed;
            StartHeartbeat();
            UpdateSessionText();
        }
    }

    private async Task EndAsync()
    {
        var s = RequireSession();
        var generation = _generation;
        StopHeartbeat();
        BringupEndDto r;
        try
        {
            r = await _connection.Require().EndBringupAsync(s.ControllerId).ConfigureAwait(true);
        }
        catch
        {
            if (generation == _generation && !_disposed)
            {
                // the session may still run in the Host: keep it so End can be pressed again; without the heartbeat its lease lapses
                UpdateSessionText();
                AddResult(string.Create(CultureInfo.InvariantCulture, $"{s.ControllerId}: End failed; heartbeat stopped. Press End again, or the lease expires at {s.ExpiresAt.ToLocalTime():HH:mm:ss}."));
            }

            throw;
        }

        if (generation == _generation && !_disposed)
        {
            ResetSession(null);
        }

        AddResult(r.AlreadyEnded ? $"{s.ControllerId}: no session was running (already ended)."
            : r.Release is { } release ? Describe(release) : $"{s.ControllerId}: session ended.");
    }

    private void Record(ProbeResultDto r, int generation)
    {
        AddResult(Describe(r));
        if (r.SessionEnded && generation == _generation && _session?.ControllerId == r.ControllerId)
        {
            ResetSession($"{r.ControllerId}: the failed step released and ended the session; nothing is retried.");
        }
    }

    internal static string Describe(ProbeResultDto r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var fields = r.Fields.Count == 0 ? string.Empty : " [" + string.Join(", ", r.Fields.Select(kv => $"{kv.Key}={kv.Value}")) + "]";
        return $"{r.ControllerId} {r.Probe}: {(r.Ok ? "ok" : "failed")} — {r.Summary}{fields}";
    }

    private BringupSessionDto RequireSession()
    {
        if (_session is not { } s || _disposed)
        {
            throw new InvalidOperationException("No bring-up session on this connection: Begin first.");
        }

        if (_connection.State != ConnectionState.Connected || _connection.Connections != _sessionConnection)
        {
            ResetSession("Host connection lost: the Host ended this connection's session.");
            throw new InvalidOperationException("The session ended with the connection that began it; Begin again.");
        }

        return s;
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        if (_session is not { } s)
        {
            return;
        }

        var generation = _generation;
        var period = HeartbeatPeriod(s.LeaseSeconds);
        _heartbeat = _time.CreateTimer(_ => _ui.Post(() => _ = HeartbeatAsync(generation)), null, period, period);
        OnPropertyChanged(nameof(HeartbeatRunning));
    }

    private void StopHeartbeat()
    {
        if (_heartbeat is null)
        {
            return;
        }

        _heartbeat.Dispose();
        _heartbeat = null;
        OnPropertyChanged(nameof(HeartbeatRunning));
    }

    private async Task HeartbeatAsync(int generation)
    {
        if (generation != _generation || _session is not { } s || _disposed)
        {
            return;
        }

        if (_connection.State != ConnectionState.Connected || _connection.Connections != _sessionConnection)
        {
            ResetSession("Host connection lost: the Host ended this connection's session; the heartbeat stopped.");
            return;
        }

        try
        {
            var renewed = await _connection.Require().RenewBringupAsync(s.ControllerId).ConfigureAwait(true);
            if (generation == _generation && !_disposed)
            {
                Session = renewed;
                UpdateSessionText();
            }
        }
#pragma warning disable CA1031 // heartbeat boundary: report, stop, never retry
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                StopHeartbeat();
                UpdateSessionText();
                AddResult($"{s.ControllerId}: renew failed, heartbeat stopped (no retry). End the session or Renew by hand.");
            }

            _onError(ex);
        }
    }

    private void OnStateChanged(object? sender, ConnectionState state) => _ui.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (_session is not null && (state != ConnectionState.Connected || _connection.Connections != _sessionConnection))
        {
            ResetSession("Host connection lost: the Host ended this connection's session; nothing begins again by itself.");
        }

        OnPropertyChanged(nameof(CanBegin));
        RaiseCommands();
    });

    private void ResetSession(string? reason)
    {
        _generation++;
        StopHeartbeat();
        Session = null;
        Probes.Clear();
        SelectedProbe = null;
        MarkerColor = Black;
        UpdateSessionText();
        if (reason is not null)
        {
            AddResult(reason);
        }
    }

    private void UpdateSessionText() => SessionText = _session is not { } s ? "No session."
        : string.Create(CultureInfo.InvariantCulture, $"Session on {s.ControllerId}: lease {s.LeaseSeconds} s, expires {s.ExpiresAt.ToLocalTime():HH:mm:ss}")
          + (HeartbeatRunning ? ", renewed by this window" : ", heartbeat stopped")
          + (s.HoldBlackOnRelease ? " · hold-black: black markers only" : string.Empty);

    private void AddResult(string line)
    {
        Results.Insert(0, string.Create(CultureInfo.InvariantCulture, $"{_time.GetLocalNow():HH:mm:ss} {line}"));
        while (Results.Count > MaxResults)
        {
            Results.RemoveAt(Results.Count - 1);
        }
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanBegin));
        OnPropertyChanged(nameof(TargetText));
        BeginCommand.RaiseCanExecuteChanged();
        RunProbeCommand.RaiseCanExecuteChanged();
        MarkerCommand.RaiseCanExecuteChanged();
        EndMarkerCommand.RaiseCanExecuteChanged();
        RenewCommand.RaiseCanExecuteChanged();
        EndCommand.RaiseCanExecuteChanged();
    }
}
