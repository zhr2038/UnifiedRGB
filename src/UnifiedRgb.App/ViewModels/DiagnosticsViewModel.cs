using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using UnifiedRgb.App.Diagnostics;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.ViewModels;

/// <summary>
/// Clock fps/jitter, per-device writer statistics (from the Host's StatusChanged; native rows add complete-frame p50/p99,
/// EffectiveFps, its source and budget skips), running writer candidates (informational), the Native bring-up expander and
/// the diagnostics zip export.
/// </summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Options = ContractsJson.Configure(new JsonSerializerOptions { WriteIndented = true });

    private readonly IUiDispatcher _ui;
    private readonly string _hostDataDirectory;
    private readonly string _appLogDirectory;
    private readonly Func<Task<StatusDto>>? _fetchDiagnostics;
    private string _clock = "—";
    private string _uptime = "—";
    private string _exportResult = string.Empty;
    private string _writerCandidates = string.Empty;
    private DeviceStatusDto? _selectedDevice;
    private string _clockChinese = "等待后台时钟";
    public string ClockChinese { get => _clockChinese; private set => SetProperty(ref _clockChinese, value); }
    public DeviceStatusDto? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetProperty(ref _selectedDevice, value)) OnPropertyChanged(nameof(SelectedName)); }
    }
    public string SelectedName => SelectedDevice is { } d ? DisplayNames.Controller(d.Id, d.Name) : "选择设备查看详情";

    /// <param name="fetchDiagnostics">GetStatus(diagnostics: true) on the Host; its packet ring goes into the zip as packets.json.</param>
    /// <param name="bringup">The Native bring-up expander (null: not shown).</param>
    public DiagnosticsViewModel(IUiDispatcher ui, string hostDataDirectory, string appLogDirectory, Action<Exception> onError, Func<Task<StatusDto>>? fetchDiagnostics = null,
        NativeBringupViewModel? bringup = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _hostDataDirectory = hostDataDirectory;
        _appLogDirectory = appLogDirectory;
        _fetchDiagnostics = fetchDiagnostics;
        Bringup = bringup;
        ExportCommand = new AsyncCommand(p => ExportAsync(p as string ?? DefaultZipPath()), onError);
    }

    public ObservableCollection<DeviceStatusDto> Devices { get; } = [];

    public NativeBringupViewModel? Bringup { get; }

    public AsyncCommand ExportCommand { get; }

    public string Clock
    {
        get => _clock;
        private set => SetProperty(ref _clock, value);
    }

    public string Uptime
    {
        get => _uptime;
        private set => SetProperty(ref _uptime, value);
    }

    public string ExportResult
    {
        get => _exportResult;
        private set => SetProperty(ref _exportResult, value);
    }

    /// <summary>Running programs that might write lighting (design §8); empty when the Host reports none.</summary>
    public string WriterCandidates
    {
        get => _writerCandidates;
        private set => SetProperty(ref _writerCandidates, value);
    }

    public static string FormatWriterCandidates(IReadOnlyList<WriterCandidateDto>? candidates) =>
        candidates is not { Count: > 0 } ? string.Empty
        : "Running, not confirmed lighting writers (informational; nothing is stopped): "
          + string.Join("; ", candidates.Select(c => c.Affects.Count == 0 ? $"{c.Name} ({c.Reason})" : $"{c.Name} → {string.Join("/", c.Affects)} ({c.Reason})"));

    public static string FormatClock(ClockStatusDto c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return FormattableString.Invariant(
            $"{c.Fps:0.0}/{c.TargetFps} fps · jitter p50 {c.JitterP50Ms:0.00} ms, p99 {c.JitterP99Ms:0.00} ms, max {c.JitterMaxMs:0.00} ms · overruns {c.Overruns}");
    }

    public void Update(StatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _ui.Post(() =>
        {
            Clock = FormatClock(status.Clock);
            ClockChinese = $"{status.Clock.Fps:0.0} / {status.Clock.TargetFps} fps    ·    抖动 p99 {status.Clock.JitterP99Ms:0.000} ms    ·    超时 {status.Clock.Overruns}";
            Uptime = FormattableString.Invariant($"{TimeSpan.FromSeconds(Math.Floor(status.UptimeSeconds)):c} · {status.FramesRendered} frames · Host {status.Version}");
            WriterCandidates = FormatWriterCandidates(status.WriterCandidates);
            var selectedId = SelectedDevice?.Id;
            Devices.Clear();
            foreach (var d in status.Devices)
            {
                Devices.Add(d);
            }
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
        });
    }

    public async Task<string> ExportAsync(string zipPath)
    {
        var extra = new Dictionary<string, string>();
        if (_fetchDiagnostics is not null)
        {
            try
            {
                var status = await _fetchDiagnostics().ConfigureAwait(false);
                extra["packets.json"] = JsonSerializer.Serialize(status, Options);
            }
#pragma warning disable CA1031 // Host down: export what is on disk
            catch (Exception ex)
#pragma warning restore CA1031
            {
                extra["packets.json"] = $"{{\"error\": \"{JsonEncodedText.Encode(ex.Message)}\"}}";
            }
        }

        var entries = await Task.Run(() => DiagnosticsExporter.Export(_hostDataDirectory, _appLogDirectory, zipPath, extra)).ConfigureAwait(false);
        var text = $"Exported {entries.Count} file(s) to {zipPath}";
        _ui.Post(() => ExportResult = text);
        return zipPath;
    }

    private static string DefaultZipPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"UnifiedRgb-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
}
