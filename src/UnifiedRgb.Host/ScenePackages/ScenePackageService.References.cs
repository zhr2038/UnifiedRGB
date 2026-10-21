using System.Collections.Concurrent;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

public sealed partial class ScenePackageService
{
    private readonly ProfileStore? _profiles;
    private readonly IDisposable? _referenceRegistration;
    private readonly ConcurrentDictionary<string, byte> _hydrating = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ReferenceJson = ContractsJson.Configure(new JsonSerializerOptions());

    private List<ProfileDto> StoredProfiles()
    {
        if (_profiles is null) throw new NotSupportedException("This Host has no managed profile library.");
        var names = _profiles.ReferenceNames();
        long bytes = 0;
        var list = new List<ProfileDto>();
        foreach (var name in names)
        {
            bytes += new FileInfo(Path.Combine(_profiles.Directory, name + ".json")).Length;
            if (bytes > 64L * 1024 * 1024) throw new InvalidOperationException("Saved-scene reference inspection exceeded 64 MiB; nothing was removed.");
            var profile = _profiles.Load(name);
            RequireReferenceShape(profile.Scene);
            list.Add(profile); // unreadable/oversized profiles refuse cleanup, never disappear from the reference check
        }
        return list;
    }

    private bool ProfileReferencesAsset(string assetId)
    {
        if (File.Exists(Path.Combine(Store.DataRoot, SceneStateFiles.JournalName)) || File.Exists(Path.Combine(Store.DataRoot, SceneStateFiles.RecoveryRequiredName))) return true;
        if (_profiles?.ReadActiveForReferences() is { } active)
        {
            RequireReferenceShape(active.Scene);
            if (active.Scene.Screens?.Assignments.Any(a => a.BackgroundAssetId == assetId) == true) return true;
        }
        if (PersistedScreens()?.Assignments.Any(a => a.BackgroundAssetId == assetId) == true) return true;
        return StoredProfiles().Any(p => p.Scene.Screens?.Assignments.Any(a => a.BackgroundAssetId == assetId) == true);
    }

    private ScreenSceneDto? PersistedScreens()
    {
        var path = Path.Combine(Store.DataRoot, "screens", "scene.json");
        if (!File.Exists(path)) return null;
        OwnedLibraryPaths.CheckAncestors(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Persisted screen references exceed their inspection budget.");
        var screens = JsonSerializer.Deserialize<ScreenSceneDto>(File.ReadAllBytes(path), ReferenceJson);
        if (screens?.Assignments is null || screens.Assignments.Any(a => a is null)) throw new InvalidDataException("Unreadable persisted screen references.");
        return screens;
    }

    private static void RequireReferenceShape(SceneDto scene)
    {
        if (scene.Assignments is null || scene.Assignments.Any(a => a is null)
            || scene.Screens is { } screens && (screens.Assignments is null || screens.Assignments.Any(a => a is null)))
            throw new InvalidDataException("Unreadable saved-scene references; nothing was reclaimed.");
    }

    private string[] PackageReferences(string contentId)
    {
        var references = new List<string>();
        if (File.Exists(Path.Combine(Store.DataRoot, SceneStateFiles.JournalName))) references.Add("场景事务等待恢复：请先重启后台完成恢复。");
        if (File.Exists(Path.Combine(Store.DataRoot, SceneStateFiles.RecoveryRequiredName))) references.Add("场景事务损坏，硬件输出已暂停：恢复完整场景并重启后台前，禁止删除场景素材。");
        void Scene(SceneDto scene, string label)
        {
            RequireReferenceShape(scene);
            if (scene.Package?.ContentId == contentId || scene.Assignments.Any(a => Refers(a.FrameSetId, contentId))
                || scene.Screens?.Assignments.Any(a => Refers(a.TimelineId, contentId)) == true) references.Add(label);
        }
        Scene(DtoMapper.ToDto(_engine.Loop.Scene), "当前灯光播放");
        if (_screenScenes?.CurrentReferences()?.Assignments.Any(a => Refers(a.TimelineId, contentId)) == true) references.Add("当前屏幕播放");
        if (_profiles?.ReadActiveForReferences() is { } active) Scene(active.Scene, "重启恢复场景");
        if (PersistedScreens()?.Assignments.Any(a => Refers(a.TimelineId, contentId)) == true) references.Add("屏幕重启恢复内容");
        foreach (var profile in StoredProfiles()) Scene(profile.Scene, "已保存场景：“" + profile.Name + "”");
        if (_hydrating.Keys.Any(id => Refers(id, contentId))) references.Add("启动时正在恢复包素材");
        return references.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool Refers(string? timeline, string contentId) => timeline is not null
        && ScenePackageIds.TryParse(timeline, out var id, out _) && id == contentId;

    private void ValidateProfileReferences(ProfileDto profile) => ValidateSceneReferences(profile.Scene);

    private void ValidateSceneReferences(SceneDto scene)
    {
        RequireReferenceShape(scene);
        if (scene.Package is { } package) ValidateTimeline(ScenePackageIds.TimelineIdOf(package.ContentId, package.BindingId));
        foreach (var assignment in scene.Assignments) ValidateTimeline(assignment.FrameSetId);
        if (scene.Screens is { } screens) ValidateScreenReferences(screens);
    }

    private void ValidateScreenReferences(ScreenSceneDto screens)
    {
        foreach (var assignment in screens.Assignments)
        {
            ValidateTimeline(assignment.TimelineId);
            if (assignment.BackgroundAssetId is { } asset && _media is not null && !_media.ContainsAsset(asset))
                throw new ArgumentException("The screen asset was removed before the scene could be saved: " + asset);
        }
    }

    private void ValidateTimeline(string? timeline)
    {
        if (!ScenePackageIds.IsTimelineId(timeline)) return;
        if (!_source.TryResolve(timeline!, out _, out _, out var problem))
            throw new ArgumentException("Package reference could not be committed; the previous scene was kept: " + problem);
    }

    private void ValidateStateReferences(IReadOnlyList<SceneStateFile> files)
    {
        foreach (var file in files)
        {
            if (file.RelativePath == "profiles/active.json")
                ValidateProfileReferences(JsonSerializer.Deserialize<ProfileDto>(file.Bytes, ReferenceJson) ?? throw new InvalidDataException("Invalid active scene."));
            else if (file.RelativePath == "screens/scene.json")
                ValidateScreenReferences(JsonSerializer.Deserialize<ScreenSceneDto>(file.Bytes, ReferenceJson) ?? throw new InvalidDataException("Invalid screen scene."));
        }
    }
}
