using System.Collections.ObjectModel;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.ViewModels;

/// <summary>One fan slot of a chain in the mounting editor; <see cref="Override"/> off = the chain setting applies.</summary>
public sealed class FanMountingItem(int fan) : ObservableObject
{
    private bool _override;
    private int _rotation;
    private bool _reverse;

    public int Fan { get; } = fan;

    public string Label => $"风扇 {Fan + 1}";

    public bool Override
    {
        get => _override;
        set => SetProperty(ref _override, value);
    }

    public int RotationDegrees
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value);
    }

    public bool Reverse
    {
        get => _reverse;
        set => SetProperty(ref _reverse, value);
    }
}

/// <summary>
/// Mounting editor of one Lian Li device (WP-11, DEBUG-SESSION-1 §3.2): clockwise rotation viewed from the lit face, a
/// mounting mirror (distinct from an effect's Reverse direction), per-fan overrides, Apply / Reset. Apply re-uploads the
/// device's loop; nothing is written to flash. WP-13 adds the separate whole-chain fan-order reversal (vendor group direction).
/// WP-14 adds, for H2 blocks, the ring calibration (H2-VERTICAL-FILL §3): fine offset, half-step, vertical fill, and the
/// "Calibrate ring" steps (marker, wedge) shown live on the block through the Host; Apply saves once, Cancel restores.
/// </summary>
public sealed class MountingItem : ObservableObject
{
    private readonly PipeClient _client;
    private readonly IUiDispatcher _ui;
    private readonly Action<Exception> _onError;
    private MountingDto _dto;
    private int _rotation;
    private bool _reverse;
    private bool _reverseChain;
    private int _ringOffset;
    private bool _halfStep;
    private bool _faceOnFill;
    private string? _calibrationStep;
    private string _message = string.Empty;

    public MountingItem(MountingDto dto, PipeClient client, IUiDispatcher ui, Action<Exception> onError)
    {
        _dto = dto ?? throw new ArgumentNullException(nameof(dto));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        ApplyCommand = new AsyncCommand(ApplyAsync, onError);
        ResetCommand = new AsyncCommand(ResetAsync, onError);
        CalibrateCommand = new AsyncCommand(() => ShowStepAsync(RingCalibrationDto.Marker), onError);
        OffsetLeftCommand = new AsyncCommand(() => StepOffsetAsync(-1), onError);
        OffsetRightCommand = new AsyncCommand(() => StepOffsetAsync(+1), onError);
        MarkerCommand = new AsyncCommand(() => ShowStepAsync(RingCalibrationDto.Marker), onError);
        WedgeCommand = new AsyncCommand(() => ShowStepAsync(RingCalibrationDto.Wedge), onError);
        ApplyCalibrationCommand = new AsyncCommand(() => EndCalibrationAsync(apply: true), onError);
        CancelCalibrationCommand = new AsyncCommand(() => EndCalibrationAsync(apply: false), onError);
        _onError = onError;
        Load(dto);
    }

    public static IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public string ControllerId => _dto.ControllerId;

    public string Header => DisplayNames.Controller(_dto.ControllerId);

    public string Note => _dto.Note ?? string.Empty;

    public string Current => $"Applied: rev {_dto.Revision}, {_dto.Description}{(_dto.Overridden ? " (GUI override)" : " (configured default)")}";

    public ObservableCollection<FanMountingItem> Fans { get; } = [];

    public AsyncCommand ApplyCommand { get; }

    public AsyncCommand ResetCommand { get; }

    public int RotationDegrees
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value);
    }

    /// <summary>Mounting mirror of every fan (not the effect's direction).</summary>
    public bool Reverse
    {
        get => _reverse;
        set => SetProperty(ref _reverse, value);
    }

    /// <summary>
    /// Whole-chain fan-order reversal (WP-13): L-Connect group effects run fan N → fan 1 on this device. Orthogonal to the
    /// per-fan <see cref="Reverse"/> mirror, which flips LEDs inside each fan.
    /// </summary>
    public bool ReverseChain
    {
        get => _reverseChain;
        set => SetProperty(ref _reverseChain, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>An H2 block (24-LED ring): the ring calibration applies.</summary>
    public bool IsRing => string.Equals(_dto.Family, LConnectLoops.H2, StringComparison.Ordinal);

    /// <summary>Fine clockwise ring shift in LEDs (H2), kept in [−3, +3]; quarter turns go to <see cref="RotationDegrees"/>.</summary>
    public int RingOffsetLeds
    {
        get => _ringOffset;
        set
        {
            if (SetProperty(ref _ringOffset, value))
            {
                OnPropertyChanged(nameof(CalibrationText));
            }
        }
    }

    /// <summary>The block's bottom lies between two LEDs (H2). While calibrating, toggling it re-shows the current step.</summary>
    public bool HalfStep
    {
        get => _halfStep;
        set
        {
            if (SetProperty(ref _halfStep, value) && IsCalibrating)
            {
                _ = ReshowAsync();
            }
        }
    }

    /// <summary>Translational group effects fill the block face from the bottom (H2 vertical fill); off = plain ring.</summary>
    public bool FaceOnFill
    {
        get => _faceOnFill;
        set => SetProperty(ref _faceOnFill, value);
    }

    public bool IsCalibrating => _calibrationStep is not null;

    public bool IsWedgeStep => _calibrationStep == RingCalibrationDto.Wedge;

    public string CalibrationText => _calibrationStep switch
    {
        RingCalibrationDto.Marker =>
            $"Step 1 of 2: use ◀ / ▶ until the RED LED sits at the very bottom of the block (the dim green LEDs show the clockwise direction). "
            + $"If the bottom is between two LEDs, tick \"bottom is between two LEDs\": two red LEDs then straddle it. Now: {RotationDegrees}° {Signed(RingOffsetLeds)}.",
        RingCalibrationDto.Wedge =>
            "Step 2 of 2: the lower quarter of the block should be lit, evenly left and right of the bottom, like liquid filling it. "
            + "Apply saves the calibration and turns vertical fill on; Cancel restores the previous mounting.",
        _ => string.Empty,
    };

    /// <summary>Mirror and chain order as they stay during calibration (read-only there; change them above).</summary>
    public string CalibrationContext => $"Mirror: {(_dto.Reverse ? "on" : "off")}; whole-chain order: {(_dto.ReverseChain ? "reversed" : "normal")} (unchanged by calibration).";

    public AsyncCommand CalibrateCommand { get; }

    public AsyncCommand OffsetLeftCommand { get; }

    public AsyncCommand OffsetRightCommand { get; }

    public AsyncCommand MarkerCommand { get; }

    public AsyncCommand WedgeCommand { get; }

    public AsyncCommand ApplyCalibrationCommand { get; }

    public AsyncCommand CancelCalibrationCommand { get; }

    public MountingDto Build() => new(
        _dto.ControllerId,
        _rotation,
        _reverse,
        Fans.Where(f => f.Override).Select(f => new FanMountingDto(f.Fan, f.RotationDegrees, f.Reverse)).ToArray(),
        ReverseChain: _reverseChain,
        RingOffsetLeds: IsRing ? _ringOffset : 0,
        HalfStep: IsRing && _halfStep,
        FaceOnFill: IsRing && _faceOnFill);

    /// <summary>Shows a calibration step on the block with the current candidate rotation/offset/half-step (nothing saved).</summary>
    internal async Task ShowStepAsync(string step)
    {
        var dto = new RingCalibrationDto(_dto.ControllerId, step, _rotation, _ringOffset, _halfStep);
        var r = await _client.Require().BeginRingCalibrationAsync(dto).ConfigureAwait(true);
        _ui.Post(() =>
        {
            SetStep(step);
            Message = $"Calibrating: {r.Description}";
        });
    }

    /// <summary>◀ / ▶: one LED anticlockwise / clockwise; values past ±3 carry into the quarter-turn rotation (normalised).</summary>
    internal Task StepOffsetAsync(int delta)
    {
        var (rotation, offset) = RingFillMap.NormalizeShift(_rotation, _ringOffset + delta);
        RotationDegrees = rotation;
        RingOffsetLeds = offset;
        return ShowStepAsync(RingCalibrationDto.Marker);
    }

    internal async Task EndCalibrationAsync(bool apply)
    {
        var r = await _client.Require().EndRingCalibrationAsync(_dto.ControllerId, apply).ConfigureAwait(true);
        _ui.Post(() =>
        {
            SetStep(null);
            Load(r);
            Message = apply ? "Ring calibration applied; vertical fill is on." : "Calibration cancelled; the previous mounting was restored.";
        });
    }

    private async Task ReshowAsync()
    {
        try
        {
            await ShowStepAsync(_calibrationStep ?? RingCalibrationDto.Marker).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    private void SetStep(string? step)
    {
        _calibrationStep = step;
        OnPropertyChanged(nameof(IsCalibrating));
        OnPropertyChanged(nameof(IsWedgeStep));
        OnPropertyChanged(nameof(CalibrationText));
    }

    private static string Signed(int v) => v.ToString("+0;-0;±0", System.Globalization.CultureInfo.InvariantCulture) + " LED";

    internal async Task ApplyAsync()
    {
        var r = await _client.Require().SetMountingAsync(Build()).ConfigureAwait(true);
        _ui.Post(() =>
        {
            Load(r);
            Message = "Applied; the loop was re-uploaded.";
        });
    }

    internal async Task ResetAsync()
    {
        var r = await _client.Require().ResetMountingAsync(_dto.ControllerId).ConfigureAwait(true);
        _ui.Post(() =>
        {
            Load(r);
            Message = "Reset to the configured default.";
        });
    }

    private void Load(MountingDto dto)
    {
        _dto = dto;
        RotationDegrees = dto.RotationDegrees;
        Reverse = dto.Reverse;
        ReverseChain = dto.ReverseChain;
        RingOffsetLeds = dto.RingOffsetLeds;
        _halfStep = dto.HalfStep; // not through the setter: loading never re-shows a calibration step
        OnPropertyChanged(nameof(HalfStep));
        FaceOnFill = dto.FaceOnFill;
        Fans.Clear();
        for (var i = 0; i < Math.Max(dto.Units, 1); i++)
        {
            var o = dto.Fans?.FirstOrDefault(f => f.Fan == i);
            Fans.Add(new FanMountingItem(i) { Override = o is not null, RotationDegrees = o?.RotationDegrees ?? 0, Reverse = o?.Reverse ?? false });
        }

        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsRing));
        OnPropertyChanged(nameof(CalibrationContext));
        OnPropertyChanged(nameof(CalibrationText));
    }
}
