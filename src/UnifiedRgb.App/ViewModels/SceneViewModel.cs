using System.Collections.ObjectModel;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Preview;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.ViewModels;

/// <summary>One assignment of the running scene with its live preview (the same generator the Host runs).</summary>
public sealed record AssignmentItem(AssignmentDto Dto, string Targets, PreviewRenderer? Preview)
{
    public string Title => Dto.GroupName is { } g ? $"{g}: {Dto.EffectId} · {StrategyText}" : $"{Dto.EffectId} · {StrategyText}";

    /// <summary>
    /// Strategy as the Host resolves it (DtoMapper.FromDto): Strategy wins; otherwise the legacy LoopPolicy ("Generic" →
    /// Program, anything else → Vendor). A Frames assignment (from a profile) is displayed with its frame set, never offered.
    /// </summary>
    public string StrategyText => StrategyOf(Dto);

    public static string StrategyOf(AssignmentDto a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.Strategy is { } s
            ? string.Equals(s, "Frames", StringComparison.OrdinalIgnoreCase) ? $"Frames: {a.FrameSetId}" : s
            : string.Equals(a.LoopPolicy, "Generic", StringComparison.OrdinalIgnoreCase) ? EffectsViewModel.ProgramStrategy : EffectsViewModel.VendorStrategy;
    }
}

/// <summary>
/// Scene page: the running assignments with live previews, and a group builder — zones are appended in order (fans → GPU →
/// DRAM) and become one continuous strip along u when applied.
/// </summary>
public sealed class SceneViewModel : ObservableObject
{
    private readonly PipeClient _client;
    private readonly IUiDispatcher _ui;
    private IReadOnlyList<ControllerDto> _controllers = [];
    private string _groupName = "Group";
    private string? _groupEffect = "Runway";
    private string _groupStrategy = EffectsViewModel.VendorStrategy;
    private ZoneItem? _candidate;

    public SceneViewModel(PipeClient client, IUiDispatcher ui, Action<Exception> onError)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        AddToGroupCommand = new RelayCommand(() =>
        {
            if (_candidate is not null && !GroupZones.Contains(_candidate))
            {
                GroupZones.Add(_candidate);
            }
        });
        RemoveFromGroupCommand = new RelayCommand(p => GroupZones.Remove((p as ZoneItem)!));
        MoveUpCommand = new RelayCommand(p => Move(p as ZoneItem, -1));
        ApplyGroupCommand = new AsyncCommand(ApplyGroupAsync, onError);
        RemoveAssignmentCommand = new AsyncCommand(p => RemoveAsync(p as AssignmentItem), onError);
        RefreshCommand = new AsyncCommand(RefreshAsync, onError);
    }

    public ObservableCollection<AssignmentItem> Assignments { get; } = [];

    public ObservableCollection<ZoneItem> AvailableZones { get; } = [];

    public ObservableCollection<ZoneItem> GroupZones { get; } = [];

#pragma warning disable CA1822 // instance member for XAML binding
    public IEnumerable<string> EffectIds => EffectRegistry.Default.All.Select(g => g.Id);

    /// <summary>Vendor or Program only: Frames has no asset source in the GUI.</summary>
    public IReadOnlyList<string> Strategies => EffectsViewModel.UsableStrategies;
#pragma warning restore CA1822

    public RelayCommand AddToGroupCommand { get; }

    public RelayCommand RemoveFromGroupCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public AsyncCommand ApplyGroupCommand { get; }

    public AsyncCommand RemoveAssignmentCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public ZoneItem? Candidate
    {
        get => _candidate;
        set => SetProperty(ref _candidate, value);
    }

    public string GroupName
    {
        get => _groupName;
        set => SetProperty(ref _groupName, value);
    }

    public string? GroupEffect
    {
        get => _groupEffect;
        set => SetProperty(ref _groupEffect, value);
    }

    /// <summary>Content strategy of the group (default Vendor, which is what a group without one always got).</summary>
    public string GroupStrategy
    {
        get => _groupStrategy;
        set => SetProperty(ref _groupStrategy,
            EffectsViewModel.UsableStrategies.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) ?? EffectsViewModel.VendorStrategy);
    }

    public async Task RefreshAsync()
    {
        var svc = _client.Require();
        var controllers = await svc.ListControllersAsync().ConfigureAwait(true);
        var scene = await svc.GetSceneAsync().ConfigureAwait(true);
        _ui.Post(() => Show(controllers, scene));
    }

    public void Show(IReadOnlyList<ControllerDto> controllers, SceneDto scene)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentNullException.ThrowIfNull(scene);
        _controllers = controllers;
        AvailableZones.Clear();
        foreach (var c in controllers.Where(c => c.State == ControllerStates.Active))
        {
            foreach (var z in c.Zones)
            {
                AvailableZones.Add(new ZoneItem(c, z));
            }
        }

        Assignments.Clear();
        foreach (var a in scene.Assignments)
        {
            var text = a.SpatialPath is { } path ? PathText(path) : string.Join(" → ", a.Targets.Select(Label));
            Assignments.Add(new AssignmentItem(a, a.LoopReturn is { } r ? $"{text}\n{ReturnText(r)}" : text, PreviewOf(a)));
        }
    }

    /// <summary>Preview of an assignment over the zones that exist now (offline targets are skipped like the Host does).</summary>
    public PreviewRenderer? PreviewOf(AssignmentDto a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.SpatialPath is not null || a.LoopReturn is not null || !EffectRegistry.Default.TryGet(a.EffectId, out var gen))
        {
            return null; // SPATIAL-PATH / STACK-RETURN: no preview rather than a misleading flat concatenation (see PathText)
        }

        var zones = a.Targets.Select(Find).OfType<ZoneDto>().ToArray();
        var layouts = zones.Select(PreviewRenderer.ToLayout).ToArray();
        return layouts.Length == 0 ? null : new PreviewRenderer(gen, layouts, PreviewRenderer.ToParams(a), zones: zones);
    }

    public AssignmentDto BuildGroup()
    {
        if (GroupZones.Count == 0 || string.IsNullOrWhiteSpace(_groupEffect))
        {
            throw new InvalidOperationException("Add zones and choose an effect.");
        }

        return new AssignmentDto(GroupZones.Select(z => z.Ref).ToArray(), _groupEffect, [], GroupName: string.IsNullOrWhiteSpace(_groupName) ? "Group" : _groupName,
            LoopPolicy: _groupStrategy == EffectsViewModel.ProgramStrategy ? "Generic" : "LConnect", Strategy: _groupStrategy);
    }

    /// <summary>
    /// SPATIAL-PATH: the stage/parallel summary of an explicit path, low source end first ("‖" = parallel, "~" = reversed in
    /// its stage), with the honest preview note.
    /// </summary>
    public string PathText(SpatialPathDto path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stages = (path.Stages ?? []).Select(s => $"{s.Length}: {string.Join(" ‖ ", (s.Bindings ?? []).Select(b => Label(b.Target) + (b.Reverse ? " ~" : string.Empty)))}");
        return $"Spatial path, low → high source: {string.Join(" | ", stages)}\nPreview unavailable for explicit spatial paths.";
    }

    /// <summary>STACK-RETURN: the compact, read-only summary of a loop return (kept unchanged through Scene/Profile roundtrips).</summary>
    public static string ReturnText(LoopReturnDto r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"Loop return after full: hold {r.HoldMs:0.###} ms, then {r.DurationMs:0.###} ms reverse-flow drain (wave {r.WaveWidthDots} dots, ripple {r.WaveStrength:0.###}).";
    }

    private ZoneDto? Find(ZoneRefDto r) => _controllers.FirstOrDefault(c => c.Id == r.ControllerId)?.Zones.FirstOrDefault(z => z.Id == r.ZoneId);

    private string Label(ZoneRefDto r)
    {
        var c = _controllers.FirstOrDefault(c => c.Id == r.ControllerId);
        var z = c?.Zones.FirstOrDefault(z => z.Id == r.ZoneId);
        return c is null || z is null ? $"{r.ControllerId}/{r.ZoneId} (offline)" : $"{c.Name}/{z.Name}";
    }

    private void Move(ZoneItem? item, int delta)
    {
        if (item is null)
        {
            return;
        }

        var i = GroupZones.IndexOf(item);
        var j = i + delta;
        if (i >= 0 && j >= 0 && j < GroupZones.Count)
        {
            GroupZones.Move(i, j);
        }
    }

    private async Task ApplyGroupAsync()
    {
        await _client.Require().SetAssignmentAsync(BuildGroup()).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RemoveAsync(AssignmentItem? item)
    {
        if (item is null)
        {
            return;
        }

        var svc = _client.Require();
        var scene = await svc.GetSceneAsync().ConfigureAwait(true);
        await svc.SetSceneAsync(new SceneDto(scene.Assignments.Where(a => !Same(a, item.Dto)).ToArray())).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private static bool Same(AssignmentDto a, AssignmentDto b) =>
        a.EffectId == b.EffectId && a.GroupName == b.GroupName && a.Targets.SequenceEqual(b.Targets);
}
