using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

/// <summary>A discovered ROG keyboard controller; the same instance is kept across refreshes so a selection survives them.</summary>
public sealed class PerKeyCalibrationKeyboard(string id, string name) : ObservableObject
{
    private string _name = name;

    public string Id { get; } = id;

    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }
}

/// <summary>One explicit lighting route of the shared keyboard actor. USB and 2.4G are calibrated and exported separately.</summary>
public sealed record PerKeyCalibrationRoute(string Transport, string Label)
{
    public const string UsbTransport = "rog-usb65-c081-v1";
    public const string RfTransport = "rog-rf64-c081-v1";

    public static IReadOnlyList<PerKeyCalibrationRoute> All { get; } = [new(UsbTransport, "USB 有线"), new(RfTransport, "2.4G 无线")];

    public static string LabelOf(string? transport) =>
        All.FirstOrDefault(r => r.Transport == transport)?.Label ?? (string.IsNullOrWhiteSpace(transport) ? "未知" : transport);
}

/// <summary>The exact controller, route and raw LED that one successful marker lit.</summary>
public sealed record PerKeyMarkerSelection(string ControllerId, string Transport, int LedId);

/// <summary>
/// One key the owner explicitly confirmed lit for a successful marker. Controller, route and LED are the marker's own and fixed;
/// the observed name and position stay editable until export.
/// </summary>
public sealed class PerKeyObservedKey(string controllerId, string transport, int ledId, string key, int row, int column, DateTimeOffset observedAt)
    : ObservableObject
{
    private string _key = key;
    private int _row = row;
    private int _column = column;

    public string ControllerId { get; } = controllerId;
    public string Transport { get; } = transport;
    public int LedId { get; } = ledId;
    public DateTimeOffset ObservedAt { get; } = observedAt;
    public string RouteLabel => PerKeyCalibrationRoute.LabelOf(Transport);
    public string LedText => string.Create(CultureInfo.InvariantCulture, $"LED {LedId}");

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value ?? string.Empty);
    }

    public int Row
    {
        get => _row;
        set => SetProperty(ref _row, value);
    }

    public int Column
    {
        get => _column;
        set => SetProperty(ref _column, value);
    }
}

/// <summary>
/// Small per-key calibration on the Devices page. One button lights exactly one raw LED red for about two seconds through the
/// existing leased bring-up marker of the shared keyboard actor, after checking that the actor's actual route (the
/// per-key-status probe) is the route the owner selected, and restores and releases in finally. A separate explicit button
/// records what the owner saw for that same marker; a successful send alone confirms nothing. Export only formats the observed
/// subset as a settings fragment in a read-only text box: no file, setting, scene or Host is changed, and nothing touches
/// hardware at startup, on selection or on export.
/// </summary>
public sealed class PerKeyCalibrationViewModel : ObservableObject
{
    public const int MaximumLedId = 127;
    /// <summary>The adapter's per-key-led marker channel (WiredKeyboardAdapter.PerKeyLedMarkerChannel).</summary>
    public const int MarkerChannel = 1;
    public const string MarkerColor = "#FF0000";
    public const int LeaseSeconds = 30;
    public const string StatusProbe = "per-key-status";
    public const string TransportField = "transport";
    public const string Intro =
        "逐颗点亮原始 LED，确认实际灯珠后再记录。一个按键内的多颗灯珠分别命名和控制，例如空格左端、左中、正中、右中、右端，不自动合并同色。USB 与 2.4G 分别校准；行/列为本机位置。";
    public const string NextStep =
        "下一步：把所选观察子集合并到现有设置 UnifiedRgb:AsusRog:KeyboardPerKey 并重启后台；之后现有场景编辑器会提供该键盘的逐键分区（仅含已观察按键），可在共享时间轴上编辑普通灯效。未导出映射的路由保持整键盘单色；完整键位布局需后续继续扫描。";

    public static readonly TimeSpan HighlightDuration = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IHostConnection _connection;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private PerKeyCalibrationKeyboard? _keyboard;
    private PerKeyCalibrationRoute? _route;
    private int _ledId;
    private string _keyName = string.Empty;
    private int _row;
    private int _column;
    private bool _busy;
    private PerKeyMarkerSelection? _lastMarker;
    private string _status = "选择键盘与传输后点亮一个 LED。";
    private string _exportText = string.Empty;
    private string _exportSummary = "尚未生成导出片段。";

    /// <param name="delay">How the lit marker is held (tests inject it; the default waits for real).</param>
    public PerKeyCalibrationViewModel(IHostConnection connection, Action<Exception> onError,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(onError);
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _time = time ?? TimeProvider.System;
        HighlightCommand = new AsyncCommand(_ => HighlightAsync(), onError, _ => !_busy && Current() is not null);
        RecordCommand = new AsyncCommand(_ => { Record(); return Task.CompletedTask; }, onError, _ => !_busy && _lastMarker is not null);
        ExportCommand = new AsyncCommand(_ => { Export(); return Task.CompletedTask; }, onError, _ => !_busy);
        RemoveCommand = new AsyncCommand(p => { Remove(p as PerKeyObservedKey); return Task.CompletedTask; }, onError, _ => !_busy);
    }

    public ObservableCollection<PerKeyCalibrationKeyboard> Keyboards { get; } = [];
#pragma warning disable CA1822 // instance member for XAML binding
    public IReadOnlyList<PerKeyCalibrationRoute> Routes => PerKeyCalibrationRoute.All;
#pragma warning restore CA1822
    public ObservableCollection<PerKeyObservedKey> Observed { get; } = [];

    public AsyncCommand HighlightCommand { get; }
    public AsyncCommand RecordCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand RemoveCommand { get; }

    public PerKeyCalibrationKeyboard? SelectedKeyboard
    {
        get => _keyboard;
        set { if (SetProperty(ref _keyboard, value)) SelectionChanged(); }
    }

    /// <summary>The explicit route; nothing is selected by default, so a marker never runs on a guessed transport.</summary>
    public PerKeyCalibrationRoute? SelectedRoute
    {
        get => _route;
        set { if (SetProperty(ref _route, value)) SelectionChanged(); }
    }

    public int LedId
    {
        get => _ledId;
        set { if (SetProperty(ref _ledId, value)) SelectionChanged(); }
    }

    public string KeyName
    {
        get => _keyName;
        set => SetProperty(ref _keyName, value ?? string.Empty);
    }

    public int Row
    {
        get => _row;
        set => SetProperty(ref _row, value);
    }

    public int Column
    {
        get => _column;
        set => SetProperty(ref _column, value);
    }

    /// <summary>One marker, record, export or removal at a time; selection inputs are disabled meanwhile.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RaiseCommands();
        }
    }

    public bool IsIdle => !_busy;

    /// <summary>The selection of the last successful marker that may still be recorded; null after any selection change.</summary>
    public PerKeyMarkerSelection? LastMarker => _lastMarker;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ExportText
    {
        get => _exportText;
        private set => SetProperty(ref _exportText, value);
    }

    public string ExportSummary
    {
        get => _exportSummary;
        private set => SetProperty(ref _exportSummary, value);
    }

    /// <summary>Lists the discovered ASUS ROG keyboards. Local only: no request is made and the selection is kept by id.</summary>
    public void SetControllers(IReadOnlyList<ControllerDto> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        var found = controllers.Where(c => c.DeviceClass == "AsusRog" && c.DeviceType == "Keyboard" && !DisplayNames.IsDiscoveryPort(c))
            .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        foreach (var gone in Keyboards.Where(k => found.All(c => c.Id != k.Id)).ToArray()) Keyboards.Remove(gone);
        foreach (var controller in found)
        {
            var name = $"{DisplayNames.Controller(controller.Id, controller.Name)} · {controller.Id}";
            if (Keyboards.FirstOrDefault(k => k.Id == controller.Id) is { } known) known.Name = name;
            else Keyboards.Add(new(controller.Id, name));
        }
        if (_keyboard is null || !Keyboards.Contains(_keyboard)) SelectedKeyboard = Keyboards.FirstOrDefault();
        HighlightCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Lights the selected raw LED red for <see cref="HighlightDuration"/> and restores. True only when it lit and both the marker
    /// restore and the session release reported success; every other outcome is stated in <see cref="Status"/>.
    /// </summary>
    public async Task<bool> HighlightAsync()
    {
        RequireIdle();
        var selection = Current() ?? throw new InvalidOperationException("请先选择键盘与传输（USB 或 2.4G）。");
        if (selection.LedId is < 0 or > MaximumLedId)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"LED 编号必须在 0–{MaximumLedId} 之间。"));
        var service = _connection.Require();
        ForgetMarker();
        Busy = true;
        try
        {
            var lit = await LightAsync(service, selection).ConfigureAwait(true);
            if (lit is null) return false;
            if (Current() == selection)
            {
                _lastMarker = selection;
                OnPropertyChanged(nameof(LastMarker));
            }
            else lit = (false, lit.Value.Report + " 点亮期间所选键盘、传输或 LED 已改变：本次标记不可记录，请重新点亮。");
            Status = lit.Value.Report;
            return lit.Value.Restored;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Records the owner's observation for the last successful marker: its own controller, route and LED plus the entered key
    /// name and row/column. Refused when the selection changed after the marker, and for a duplicate LED, name or position on
    /// the same controller and route.
    /// </summary>
    public PerKeyObservedKey Record()
    {
        RequireIdle();
        var marker = _lastMarker ?? throw new InvalidOperationException("没有可记录的成功标记：请先点亮所选 LED 并观察实际亮起的按键。");
        if (Current() != marker)
        {
            ForgetMarker();
            throw new InvalidOperationException("所选键盘、传输或 LED 已在点亮后改变，未记录。请重新点亮后再记录。");
        }
        var name = _keyName.Trim();
        if (name.Length == 0) throw new InvalidOperationException("请填写实际亮起的按键名称。");
        if (_row < 0 || _column < 0) throw new InvalidOperationException("行与列必须为非负整数。");
        var route = Observed.Where(o => o.ControllerId == marker.ControllerId && o.Transport == marker.Transport).ToArray();
        if (route.FirstOrDefault(o => o.LedId == marker.LedId) is { } sameLed)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"该路由的 LED {marker.LedId} 已记录为“{sameLed.Key}”，请先删除该行。"));
        if (route.Any(o => o.Key == name)) throw new InvalidOperationException($"该路由已记录按键“{name}”，请先删除或修改该行。");
        if (route.FirstOrDefault(o => o.Row == _row && o.Column == _column) is { } samePlace)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"该路由的行 {_row} 列 {_column} 已被“{samePlace.Key}”占用。"));

        var item = new PerKeyObservedKey(marker.ControllerId, marker.Transport, marker.LedId, name, _row, _column, _time.GetUtcNow());
        item.PropertyChanged += ObservedEdited;
        Observed.Add(item);
        ForgetMarker();
        Status = string.Create(CultureInfo.InvariantCulture,
            $"已记录：{item.RouteLabel} · LED {item.LedId} = {item.Key}（行 {item.Row}，列 {item.Column}）。");
        ExportStale();
        return item;
    }

    public void Remove(PerKeyObservedKey? item)
    {
        RequireIdle();
        if (item is null || !Observed.Remove(item)) return;
        item.PropertyChanged -= ObservedEdited;
        Status = string.Create(CultureInfo.InvariantCulture, $"已删除：{item.RouteLabel} · LED {item.LedId} = {item.Key}。");
        ExportStale();
    }

    /// <summary>
    /// Formats the selected keyboard's observed keys as a local settings fragment: one map per route that has observations,
    /// holding only those keys. Complete/Verified/KeyOrderVerified state that this observed subset is confirmed, never that
    /// every physical key is. Invalid or empty input yields no fragment. Writes nothing anywhere.
    /// </summary>
    public string Export()
    {
        RequireIdle();
        Busy = true;
        try
        {
            ExportText = string.Empty;
            try
            {
                var (text, summary) = Format();
                ExportText = text;
                ExportSummary = summary;
                return text;
            }
            catch (InvalidOperationException ex)
            {
                ExportSummary = ex.Message;
                throw;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private (string Text, string Summary) Format()
    {
        var keyboard = _keyboard ?? throw new InvalidOperationException("请先选择键盘；未生成导出片段。");
        var rows = Observed.Where(o => o.ControllerId == keyboard.Id).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("该键盘没有已记录的观察按键；不会生成任何映射。");

        var maps = new JsonArray();
        var parts = new List<string>();
        var shapes = new List<HashSet<(string, int, int)>>();
        foreach (var route in PerKeyCalibrationRoute.All)
        {
            var keys = rows.Where(r => r.Transport == route.Transport).OrderBy(r => r.Row).ThenBy(r => r.Column).ToArray();
            if (keys.Length == 0) continue;
            if (Invalid(keys) is { } problem) throw new InvalidOperationException($"{route.Label}：{problem}；未生成导出片段。");
            maps.Add(Map(route, keys));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{route.Label} {keys.Length} 键（{string.Join("、", keys.Select(k => k.Key))}）"));
            shapes.Add(keys.Select(k => (k.Key.Trim(), k.Row, k.Column)).ToHashSet());
        }

        var root = new JsonObject
        {
            ["UnifiedRgb"] = new JsonObject
            {
                ["AsusRog"] = new JsonObject
                {
                    ["KeyboardPerKey"] = new JsonObject { ["Enabled"] = true, ["Maps"] = maps },
                },
            },
        };
        var summary = $"观察子集：{string.Join("；", parts)}。只包含逐一点亮并确认的按键，不是整把键盘；本片段只供合并到现有设置，未写入任何文件或设置。";
        if (shapes.Count == 2 && !shapes[0].SetEquals(shapes[1]))
            summary += " 注意：USB 与 2.4G 子集的按键或位置不同，后台在两条路由间切换时会阻止逐键输出，直到两者一致。";
        return (root.ToJsonString(ExportJson), summary);
    }

    private static JsonObject Map(PerKeyCalibrationRoute route, PerKeyObservedKey[] keys)
    {
        var first = keys.Min(k => k.ObservedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = keys.Max(k => k.ObservedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dates = first == last ? first : $"{first}..{last}";
        var cells = new JsonArray();
#pragma warning disable CA1507 // settings keys of RogPerKeyCellOptions, not this type's member names
        foreach (var key in keys)
            cells.Add(new JsonObject { ["LedId"] = key.LedId, ["Key"] = key.Key.Trim(), ["Row"] = key.Row, ["Column"] = key.Column });
#pragma warning restore CA1507
        return new JsonObject
        {
            ["Model"] = string.Create(CultureInfo.InvariantCulture, $"ROG Azoth X observed subset ({keys.Length} key(s), {route.Transport}, not full keyboard)"),
            ["Transport"] = route.Transport,
            ["Complete"] = true,
            ["Verified"] = true,
            ["Evidence"] = string.Create(CultureInfo.InvariantCulture,
                $"Owner-confirmed one-LED red markers on {route.Transport}, Devices calibration panel, {dates}: {keys.Length} observed key(s) ({string.Join(", ", keys.Select(k => k.Key.Trim()))}). Complete/Verified/KeyOrderVerified cover only this observed subset, not every physical key."),
            ["KeyOrderVerified"] = true,
            ["Keys"] = cells,
        };
    }

    private static string? Invalid(PerKeyObservedKey[] keys)
    {
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var places = new HashSet<(int, int)>();
        foreach (var key in keys)
        {
            var name = key.Key.Trim();
            if (key.LedId is < 0 or > MaximumLedId) return string.Create(CultureInfo.InvariantCulture, $"LED {key.LedId} 超出 0–{MaximumLedId}");
            if (name.Length == 0) return string.Create(CultureInfo.InvariantCulture, $"LED {key.LedId} 缺少按键名称");
            if (key.Row < 0 || key.Column < 0) return $"按键“{name}”的行/列为负数";
            if (!ids.Add(key.LedId)) return string.Create(CultureInfo.InvariantCulture, $"LED {key.LedId} 重复");
            if (!names.Add(name)) return $"按键名称“{name}”重复";
            if (!places.Add((key.Row, key.Column))) return string.Create(CultureInfo.InvariantCulture, $"行 {key.Row} 列 {key.Column} 重复");
        }
        return null;
    }

    // Begin → status probe (actual route) → one red marker → hold → end marker → end session. Returns null when nothing lit.
    private async Task<(bool Restored, string Report)?> LightAsync(IRgbService service, PerKeyMarkerSelection selection)
    {
        var id = selection.ControllerId;
        var where = string.Create(CultureInfo.InvariantCulture, $"{PerKeyCalibrationRoute.LabelOf(selection.Transport)} · LED {selection.LedId}");
        Status = $"正在点亮 {where} …";
        BringupSessionDto session;
        try
        {
            session = await service.BeginBringupAsync(id, LeaseSeconds, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"未能开始校准会话，未发送标记：{ex.Message}";
            throw;
        }

        var markerSent = false;
        var ended = false;
        var lit = false;
        string? refusal = null;
        var cleanup = new List<string>();
        try
        {
            try
            {
                var probe = await service.RunNativeProbeAsync(id, StatusProbe, CancellationToken.None).ConfigureAwait(true);
                ended = probe.SessionEnded;
                string? actual = null;
                if (probe.Fields is { } fields && fields.TryGetValue(TransportField, out var reported)) actual = reported;
                if (!probe.Ok) refusal = $"状态探测未成功，未发送标记：{probe.Summary}";
                else if (session.HoldBlackOnRelease) refusal = "后台当前设置为释放后保持黑色，只接受黑色标记；未发送红色标记。";
                else if (actual != selection.Transport)
                    refusal = $"所选传输为 {PerKeyCalibrationRoute.LabelOf(selection.Transport)}，但后台当前实际路由为 {PerKeyCalibrationRoute.LabelOf(actual)}；未发送标记。请选择实际路由后重试。";
                else
                {
                    markerSent = true;
                    var marker = await service.BeginMarkerAsync(new MarkerDto(id, selection.LedId, 1, MarkerColor, MarkerChannel), CancellationToken.None)
                        .ConfigureAwait(true);
                    ended |= marker.SessionEnded;
                    if (!marker.Ok) refusal = $"标记未成功：{marker.Summary}{(marker.SessionEnded ? "（后台已结束会话并执行释放）" : string.Empty)}";
                    else
                    {
                        lit = true;
                        await _delay(HighlightDuration, CancellationToken.None).ConfigureAwait(true);
                    }
                }
            }
            finally
            {
                await CleanupAsync(service, id, markerSent && !ended, cleanup).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Status = Join($"点亮 {where} 失败：{ex.Message}", cleanup);
            throw;
        }

        if (!lit)
        {
            Status = Join(refusal ?? $"未点亮 {where}。", cleanup);
            return null;
        }
        return cleanup.Count == 0
            ? (true, $"已点亮 {where}（红色约 2 秒）并已恢复当前场景。请确认实际亮起的按键，填写按键名与行/列后点击“记录观察到的按键”。")
            : (false, Join($"已点亮 {where}，但恢复或释放未成功。", cleanup) + " 如确认亮起的按键，仍可记录。");
    }

#pragma warning disable CA1031 // Cleanup states every failure and still ends the session; nothing is retried or hidden.
    private static async Task CleanupAsync(IRgbService service, string id, bool endMarker, List<string> problems)
    {
        if (endMarker)
        {
            try
            {
                var restored = await service.EndMarkerAsync(id, CancellationToken.None).ConfigureAwait(true);
                if (!restored.Ok) problems.Add($"恢复标记失败：{restored.Summary}");
            }
            catch (Exception ex)
            {
                problems.Add($"恢复标记失败：{ex.Message}");
            }
        }
        try
        {
            var end = await service.EndBringupAsync(id, CancellationToken.None).ConfigureAwait(true);
            if (end.Release is { Ok: false } release) problems.Add($"释放会话失败：{release.Summary}");
        }
        catch (Exception ex)
        {
            problems.Add($"释放会话失败：{ex.Message}");
        }
    }
#pragma warning restore CA1031

    private static string Join(string head, List<string> problems) => problems.Count == 0 ? head : head + " " + string.Join("；", problems) + "。";

    private PerKeyMarkerSelection? Current() =>
        _keyboard is { } keyboard && _route is { } route ? new(keyboard.Id, route.Transport, _ledId) : null;

    private void SelectionChanged()
    {
        if (_lastMarker is not null && Current() != _lastMarker)
        {
            ForgetMarker();
            Status = "所选键盘、传输或 LED 已改变：上一次标记不可再记录，请重新点亮。";
        }
        HighlightCommand.RaiseCanExecuteChanged();
    }

    private void ForgetMarker()
    {
        _lastMarker = null;
        OnPropertyChanged(nameof(LastMarker));
        RecordCommand.RaiseCanExecuteChanged();
    }

    private void ObservedEdited(object? sender, PropertyChangedEventArgs e) => ExportStale();

    private void ExportStale()
    {
        if (_exportText.Length == 0) return;
        ExportText = string.Empty;
        ExportSummary = "观察记录已改变，请重新生成导出片段。";
    }

    private void RequireIdle()
    {
        if (_busy) throw new InvalidOperationException("逐键校准正在进行，请等待当前操作结束。");
    }

    private void RaiseCommands()
    {
        HighlightCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }
}
