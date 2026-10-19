using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;

namespace UnifiedRgb.App.Workbench;

public sealed class StudioZone(ControllerDto controller, ZoneDto zone) : ObservableObject
{
    private bool _chosen;
    public ControllerDto Controller { get; private set; } = controller;
    public ZoneDto Zone { get; private set; } = zone;
    public ZoneRefDto Ref => new(Controller.Id, Zone.Id);
    public string Name => DisplayNames.Zone(Controller, Zone);
    public string Detail => $"{DisplayNames.State(Controller.State)} · {Zone.LedCount} 个逻辑灯位{(Zone.Mapped ? string.Empty : " · 未定位")}";
    public bool IsChosen { get => _chosen; set => SetProperty(ref _chosen, value); }
    internal void Update(ControllerDto updatedController, ZoneDto updatedZone)
    {
        if (updatedController.Id != Controller.Id || updatedZone.Id != Zone.Id) throw new ArgumentException("Zone identity changed.");
        Controller = updatedController; Zone = updatedZone;
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Detail));
    }
}

public sealed class DraftBinding(StudioZone zone, bool reverse, Action changed) : ObservableObject
{
    private bool _reverse = reverse;
    public StudioZone Zone { get; } = zone;
    public string Name => Zone.Name;
    public bool Reverse
    {
        get => _reverse;
        set { if (SetProperty(ref _reverse, value)) changed(); }
    }
    public SpatialBindingDto ToDto(string effectId) => new(Zone.Ref, PhysicalReverse(Zone, Reverse, effectId));
    public static bool PhysicalReverse(StudioZone zone, bool reverse, string effectId) =>
        reverse ^ (zone.Zone.Family == "H2" && zone.Zone.FaceOnFill && EffectGeometry.Of(effectId).Bottom == BottomEnd.High);
}

public sealed class DraftStage : ObservableObject
{
    private int _length;
    private readonly Action _changed;
    public DraftStage(int length, IEnumerable<DraftBinding> bindings, Action changed)
    {
        _length = length;
        _changed = changed;
        foreach (var b in bindings) Bindings.Add(b);
    }
    public int Length
    {
        get => _length;
        set { if (SetProperty(ref _length, value)) _changed(); }
    }
    public ObservableCollection<DraftBinding> Bindings { get; } = [];
    public string Names => string.Join(" ＋ ", Bindings.Select(b => b.Name));
    public SpatialStageDto ToDto(string effectId) => new(Length, Bindings.Select(b => b.ToDto(effectId)).ToArray());
}

public sealed record EffectChoice(EffectDto Dto)
{
    public string Id => Dto.Id;
    public string Name => DisplayNames.Effect(Dto.Id);
}

public sealed record StrategyChoice(string Id, string Name);

public sealed class SceneDraft : ObservableObject
{
    private bool _loading;
    private bool _dirty;
    private AssignmentDto? _original;
    private string _name = "全机灯效";
    private EffectChoice? _effect;
    private StrategyChoice? _strategy;
    private string _colors = "#FF0000";
    private double _speed = 75;
    private double _brightness = 100;
    private bool _reverse;
    private bool _return;
    private double _duration = 4;
    private double _hold = .6;
    private int _width = 24;
    private double _strength = 25;
    private DraftStage? _selectedStage;
    private bool _spatial = true;
    private string _lastPalette = "#FF0000";
    public long Revision { get; private set; }

    public SceneDraft()
    {
        Strategies = [new("Vendor", "原厂曲线"), new("Program", "程序生成"), new("Frames", "保留帧动画")];
        _strategy = Strategies[0];
        Stages.CollectionChanged += (_, _) => { Changed(); OnPropertyChanged(nameof(PathSummary)); };
    }

    public event EventHandler? ChangedDraft;
    public ObservableCollection<EffectChoice> Effects { get; } = [];
    public IReadOnlyList<StrategyChoice> Strategies { get; }
    public IEnumerable<StrategyChoice> AvailableStrategies => _original?.Strategy == "Frames" ? Strategies : Strategies.Take(2);
    public bool CanUseVendor => !UseSpatialPath || !LConnectLoops.IsPerDeviceGroupRoute(Effect?.Id ?? "");
    public bool CanUseSpatial => Strategy?.Id != "Frames";
    public string StrategyHint => UseSpatialPath && LConnectLoops.IsPerDeviceGroupRoute(Effect?.Id ?? "")
        ? "此效果使用程序生成，以保持整机路径和共享节奏。" : "原厂曲线与程序效果都由光域独立生成和发送。";
    public ObservableCollection<DraftStage> Stages { get; } = [];
    public bool Dirty { get => _dirty; private set { if (SetProperty(ref _dirty, value)) OnPropertyChanged(nameof(DraftState)); } }
    public string DraftState => Dirty ? "有未应用的修改" : "与已读取场景一致";
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) { Changed(); OnPropertyChanged(nameof(NameLabel)); } } }
    public string NameLabel { get => DisplayNames.Scene(Name); set => Name = value; }
    public EffectChoice? Effect
    {
        get => _effect;
        set
        {
            if (!SetProperty(ref _effect, value)) return;
            if (!_loading)
            {
                if (ParseColors().Length > 0) _lastPalette = Colors;
                var schema = value?.Dto;
                if (schema?.MaxColors == 0) Colors = "";
                else if (schema is not null)
                {
                    var colors = ParseColors();
                    var previous = _lastPalette.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (colors.Length < schema.MinColors || colors.Length > schema.MaxColors)
                        Colors = previous.Length >= schema.MinColors && previous.Length <= schema.MaxColors ? _lastPalette : string.Join(" ", schema.DefaultColors);
                }
                if (!CanReturn) ReturnEnabled = false;
                EnsureSupportedStrategy();
            }
            Changed(); NotifyEffectCapabilities();
        }
    }
    public StrategyChoice? Strategy
    {
        get => _strategy;
        set
        {
            if (!_loading && value?.Id == "Vendor" && UseSpatialPath && LConnectLoops.IsPerDeviceGroupRoute(Effect?.Id ?? "")) value = Strategies[1];
            if (SetProperty(ref _strategy, value)) { if (!_loading && !CanReturn) ReturnEnabled = false; Changed(); NotifyEffectCapabilities(); }
        }
    }
    public string Colors { get => _colors; set { if (SetProperty(ref _colors, value)) { Changed(); OnPropertyChanged(nameof(PrimaryColor)); } } }
    public string PrimaryColor => ParseColors().FirstOrDefault() is { } c && Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant) ? c : "#5DDAC8";
    public double Speed { get => _speed; set { if (SetProperty(ref _speed, value)) Changed(); } }
    public double Brightness { get => _brightness; set { if (SetProperty(ref _brightness, value)) Changed(); } }
    public bool Reverse { get => _reverse; set { if (SetProperty(ref _reverse, value)) Changed(); } }
    public bool ReturnEnabled { get => _return; set { if (SetProperty(ref _return, value)) Changed(); } }
    public bool CanReturn => Effect?.Id == "Stacking" && Strategy?.Id == "Vendor";
    public bool HasColors => Effect?.Dto.MaxColors > 0;
    public bool UsesSpeed => Effect?.Dto.UsesSpeed ?? false;
    public bool UsesDirection => Effect?.Dto.UsesDirection ?? false;
    public bool SnapSpeed => Strategy?.Id == "Vendor";
    public bool UseSpatialPath { get => _spatial; set { if (SetProperty(ref _spatial, value)) { if (!_loading) EnsureSupportedStrategy(); Changed(); NotifyEffectCapabilities(); } } }
    public double ReturnSeconds { get => _duration; set { if (SetProperty(ref _duration, value)) Changed(); } }
    public double HoldSeconds { get => _hold; set { if (SetProperty(ref _hold, value)) Changed(); } }
    public int WaveWidth { get => _width; set { if (SetProperty(ref _width, value)) Changed(); } }
    public double WaveStrength { get => _strength; set { if (SetProperty(ref _strength, value)) Changed(); } }
    public DraftStage? SelectedStage { get => _selectedStage; set => SetProperty(ref _selectedStage, value); }
    public string PathSummary => $"{Stages.Count} 段路径 · {Stages.Sum(s => s.Length)} 个源灯位 · {Stages.Sum(s => s.Bindings.Count)} 个分区";
    public string FlowNote => Effect?.Id == "Stacking"
        ? "路径按光条行进顺序排列；堆积从路径末端留下。回退沿反方向流动。"
        : "每段内的分区同时变化，依次衔接下一段。方向是效果方向，不改变安装校准。";

    public AssignmentDto? Original => _original;

    public void Load(AssignmentDto? dto, IReadOnlyList<StudioZone> zones)
    {
        _loading = true;
        try
        {
            _original = dto;
            OnPropertyChanged(nameof(AvailableStrategies));
            UseSpatialPath = dto is null || dto.SpatialPath is not null;
            Name = dto?.GroupName ?? "全机灯效";
            var id = dto?.EffectId ?? "Stacking";
            if (Effects.All(e => e.Id != id)) Effects.Add(new(new EffectDto(id, id, 0, 64, true, true, true, [])));
            Effect = Effects.First(e => e.Id == id);
            var strategy = dto?.Strategy ?? (dto?.LoopPolicy == "Generic" ? "Program" : "Vendor");
            Strategy = Strategies.FirstOrDefault(s => s.Id == strategy) ?? Strategies[0];
            Colors = string.Join("  ", dto?.Colors ?? ["#FF0000"]);
            Speed = (dto?.Speed ?? .75f) * 100;
            Brightness = (dto?.Brightness ?? 1) * 100;
            Reverse = dto?.Reverse ?? false;
            ReturnEnabled = dto?.LoopReturn is not null;
            ReturnSeconds = (dto?.LoopReturn?.DurationMs ?? 4000) / 1000;
            HoldSeconds = (dto?.LoopReturn?.HoldMs ?? 600) / 1000;
            WaveWidth = dto?.LoopReturn?.WaveWidthDots ?? 24;
            WaveStrength = (dto?.LoopReturn?.WaveStrength ?? .25) * 100;
            Stages.Clear();
            if (dto?.SpatialPath is { } path)
            {
                foreach (var stage in path.Stages)
                {
                    var bindings = stage.Bindings.Select(b =>
                    {
                        var zone = FindZone(zones, b.Target);
                        return new DraftBinding(zone, DraftBinding.PhysicalReverse(zone, b.Reverse, id), Changed);
                    });
                    Stages.Add(new(stage.Length, bindings, Changed));
                }
            }
            else
            {
                foreach (var target in dto?.Targets ?? [])
                {
                    var z = FindZone(zones, target);
                    AddStage([z]);
                }
            }
            SelectedStage = Stages.FirstOrDefault();
        }
        finally { _loading = false; Dirty = dto is null; Revision++; NotifyAll(); }
    }

    private static StudioZone FindZone(IReadOnlyList<StudioZone> zones, ZoneRefDto target) => zones.FirstOrDefault(z => z.Ref == target)
        ?? new(new ControllerDto(target.ControllerId, "", target.ControllerId, "Unknown", "", "", [], 0, "Offline", "未发现"), new ZoneDto(target.ZoneId, "离线分区", "Linear", 1, 1, 1));

    public void AddStage(IEnumerable<StudioZone> zones)
    {
        var list = zones.ToArray();
        if (list.Length == 0) return;
        var bound = Stages.SelectMany(s => s.Bindings).Select(b => b.Zone.Ref).ToHashSet();
        if (list.Any(z => bound.Contains(z.Ref))) throw new InvalidOperationException("所选分区已在路径中。请先从原路径段移除，避免重复控制。");
        var length = list.Max(z => z.Zone.Family == "TL" ? Math.Max(1, z.Zone.Units) * 12 : z.Zone.Family == "H2" ? 24 : Math.Max(12, (int)Math.Ceiling(z.Zone.LedCount / 12.0) * 12));
        var stage = new DraftStage(length, list.Select(z => new DraftBinding(z, false, Changed)), Changed);
        Stages.Add(stage);
        SelectedStage = stage;
    }

    public void MoveStage(int delta)
    {
        var i = SelectedStage is null ? -1 : Stages.IndexOf(SelectedStage);
        if (i >= 0 && i + delta >= 0 && i + delta < Stages.Count) Stages.Move(i, i + delta);
    }

    public void RemoveStage()
    {
        if (SelectedStage is not null) Stages.Remove(SelectedStage);
        SelectedStage = Stages.FirstOrDefault();
    }

    public AssignmentDto Build()
    {
        if (!Dirty && _original is not null) return _original;
        var effect = Effect?.Dto ?? throw new InvalidOperationException("请选择灯效。");
        var colors = ParseColors();
        if (colors.Any(c => !Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant))) throw new InvalidOperationException("颜色使用 #RRGGBB，多色之间用空格分隔。");
        if (colors.Length != 0 && (colors.Length < effect.MinColors || colors.Length > effect.MaxColors)) throw new InvalidOperationException($"此灯效需要 {effect.MinColors}～{effect.MaxColors} 个颜色；留空可使用默认配色。");
        if (!double.IsFinite(Speed) || !double.IsFinite(Brightness) || Speed is < 0 or > 100 || Brightness is < 0 or > 100) throw new InvalidOperationException("速度与亮度必须在 0～100% 之间。");
        if (Stages.Count == 0 || Stages.Any(s => s.Length is < 1 or > 720 || s.Bindings.Count == 0) || Stages.Sum(s => s.Length) > 720) throw new InvalidOperationException("路径每段需要 1～720 个源灯位及至少一个分区，总长度不超过 720。");
        var targets = Stages.SelectMany(s => s.Bindings).Select(b => b.Zone.Ref).ToArray();
        if (targets.Distinct().Count() != targets.Length) throw new InvalidOperationException("一个分区只能出现在一个路径段中。");
        LoopReturnDto? tail = null;
        if (ReturnEnabled)
        {
            if (!CanReturn) throw new InvalidOperationException("波浪回退仅适用于“原厂曲线”的“堆积”效果。请关闭回退或切回该组合。");
            if (!UseSpatialPath) throw new InvalidOperationException("波浪回退需要启用分段空间路径。");
            if (!double.IsFinite(ReturnSeconds) || ReturnSeconds is < 1 or > 20 || !double.IsFinite(HoldSeconds) || HoldSeconds is < 0 or > 2 || WaveWidth is < 2 or > 96 || !double.IsFinite(WaveStrength) || WaveStrength is < 0 or > 50) throw new InvalidOperationException("回退范围：1～20 秒、停留 0～2 秒、羽化 2～96 灯位、波纹 0～50%。");
            tail = new(ReturnSeconds * 1000, HoldSeconds * 1000, WaveWidth, WaveStrength / 100);
        }
        var original = _original ?? new AssignmentDto([], effect.Id, []);
        return original with
        {
            Targets = targets, EffectId = effect.Id, Colors = colors, Speed = (float)(Speed / 100), Brightness = (float)(Brightness / 100),
            Reverse = Reverse, GroupName = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(), Strategy = Strategy?.Id ?? "Vendor",
            LoopPolicy = Strategy?.Id == "Program" ? "Generic" : "LConnect", SpatialPath = UseSpatialPath ? new(Stages.Select(s => s.ToDto(effect.Id)).ToArray()) : null, LoopReturn = tail,
        };
    }

    private string[] ParseColors() => Colors.Split([' ', ',', ';', '，', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public void SetPrimaryColor(string hex)
    {
        var colors = ParseColors();
        Colors = colors.Length == 0 ? hex : string.Join(" ", new[] { hex }.Concat(colors.Skip(1)));
    }
    private void Changed()
    {
        if (_loading) return;
        Revision++;
        Dirty = true;
        NotifyAll();
    }
    private void NotifyAll()
    {
        OnPropertyChanged(nameof(PathSummary)); OnPropertyChanged(nameof(FlowNote)); OnPropertyChanged(nameof(PrimaryColor));
        ChangedDraft?.Invoke(this, EventArgs.Empty);
    }
    private void NotifyEffectCapabilities()
    {
        OnPropertyChanged(nameof(CanReturn)); OnPropertyChanged(nameof(HasColors)); OnPropertyChanged(nameof(UsesSpeed));
        OnPropertyChanged(nameof(UsesDirection)); OnPropertyChanged(nameof(SnapSpeed));
        OnPropertyChanged(nameof(CanUseVendor)); OnPropertyChanged(nameof(CanUseSpatial)); OnPropertyChanged(nameof(StrategyHint));
    }
    private void EnsureSupportedStrategy()
    {
        if (UseSpatialPath && Strategy?.Id == "Vendor" && LConnectLoops.IsPerDeviceGroupRoute(Effect?.Id ?? "")) Strategy = Strategies[1];
    }
}
