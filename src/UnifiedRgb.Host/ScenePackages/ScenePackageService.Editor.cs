using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.Generators;

namespace UnifiedRgb.Host.ScenePackages;

public sealed partial class ScenePackageService
{
    private string? _previewFileKey;
    private byte[]? _previewFileBytes;

    public async Task SaveScenePackageVariantAsync(ScenePackageSaveVariantDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_profiles is null) throw new InvalidOperationException("This Host has no profile store.");
            var package = await RequireAsync(request.ContentId, ct).ConfigureAwait(false);
            var snapshot = BindingStore.Current(request.ContentId, out _) ?? throw new InvalidOperationException("请先保存参数和设备绑定。");
            if (snapshot.BindingId != request.ExpectedBindingId) throw new InvalidOperationException("参数版本已变化，请重新读取。");
            if (!request.Overwrite && _profiles.List().Any(p => p.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("已有同名场景，请使用新名称。");
            var timeline = ScenePackageIds.TimelineIdOf(request.ContentId, snapshot.BindingId);
            var current = _engine.Loop.Scene;
            var scene = package.Manifest.Lighting.Count > 0
                ? new Scene([new Assignment(snapshot.LightingTargets(package.Manifest), StaticColorEffect.EffectId, new EffectParams([], .5f, 1), package.Manifest.Name, ContentStrategy.Frames) { FrameSetId = timeline }])
                : new Scene(current.Assignments.Where(a => !ScenePackageIds.IsTimelineId(a.FrameSetId)).ToArray());
            scene = scene with { Package = new(request.ContentId, snapshot.BindingId, snapshot.PlaybackContract) };
            var screens = await ScreenSceneOfAsync(package, snapshot, timeline, ct).ConfigureAwait(false);
            if (screens is not null) _screenScenes?.Validate(screens);
            _profiles.Save(new(request.Name, DtoMapper.ToDto(scene) with { Screens = screens }));
        }
        finally { _gate.Release(); }
    }

    public async Task<ScenePackageEditorStateDto> GetScenePackageEditorAsync(string contentId, string? bindingId = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var package = await RequireAsync(contentId, ct).ConfigureAwait(false);
            BindingSnapshot? snapshot;
            if (bindingId is null) snapshot = BindingStore.Current(contentId, out _);
            else if (!BindingStore.TryLoad(contentId, bindingId, out snapshot!, out var problem)) throw new ArgumentException(problem, nameof(bindingId));
            var problems = snapshot is null ? ["此包尚未保存设备绑定。"] : await EvaluateAsync(package, snapshot.Bindings, true, ct).ConfigureAwait(false);
            return new(contentId, snapshot?.BindingId, snapshot?.Playback ?? new(), snapshot?.Bindings.Select(ToDto).ToArray()
                ?? package.Manifest.Roles.Select(r => new ScenePackageBindingDto(r)).ToArray(), snapshot is not null && problems.Count == 0, problems,
                await Task.Run(() => BindingStore.History(contentId), ct).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    public async Task<ScenePackageEditorStateDto> SaveScenePackageEditAsync(ScenePackageEditRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var package = await RequireAsync(request.ContentId, ct).ConfigureAwait(false);
            var previous = BindingStore.Current(request.ContentId, out _);
            if (previous?.BindingId != request.ExpectedBindingId) throw new InvalidOperationException("设备绑定或参数已被其他窗口修改。请重新读取后再保存，草稿保持不变。");
            if (ScenePackagePlayback.Validate(request.Playback) is { } bad) throw new ArgumentException(bad, nameof(request));
            var (entries, problems) = Normalize(package, request.Bindings ?? []);
            if (request.Playback.Screens?.Any(e => !package.Manifest.Screens.Any(s => s.Role == e.Role)) == true) problems.Add("屏幕参数引用了此包不存在的角色。");
            if (package.Manifest.DurationMs / request.Playback.Speed > ScenePackageFormat.MaxDurationMs) problems.Add("调整后的循环时长超过 600 秒，请提高播放速度。");
            foreach (var l in package.Manifest.Lighting)
            {
                var target = entries.FirstOrDefault(e => e.Role == l.Role)?.ControllerId;
                var uploaded = _engine.Adapters.Controllers.Any(c => c.Descriptor.Id.Value == target && c.Descriptor.Capabilities.Delivery == Delivery.UploadedLoop);
                if (uploaded && !FrameSet.TryRawTicks(l.FrameDurationMs / request.Playback.Speed, out _)) problems.Add($"{l.Label ?? l.Role}：此速度无法精确表示为设备时钟，请选择其他速度。");
            }
            if (problems.Count == 0) problems.AddRange(await EvaluateAsync(package, entries, false, ct).ConfigureAwait(false));
            if (problems.Count > 0) throw new ArgumentException(string.Join("\n", problems), nameof(request));
            var snapshot = BindingStore.Save(request.ContentId, entries, request.Playback);
            var live = await EvaluateAsync(package, entries, true, ct).ConfigureAwait(false);
            return new(request.ContentId, snapshot.BindingId, snapshot.Playback ?? new(), entries.Select(ToDto).ToArray(), live.Count == 0, live,
                await Task.Run(() => BindingStore.History(request.ContentId), ct).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    public async Task<ScenePackagePreviewDto> GetScenePackagePreviewAsync(string contentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var p = await RequireAsync(contentId, ct).ConfigureAwait(false);
            var lighting = p.Manifest.Lighting.Select(l => new ScenePackagePreviewTrackDto(l.Role, l.Label ?? l.Role, l.File, l.LedCount, l.Frames,
                l.FrameDurationMs, l.ByteLength, p.Manifest.Files[l.File])).ToArray();
            var screens = p.Manifest.Screens.Select((s, i) => new ScenePackagePreviewScreenDto(s.Role, s.Label ?? s.Role, p.Media[i].Width, p.Media[i].Height, s.Fps,
                s.Overlay, s.AccentColor ?? "#5DDAC8", s.Fit, s.OverlayLayout, s.IsSequence
                    ? s.Segments.Select(c => new ScenePackagePreviewClipDto(c.File, c.DurationMs, 0, p.Manifest.Files[c.File])).ToArray()
                    : [new(s.File, s.DurationMs, 0, p.Manifest.Files[s.File])])).ToArray();
            return new(contentId, p.Manifest.Name, p.Manifest.DurationMs, lighting, screens);
        }
        finally { _gate.Release(); }
    }

    public async Task<ScenePackagePreviewChunkDto> ReadScenePackagePreviewChunkAsync(ScenePackagePreviewChunkRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || request.Bytes is < 1 or > 49152) throw new ArgumentException("预览分块范围无效。", nameof(request));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var p = await RequireAsync(request.ContentId, ct).ConfigureAwait(false);
            if (!p.Manifest.Lighting.Any(l => l.File == request.File) && !p.Manifest.Screens.Any(s => s.MediaFiles.Contains(request.File, StringComparer.Ordinal)))
                throw new ArgumentException("请求的文件不是此包的灯光或屏幕内容。", nameof(request));
            byte[] bytes;
            if (p.Lighting.TryGetValue(request.File, out var lighting)) bytes = lighting;
            else
            {
                var key = request.ContentId + "/" + request.File;
                if (_previewFileKey != key || _previewFileBytes is null)
                {
                    _previewFileBytes = await Task.Run(() => p.ReadFile(request.File), ct).ConfigureAwait(false);
                    _previewFileKey = key;
                }
                bytes = _previewFileBytes;
            }
            if (request.Offset > bytes.LongLength) throw new ArgumentException("预览偏移超出文件范围。", nameof(request));
            var count = (int)Math.Min(request.Bytes, bytes.LongLength - request.Offset);
            return new(request.Offset, bytes.LongLength, p.Manifest.Files[request.File], Convert.ToBase64String(bytes, (int)request.Offset, count));
        }
        finally { _gate.Release(); }
    }
}
