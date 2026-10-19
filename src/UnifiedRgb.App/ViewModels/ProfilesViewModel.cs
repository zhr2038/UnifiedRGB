using System.Collections.ObjectModel;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.App.ViewModels;

public sealed class ProfilesViewModel : ObservableObject
{
    private readonly PipeClient _client;
    private readonly IUiDispatcher _ui;
    private string _newName = string.Empty;
    private ProfileInfoDto? _selected;
    private string _summary = string.Empty;
    private string _color = "#5DDAC8";
    private string _details = "选择一个场景查看内容。";
    private int _libraryPage;
    public event EventHandler? Applied;
    public string SelectedTitle => _selected is null ? "场景详情" : DisplayNames.Scene(_selected.Name);
    public string SelectedModified => _selected is null ? "" : $"更新于 {_selected.Modified.ToLocalTime():yyyy/MM/dd HH:mm}";
    public string SelectedColor { get => _color; private set => SetProperty(ref _color, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }

    public ProfilesViewModel(PipeClient client, IUiDispatcher ui, Action<Exception> onError, ScenePackagesViewModel? packages = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Packages = packages;
        ShowSavedCommand = new RelayCommand(() => LibraryPage = 0);
        ShowPackagesCommand = new RelayCommand(() => LibraryPage = 1);
        SaveCommand = new AsyncCommand(SaveAsync, onError);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(), onError, _ => Selected is not null);
        RefreshCommand = new AsyncCommand(RefreshAsync, onError);
    }

    public ObservableCollection<ProfileInfoDto> Profiles { get; } = [];
    public ScenePackagesViewModel? Packages { get; }
    public int LibraryPage { get => _libraryPage; set => SetProperty(ref _libraryPage, value); }
    public RelayCommand ShowSavedCommand { get; }
    public RelayCommand ShowPackagesCommand { get; }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand ApplyCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ProfileInfoDto? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedTitle));
                OnPropertyChanged(nameof(SelectedModified));
                ApplyCommand.RaiseCanExecuteChanged();
                if (value is not null) _ = ShowAsync(value.Name);
                else { Summary = ""; Details = "选择一个场景查看内容。"; SelectedColor = "#5DDAC8"; }
            }
        }
    }

    public async Task RefreshAsync()
    {
        var list = await _client.Require().ListProfilesAsync().ConfigureAwait(true);
        _ui.Post(() =>
        {
            Profiles.Clear();
            foreach (var p in list)
            {
                Profiles.Add(p);
            }
        });
    }

    /// <summary>Saves the running scene under <see cref="NewName"/> (the Host validates the name).</summary>
    public async Task SaveAsync()
    {
        var svc = _client.Require();
        var scene = await svc.GetSceneAsync().ConfigureAwait(true);
        await svc.SaveProfileAsync(new ProfileDto(NewName.Trim(), scene)).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task ApplyAsync()
    {
        var name = _selected?.Name ?? throw new InvalidOperationException("Select a profile.");
        await _client.Require().ApplyProfileAsync(name).ConfigureAwait(true);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowAsync(string name)
    {
        try
        {
            var p = await _client.Require().LoadProfileAsync(name).ConfigureAwait(true);
            _ui.Post(() =>
            {
                if (_selected?.Name != name) return;
                SelectedColor = p.Scene.Assignments.SelectMany(a => a.Colors).FirstOrDefault() ?? "#5DDAC8";
                Details = $"{p.Scene.Assignments.Count} 个灯效编排 · {p.Scene.Assignments.SelectMany(a => a.Targets).Distinct().Count()} 个分区";
                if (p.Scene.Package is { } package)
                {
                    Details = $"外部场景包 · 内容 {package.ContentId[..Math.Min(12, package.ContentId.Length)]}";
                    Summary = $"此场景使用已安装的独立内容包和固定设备绑定。\n\n播放契约：{package.PlaybackContract}\n设备绑定：{package.BindingId[..Math.Min(12, package.BindingId.Length)]}\n\n内容缺失或契约不受支持时，后台会明确阻止应用。";
                    return;
                }
                Summary = string.Join(Environment.NewLine + Environment.NewLine, p.Scene.Assignments.Select(a =>
                    $"{DisplayNames.Scene(a.GroupName ?? DisplayNames.Effect(a.EffectId))}\n{DisplayNames.Effect(a.EffectId)} · {a.Brightness:P0} 亮度 · {DisplayNames.Strategy(a.Strategy)}\n"
                    + (a.SpatialPath is { } path ? string.Join(" → ", path.Stages.Select(s => string.Join(" ＋ ", s.Bindings.Select(b => DisplayNames.Controller(b.Target.ControllerId))))) : string.Join("、", a.Targets.Select(t => DisplayNames.Controller(t.ControllerId))))
                    + (a.LoopReturn is { } r ? $"\n波浪回退 {r.DurationMs / 1000:0.0} 秒 · 堆满停留 {r.HoldMs / 1000:0.0} 秒" : "")));
            });
        }
#pragma warning disable CA1031 // preview text only
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ui.Post(() => Summary = ex.Message);
        }
    }
}
