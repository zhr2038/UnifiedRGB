using System.Collections.ObjectModel;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Preview;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;

namespace UnifiedRgb.App.ViewModels;

/// <summary>One selectable zone of an active controller.</summary>
public sealed class ZoneItem(ControllerDto controller, ZoneDto zone) : ObservableObject
{
    private bool _isSelected;

    public ControllerDto Controller { get; } = controller;

    public ZoneDto Zone { get; } = zone;

    public ZoneRefDto Ref => new(Controller.Id, Zone.Id);

    /// <summary>A native aggregate zone (one colour on every record, no spatial claim) is tagged "unmapped".</summary>
    public string Label => Zone.Mapped
        ? $"{Controller.Name} / {Zone.Name} ({Zone.Layout} {Zone.LedCount})"
        : $"{Controller.Name} / {Zone.Name} ({Zone.Layout} {Zone.LedCount}) · unmapped";

    public bool IsLianLi => string.Equals(Controller.DeviceClass, "LianLi", StringComparison.OrdinalIgnoreCase);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class ColorItem(string hex) : ObservableObject
{
    private string _hex = hex;

    public string Hex
    {
        get => _hex;
        set => SetProperty(ref _hex, value);
    }
}

/// <summary>
/// Schema-driven effect editor (ARCHITECTURE §5.1): the color list honours Min/MaxColors, speed and direction are shown only
/// when the effect uses them. When every selected zone is a Lian Li device the per-family mode list from
/// <see cref="FamilyModeTable"/> is offered; picking a mode loads its effect and preset. With <see cref="LiveApply"/> a
/// slider/colour change re-applies after <see cref="SliderDebounce"/> of quiet (ARCHITECTURE-D2 §4: each change is a Lian Li
/// re-upload).
/// </summary>
public sealed class EffectsViewModel : ObservableObject, IDisposable
{
    /// <summary>D2 §4: sliders are debounced at 300 ms; the Host's mailbox coalesces the rest.</summary>
    public static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The Lian Li loop limits the generic-policy preview mirrors (LianLi:Loop defaults; the Host compiles the same way). Under
    /// the L-Connect policy (WP-11) the preview plays the vendor frames and interval instead (<see cref="PreviewRenderer.VendorPlanFor"/>).
    /// </summary>
    public static readonly Devices.Capabilities LianLiLoopCaps = Devices.Capabilities.UploadedLoopOnly(200, 20, 12288);

    private readonly PipeClient _client;
    private readonly IUiDispatcher _ui;
    private readonly Debouncer _liveApply;
    private bool _live = true;
    private EffectDto? _selected;
    private float _speed = 0.5f;
    private float _brightness = 1f;
    private bool _reverse;
    private string _strategy = VendorStrategy;
    private string? _family;
    private FamilyMode? _familyMode;
    private PreviewRenderer? _preview;

    public EffectsViewModel(PipeClient client, IUiDispatcher ui, Action<Exception> onError, TimeSpan? debounce = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _liveApply = new Debouncer(debounce ?? SliderDebounce, ApplyAsync, onError);
        ApplyCommand = new AsyncCommand(ApplyAsync, onError);
        AddColorCommand = new RelayCommand(_ => AddColor("#FFFFFF"), _ => _selected is not null && Colors.Count < _selected.MaxColors);
        RemoveColorCommand = new RelayCommand(p => RemoveColor(p as ColorItem), _ => _selected is not null && Colors.Count > _selected.MinColors);
    }

    public ObservableCollection<EffectDto> Effects { get; } = [];

    public ObservableCollection<ZoneItem> Zones { get; } = [];

    public ObservableCollection<ColorItem> Colors { get; } = [];

#pragma warning disable CA1822 // instance member for XAML binding
    public IReadOnlyCollection<string> Families => FamilyModeTable.Default.Families;
#pragma warning restore CA1822

    public ObservableCollection<FamilyMode> FamilyModes { get; } = [];

    public AsyncCommand ApplyCommand { get; }

    public RelayCommand AddColorCommand { get; }

    public RelayCommand RemoveColorCommand { get; }

    public EffectDto? SelectedEffect
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                Colors.Clear();
                foreach (var c in value?.DefaultColors ?? [])
                {
                    Colors.Add(new ColorItem(c));
                }

                OnPropertyChanged(nameof(UsesSpeed));
                OnPropertyChanged(nameof(UsesDirection));
                AddColorCommand.RaiseCanExecuteChanged();
                RemoveColorCommand.RaiseCanExecuteChanged();
                RenderPreview();
            }
        }
    }

    public bool UsesSpeed => _selected?.UsesSpeed ?? false;

    /// <summary>Re-apply on slider/colour changes (debounced). Off: only the Apply button sends.</summary>
    public bool LiveApply
    {
        get => _live;
        set => SetProperty(ref _live, value);
    }

    /// <summary>The debounced live apply (tests read <see cref="Debouncer.Fired"/>).</summary>
    public Debouncer LiveApplyDebouncer => _liveApply;

    public bool UsesDirection => _selected?.UsesDirection ?? false;

    public float Speed
    {
        get => _speed;
        set
        {
            if (SetProperty(ref _speed, Math.Clamp(value, 0f, 1f)))
            {
                RenderPreview();
                ScheduleApply();
            }
        }
    }

    public float Brightness
    {
        get => _brightness;
        set
        {
            if (SetProperty(ref _brightness, Math.Clamp(value, 0f, 1f)))
            {
                RenderPreview();
                ScheduleApply();
            }
        }
    }

    public bool Reverse
    {
        get => _reverse;
        set
        {
            if (SetProperty(ref _reverse, value))
            {
                RenderPreview();
                ScheduleApply();
            }
        }
    }

    public const string VendorStrategy = "Vendor";

    public const string ProgramStrategy = "Program";

    /// <summary>
    /// The content strategies this page can apply (NATIVE design §7.2). Frames is a Host seam without an asset source here, so it
    /// is not offered.
    /// </summary>
    public static readonly string[] UsableStrategies = [VendorStrategy, ProgramStrategy];

#pragma warning disable CA1822 // instance member for XAML binding
    public IReadOnlyList<string> Strategies => UsableStrategies;
#pragma warning restore CA1822

    /// <summary>
    /// Content strategy of the assignment (AssignmentDto.Strategy): Vendor = L-Connect's own loop bodies where they exist
    /// (default; streamed native zones join the same plan), Program = the generator itself. The legacy LoopPolicy is sent too.
    /// </summary>
    public string Strategy
    {
        get => _strategy;
        set
        {
            var s = UsableStrategies.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) ?? VendorStrategy;
            if (SetProperty(ref _strategy, s))
            {
                OnPropertyChanged(nameof(LConnectLoops));
                RenderPreview();
                ScheduleApply();
            }
        }
    }

    /// <summary>Legacy WP-11 switch: on = Vendor, off = Program.</summary>
    public bool LConnectLoops
    {
        get => _strategy == VendorStrategy;
        set => Strategy = value ? VendorStrategy : ProgramStrategy;
    }

    public LoopPolicy Policy => LConnectLoops ? LoopPolicy.LConnect : LoopPolicy.Generic;

    /// <summary>The strategy choice is shown once a zone is selected.</summary>
    public bool ShowStrategy => Zones.Any(z => z.IsSelected);

    /// <summary>WP-12: under the L-Connect policy on Lian Li zones the speed slider snaps to the vendor's five steps.</summary>
    public bool SnapVendorSpeed => LConnectLoops && ShowFamilyModes;

    /// <summary>The vendor speed step the slider position plays at (L1..L5 = SpeedType 7..3), when it snaps.</summary>
    public string VendorSpeedText => SnapVendorSpeed
        ? $"L{8 - Core.Effects.LianLi.LConnectLoops.SpeedType(_speed)} (×{Core.Effects.LianLi.LConnectLoops.SpeedType(_speed)})"
        : string.Empty;

    /// <summary>What the preview (and the Host) compile for the selection: vendor frames × interval (per zone when the
    /// per-device routes differ), or generic.</summary>
    public string LoopSummary => _preview?.Vendor is { } v
        ? $"{v.Variant}: {v.Summary}"
        : _preview?.Loop is { } l ? $"generic: {l.Timing.Frames} frames × {l.Timing.IntervalMs:0.##} ms = {l.PeriodSeconds:0.###} s" : string.Empty;

    /// <summary>True when the selection is non-empty and all Lian Li: the per-family mode list applies.</summary>
    public bool ShowFamilyModes => Zones.Any(z => z.IsSelected) && Zones.Where(z => z.IsSelected).All(z => z.IsLianLi);

    public string? Family
    {
        get => _family;
        set
        {
            if (SetProperty(ref _family, value))
            {
                FamilyModes.Clear();
                if (value is not null && Families.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var m in FamilyModeTable.Default.Modes(value))
                    {
                        FamilyModes.Add(m);
                    }
                }
            }
        }
    }

    public FamilyMode? FamilyMode
    {
        get => _familyMode;
        set
        {
            if (SetProperty(ref _familyMode, value) && value is not null)
            {
                SelectedEffect = Effects.FirstOrDefault(e => e.Id == value.EffectId);
                Speed = value.Preset.Speed;
                Reverse = value.Preset.Direction == Direction.Reverse;
            }
        }
    }

    public PreviewRenderer? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(LoopSummary));
            }

            // speed, policy and selection all re-render the preview
            OnPropertyChanged(nameof(SnapVendorSpeed));
            OnPropertyChanged(nameof(VendorSpeedText));
        }
    }

    public async Task RefreshAsync()
    {
        var svc = _client.Require();
        var effects = await svc.ListEffectsAsync().ConfigureAwait(true);
        var controllers = await svc.ListControllersAsync().ConfigureAwait(true);
        _ui.Post(() =>
        {
            var keep = _selected?.Id;
            Effects.Clear();
            foreach (var e in effects)
            {
                Effects.Add(e);
            }

            SetControllers(controllers);
            SelectedEffect = Effects.FirstOrDefault(e => e.Id == keep) ?? Effects.FirstOrDefault();
        });
    }

    public void SetControllers(IReadOnlyList<ControllerDto> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        var selected = Zones.Where(z => z.IsSelected).Select(z => z.Ref).ToHashSet();
        foreach (var z in Zones)
        {
            z.PropertyChanged -= OnZoneChanged;
        }

        Zones.Clear();
        foreach (var c in controllers.Where(c => c.State == ControllerStates.Active))
        {
            foreach (var z in c.Zones)
            {
                var item = new ZoneItem(c, z) { IsSelected = selected.Contains(new ZoneRefDto(c.Id, z.Id)) };
                item.PropertyChanged += OnZoneChanged;
                Zones.Add(item);
            }
        }

        OnZoneChanged(null, null);
    }

    public AssignmentDto BuildAssignment()
    {
        var effect = _selected ?? throw new InvalidOperationException("Choose an effect.");
        var targets = Zones.Where(z => z.IsSelected).Select(z => z.Ref).ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidOperationException("Select at least one zone.");
        }

        var colors = Colors.Select(c => ColorText.TryParse(c.Hex, out var rgb) ? ColorText.Format(rgb) : throw new FormatException($"'{c.Hex}' is not #RRGGBB.")).ToArray();
        return new AssignmentDto(targets, effect.Id, colors, _speed, _brightness, _reverse, 1, targets.Length > 1 ? "Group" : null, Policy.ToString(), _strategy);
    }

    public void AddColor(string hex)
    {
        Colors.Add(new ColorItem(hex));
        AddColorCommand.RaiseCanExecuteChanged();
        RemoveColorCommand.RaiseCanExecuteChanged();
        RenderPreview();
        ScheduleApply();
    }

    public void Dispose() => _liveApply.Dispose();

    /// <summary>Starts (or restarts) the debounce window when a live apply is possible: an effect and at least one zone.</summary>
    public void ScheduleApply()
    {
        if (_live && _selected is not null && Zones.Any(z => z.IsSelected))
        {
            _liveApply.Trigger();
        }
    }

    private void RemoveColor(ColorItem? item)
    {
        if (item is not null && Colors.Remove(item))
        {
            AddColorCommand.RaiseCanExecuteChanged();
            RemoveColorCommand.RaiseCanExecuteChanged();
            RenderPreview();
            ScheduleApply();
        }
    }

    private Task ApplyAsync() => _client.Require().SetAssignmentAsync(BuildAssignment());

    private void OnZoneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs? e)
    {
        OnPropertyChanged(nameof(ShowFamilyModes));
        OnPropertyChanged(nameof(ShowStrategy));
        if (ShowFamilyModes && _family is null)
        {
            // default the family from the controller name ("SLv3 AA-BB-…" as discovery names it)
            var name = Zones.First(z => z.IsSelected).Controller.Name;
            Family = Families.FirstOrDefault(f => name.StartsWith(f + " ", StringComparison.OrdinalIgnoreCase));
        }

        RenderPreview();
    }

    private void RenderPreview()
    {
        var zones = Zones.Where(z => z.IsSelected).ToArray();
        if (_selected is null || !EffectRegistry.Default.TryGet(_selected.Id, out var gen))
        {
            Preview = null;
            return;
        }

        var layouts = zones.Length > 0 ? zones.Select(z => PreviewRenderer.ToLayout(z.Zone)).ToArray() : [Devices.ZoneLayout.Linear(24)];
        var colors = Colors.Select(c => ColorText.TryParse(c.Hex, out var rgb) ? rgb : (Devices.Rgb?)null).OfType<Devices.Rgb>().ToArray();
        var p = new EffectParams(colors, _speed, _brightness, _reverse ? Direction.Reverse : Direction.Forward);

        // a selection with a Lian Li chain plays the compiled loop (D2 §6): same period, same frame steps as the upload
        var loop = zones.Any(z => z.IsLianLi) ? PreviewRenderer.LoopFor(gen, p, LianLiLoopCaps) : null;
        var vendor = zones.Length > 0 && zones.All(z => z.IsLianLi) ? PreviewRenderer.VendorPlanFor(gen, p, zones.Select(z => z.Zone).ToArray(), Policy) : null;
        Preview = new PreviewRenderer(gen, layouts, p, loop, vendor, zones.Select(z => z.Zone).ToArray());
    }
}
