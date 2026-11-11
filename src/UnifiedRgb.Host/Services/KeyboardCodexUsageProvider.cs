using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

/// <summary>Independent cached worker. Neither construction nor a display/status read starts Codex or renders a frame.</summary>
internal sealed class KeyboardCodexUsageProvider : BackgroundService
{
    private readonly object _gate = new();
    private readonly ICodexQuotaClient _client;
    private readonly TimeProvider _time;
    private readonly Func<KeyboardCodexUsageDto, byte[]> _render;
    private readonly string _path;
    private readonly bool _settingsError;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private KeyboardCodexUsageDto _status = new(false, "Off", null, null, null, null, null);
    private byte[]? _pixels;
    private long _revision;
    private CancellationTokenSource? _readStop;
    private DateTimeOffset _nextRead;
    private int _failures;
    private readonly Action<bool>? _requestPage;

    public KeyboardCodexUsageProvider(string dataDirectory, ICodexQuotaClient? client = null, TimeProvider? time = null,
        Func<KeyboardCodexUsageDto, byte[]>? render = null, Action<bool>? requestPage = null)
    {
        _path = Path.Combine(dataDirectory, "peripherals", "keyboard-codex-usage.json");
        _client = client ?? new CodexQuotaClient(); _time = time ?? TimeProvider.System;
        _render = render ?? KeyboardCodexArtwork.Render;
        _requestPage = requestPage;
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 4096) throw new InvalidDataException();
                using var settings = JsonDocument.Parse(File.ReadAllText(_path));
                var root = settings.RootElement;
                if (root.GetProperty("Schema").GetInt32() != 1) throw new InvalidDataException();
                var enabled = root.GetProperty("Enabled").GetBoolean();
                _status = _status with { Enabled = enabled, State = enabled ? "Loading" : "Off" };
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _settingsError = true;
            _status = _status with { State = "SettingsError", Error = "设置损坏：已关闭并保留原文件" };
        }
        _requestPage?.Invoke(_status.Enabled);
    }

    public KeyboardCodexUsageDto Status => Volatile.Read(ref _status);
    // Pure cached callback; only the worker swaps this reference. No rendering, clocks, process or network on the OLED actor.
    public byte[]? Pixels => Volatile.Read(ref _pixels);

    public KeyboardCodexUsageDto SetEnabled(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_settingsError) throw new InvalidOperationException("Codex 设置无法读取，原文件已保留；未覆盖。");
            if (_status.Enabled == enabled) return _status; // repeat requests cannot defeat backoff
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(1, enabled)));
            File.Move(temporary, _path, overwrite: true);
            _revision++; _readStop?.Cancel(); _nextRead = DateTimeOffset.MinValue;
            _status = new(enabled, enabled ? "Loading" : "Off", null, null, null, null, null);
            Volatile.Write(ref _pixels, null);
            _requestPage?.Invoke(enabled);
            _wake.Writer.TryWrite(true);
            return _status;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task? read = null;
        KeyboardCodexUsageDto? rendered = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    ExpireLocked();
                    if (_status.Enabled && (read is null || read.IsCompleted) && _time.GetUtcNow() >= _nextRead)
                    {
                        _readStop?.Dispose();
                        _readStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        // Async read begins outside the lock and on a worker, never in an endpoint or the OLED owner.
                        var revision = _revision; var token = _readStop.Token;
                        read = Task.Run(() => ReadAsync(revision, token), CancellationToken.None);
                        _nextRead = DateTimeOffset.MaxValue;
                    }
                }
                var status = Status;
                var visible = status with { LastSuccess = null };
                if (visible != rendered)
                {
                    byte[]? pixels = null;
                    if (status.Enabled)
                    {
                        try { pixels = _render(status); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { /* No raw details or fallback invented data. */ }
                    }
                    lock (_gate)
                    {
                        if (ReferenceEquals(_status, status))
                        {
                            if (pixels is null || pixels.Length != 6656) Volatile.Write(ref _pixels, null);
                            else if (_pixels is null || !pixels.AsSpan().SequenceEqual(_pixels)) Volatile.Write(ref _pixels, pixels);
                            rendered = visible;
                        }
                    }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    await _wake.Reader.ReadAsync(wait.Token).AsTask().WaitAsync(
                        status.Enabled ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
                finally { wait.Cancel(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate) { _revision++; _readStop?.Cancel(); _requestPage?.Invoke(false); }
            if (read is not null) await read.ConfigureAwait(false); // client joins its own child before completing
            _readStop?.Dispose();
        }
    }

    private async Task ReadAsync(long revision, CancellationToken ct)
    {
        KeyboardCodexUsageDto? answer = null;
        try { answer = await _client.ReadAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Fixed sanitized error below; never log exceptions from CLI. */ }
        lock (_gate)
        {
            if (ct.IsCancellationRequested || revision != _revision || !_status.Enabled) return;
            if (answer is { State: "Ready" })
            {
                _failures = 0;
                _status = answer with { Enabled = true, LastSuccess = _time.GetUtcNow() };
            }
            else
            {
                _failures = Math.Min(_failures + 1, 4);
                _status = _status with { State = _status.LastSuccess is null ? "Unavailable" : "Stale", Error = "读取失败或配额不可用；等待后台重试" };
            }
            ExpireLocked();
            _nextRead = _time.GetUtcNow().AddSeconds(Math.Min(300, 60 * (1 << Math.Max(0, _failures - 1))));
        }
        _wake.Writer.TryWrite(true);
    }

    private void ExpireLocked()
    {
        var now = _time.GetUtcNow();
        var primary = _status.Primary is { } p && p.ResetsAt > now ? p : null;
        var secondary = _status.Secondary is { } s && s.ResetsAt > now ? s : null;
        if (primary != _status.Primary || secondary != _status.Secondary)
            _status = _status with { Primary = primary, Secondary = secondary, State = "Stale", Error = "重置时间已到，旧数值已失效" };
    }

    private sealed record Settings(int Schema, bool Enabled);
}
