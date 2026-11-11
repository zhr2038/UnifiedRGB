using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Hosting;

namespace UnifiedRgb.Host.Services;

internal sealed record HomeAssistantPageView(string Name, string Detail, int Number = 0, int Count = 0, string? ConnectionBadge = null);

/// <summary>Owns HA requests and rendering off the HID actor. The actor only reads pixels and offers validated input.</summary>
internal sealed class KeyboardHomeAssistantPage : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    private readonly HomeAssistantSceneClient _client;
    private readonly TimeProvider _time;
    private readonly Uri? _endpoint;
    private readonly Func<Uri, Func<bool>, CancellationToken, Task<bool>> _openBrowser;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly KeyboardHomeAssistantScene[] _scenes;
    private HomeAssistantSceneState[] _states = [];
    private PublishedFrame? _frame;
    private int _selected; // Home is zero; the configured scenes follow in order.
    private long _entry;
    private bool _visible;
    private string? _connectionStatus = "正在连接";
    private string? _result;
    private DateTimeOffset _resultUntil;
    private PendingAction? _pending;
    private bool _busy, _stopping;

    public KeyboardHomeAssistantPage(KeyboardHomeAssistantOptions options, HomeAssistantSceneClient client, TimeProvider time,
        Func<Uri, Func<bool>, CancellationToken, Task<bool>>? openBrowser = null)
    {
        _client = client;
        _time = time;
        _endpoint = options.Endpoint;
        _openBrowser = openBrowser ?? new WindowsHomeAssistantBrowser().OpenAsync;
        _scenes = options.Scenes.Where(scene => scene.IsValid).DistinctBy(scene => scene.EntityId, StringComparer.Ordinal).Take(24).ToArray();
        Enabled = _endpoint is not null && _scenes.Length > 0;
    }

    public bool Enabled { get; }
    public byte[]? Pixels => Volatile.Read(ref _frame)?.Pixels;

    public void ObservePage(bool visible)
    {
        lock (_sync)
        {
            if (_visible == visible || _stopping) return;
            _visible = visible;
            _entry++;
            if (!visible) return;
            _selected = 0;
            _result = null;
            Volatile.Write(ref _frame, null);
            Wake();
        }
    }

    public void Interact(byte mask, byte[]? acknowledged, Func<bool> stillAdmitted)
    {
        if (acknowledged is null) return; // The entry logo or an uncommitted frame cannot authorize an action.
        lock (_sync)
        {
            if (_stopping || !_visible || _busy || !stillAdmitted()) return;
            if (mask is 1 or 4)
            {
                var count = _states.Length + 1;
                _selected = (_selected + (mask == 1 ? -1 : 1) + count) % count;
                _result = null;
            }
            else if (mask == 2 && _frame is { CanActivate: true } frame && frame.Selection == _selected
                && (_selected == 0 || _connectionStatus is null && _states[_selected - 1].Available)
                && acknowledged.AsSpan().SequenceEqual(frame.Pixels))
            {
                _pending = new(_selected == 0 ? null : _states[_selected - 1].Scene, _entry, stillAdmitted);
                _busy = true;
                _result = null;
            }
            else return;
            Wake();
        }
    }

    // All wake producers use the same lock; changes coalesce while one is pending.
    private void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        await Task.Yield(); // HA availability never gates Host startup.
        DateTimeOffset refreshAt = default;
        Task? refresh = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                PendingAction? pending;
                lock (_sync) { pending = _pending; _pending = null; }
                PublishFrame();
                if (pending is not null)
                {
                    var result = await RunActionAsync(pending, stoppingToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _busy = false;
                        if (pending.Entry == _entry)
                        {
                            _result = result;
                            _resultUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(4);
                        }
                    }
                    PublishFrame();
                    if (pending.Scene is not null) refreshAt = default;
                }
                if (refresh is { IsCompleted: true }) { await refresh.ConfigureAwait(false); refresh = null; }
                if (refresh is null && _time.GetUtcNow() >= refreshAt)
                {
                    // The scene refresh may time out. Opening the browser must still work while it is outstanding.
                    refresh = RefreshAsync(stoppingToken);
                    refreshAt = _time.GetUtcNow() + PollInterval;
                }
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally { if (refresh is not null) await refresh.ConfigureAwait(false); }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var states = await Task.WhenAll(_scenes.Select(scene => _client.ReadAsync(scene, timeout.Token))).ConfigureAwait(false);
            lock (_sync)
            {
                if (_connectionStatus is not null)
                    Serilog.Log.Information("HA keyboard scenes loaded: configured={Configured}, available={Available}", states.Length, states.Count(s => s.Available));
                _states = states;
                _connectionStatus = null;
                Wake();
            }
        }
        catch (HomeAssistantCredentialMissingException) { SetConnectionStatus("未配置 HA 凭据"); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { SetConnectionStatus("HA 认证失败"); }
        catch (Exception ex) when (IsIntegrationFailure(ex)) { SetConnectionStatus("HA 暂不可用"); }
    }

    private void SetConnectionStatus(string value)
    {
        lock (_sync)
        {
            if (_connectionStatus != value) Serilog.Log.Warning("HA keyboard scenes unavailable: {Status}", value);
            _connectionStatus = value;
            Wake();
        }
    }

    private async Task<string> RunActionAsync(PendingAction pending, CancellationToken ct)
    {
        if (pending.Scene is null)
        {
            if (!pending.StillAdmitted() || _endpoint is null) return "已取消";
            return await _openBrowser(_endpoint, pending.StillAdmitted, ct).ConfigureAwait(false) ? "已请求打开网页" : "无法打开网页";
        }
        try { return await _client.ActivateAsync(pending.Scene, pending.StillAdmitted, ct).ConfigureAwait(false); }
        catch (HomeAssistantCredentialMissingException) { return "未配置 HA 凭据"; }
        catch (Exception ex) when (IsIntegrationFailure(ex)) { return "结果未知"; }
    }

    private static bool IsIntegrationFailure(Exception ex) => ex is HttpRequestException or IOException or OperationCanceledException
        or JsonException or InvalidOperationException or KeyNotFoundException or FormatException;

    private void PublishFrame()
    {
        HomeAssistantPageView view;
        int selected;
        long entry;
        bool canActivate;
        lock (_sync)
        {
            selected = _selected;
            entry = _entry;
            if (_result is not null && _time.GetUtcNow() >= _resultUntil) _result = null;
            if (selected == 0)
            {
                canActivate = !_busy;
                var headline = _connectionStatus ?? string.Create(CultureInfo.InvariantCulture, $"{_states.Count(s => s.Available)} 个场景可用");
                var badge = _connectionStatus switch { null => "在线", "正在连接" => "连接中", _ => "离线" };
                view = new(headline, _result ?? "↑↓ 场景　中按打开网页", ConnectionBadge: badge);
            }
            else
            {
                var state = _states[selected - 1];
                canActivate = state.Available && _connectionStatus is null && !_busy;
                var detail = _busy ? "执行中…" : _result ?? _connectionStatus ?? (canActivate ? "↑↓ 选择　中按执行" : "场景暂不可用");
                view = new(state.Name, detail, selected, _states.Length);
            }
            if (_frame is { } old && old.View == view && old.Selection == selected && old.CanActivate == canActivate) return;
        }
        try
        {
            var pixels = Render(view);
            lock (_sync)
                if (_entry == entry && _selected == selected) Volatile.Write(ref _frame, new(view, selected, canActivate, pixels));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Volatile.Write(ref _frame, null);
            Serilog.Log.Warning("HA keyboard artwork unavailable: {FailureType}", ex.GetType().Name);
        }
    }

    internal static byte[] Render(HomeAssistantPageView view) => KeyboardStatusArtwork.Rasterize((dc, _) =>
    {
        KeyboardStatusArtwork.Text(dc, "HA · 家庭场景", 12, 6, 0, 154);
        if (view.Count > 0)
            KeyboardStatusArtwork.Text(dc, string.Create(CultureInfo.InvariantCulture, $"{view.Number}/{view.Count}"), 12, 167, 0, 35);
        else if (view.ConnectionBadge is not null)
            KeyboardStatusArtwork.Text(dc, view.ConnectionBadge, 12, 160, 0, 42);
        dc.DrawLine(new Pen(Brushes.DimGray, 1), new Point(6, 17), new Point(202, 17));
        KeyboardStatusArtwork.Text(dc, view.Name, 21, 6, 19, 196);
        KeyboardStatusArtwork.Text(dc, view.Detail, 13, 6, 46, 196);
    });

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync) { _stopping = true; _pending = null; }
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_stopping && _disposed) return;
            _stopping = _disposed = true;
        }
        base.Dispose();
    }

    private bool _disposed;
    private sealed record PendingAction(KeyboardHomeAssistantScene? Scene, long Entry, Func<bool> StillAdmitted);
    private sealed record PublishedFrame(HomeAssistantPageView View, int Selection, bool CanActivate, byte[] Pixels);
}
