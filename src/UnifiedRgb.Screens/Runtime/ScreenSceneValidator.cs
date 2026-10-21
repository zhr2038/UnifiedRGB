using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Whole-candidate validation (ArgumentException naming the first problem); nothing is applied by validating.</summary>
public static class ScreenSceneValidator
{
    public const int MaxAssignments = 16;

    /// <summary>Longest accepted <see cref="ScreenAssignmentDto.TimelineId"/> (a package timeline id is 137 characters).</summary>
    public const int MaxTimelineIdLength = 160;

    public static IReadOnlyList<string> Overlays { get; } = ["none", "compact", "dashboard"];

    public static IReadOnlyList<string> Fits { get; } = ["cover", "contain"];

    /// <param name="assetExists">Null skips the asset-existence check (structural validation only).</param>
    public static void Validate(ScreenSceneDto? scene, Func<string, bool>? assetExists)
    {
        if (scene?.Assignments is null)
        {
            throw new ArgumentException("A screen scene needs an assignment list (it may be empty).", nameof(scene));
        }

        if (scene.Assignments.Count > MaxAssignments)
        {
            throw new ArgumentException($"A screen scene may hold at most {MaxAssignments} assignments.", nameof(scene));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in scene.Assignments)
        {
            ValidateAssignment(a, assetExists);
            if (!seen.Add(a.ScreenId))
            {
                throw new ArgumentException($"Screen {a.ScreenId} is assigned twice.", nameof(scene));
            }
        }
    }

    public static void ValidateAssignment(ScreenAssignmentDto? a, Func<string, bool>? assetExists)
    {
        if (a is null)
        {
            throw new ArgumentException("A screen assignment is null.", nameof(a));
        }

        if (!ScreenIds.TryParse(a.ScreenId, out var family, out _))
        {
            throw new ArgumentException($"'{a.ScreenId}' is not a screen id (screen:tl-lcd|h2|turzx:<instance>).", nameof(a));
        }

        if (!ScreenColor.IsValid(a.BackgroundColor) || !ScreenColor.IsValid(a.AccentColor))
        {
            throw new ArgumentException($"{a.ScreenId}: colours must be #RRGGBB.", nameof(a));
        }

        if (a.Overlay is null || !Overlays.Contains(a.Overlay))
        {
            throw new ArgumentException($"{a.ScreenId}: overlay must be none, compact or dashboard.", nameof(a));
        }

        if (a.Fit is null || !Fits.Contains(a.Fit))
        {
            throw new ArgumentException($"{a.ScreenId}: fit must be cover or contain.", nameof(a));
        }

        if (a.Rotation is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentException($"{a.ScreenId}: rotation must be 0, 90, 180 or 270.", nameof(a));
        }

        if (!double.IsFinite(a.Brightness) || a.Brightness < 0 || a.Brightness > 1)
        {
            throw new ArgumentException($"{a.ScreenId}: brightness must be within 0..1.", nameof(a));
        }

        if (a.Fps is < 1 or > 60)
        {
            throw new ArgumentException($"{a.ScreenId}: fps must be within 1..60.", nameof(a));
        }

        if (a.TimelineId is { } timeline && (timeline.Length is 0 or > MaxTimelineIdLength || !timeline.All(c => c is > ' ' and <= '~')))
        {
            throw new ArgumentException($"{a.ScreenId}: a timeline id must be 1..{MaxTimelineIdLength} printable ASCII characters.", nameof(a));
        }

        if (a.OverlayLayout is { } layout)
        {
            if (a.Overlay != OverlayLayouts.RegionOverlay)
            {
                throw new ArgumentException($"{a.ScreenId}: an overlay region needs the {OverlayLayouts.RegionOverlay} overlay (overlay is {a.Overlay}).", nameof(a));
            }

            if (OverlayRenderer.ShapeOf(family.Family) != OverlayShape.Rectangle)
            {
                throw new ArgumentException(
                    $"{a.ScreenId}: an overlay region is supported on rectangular screens only, not this round {family.Family} panel (never dropped or widened).",
                    nameof(a));
            }

            if (OverlayLayouts.Problem(layout) is { } why)
            {
                throw new ArgumentException($"{a.ScreenId}: overlay region invalid: {why}.", nameof(a));
            }
            if (OverlayLayouts.TargetProblem(a.ScreenId, layout, a.Rotation) is { } targetProblem)
            {
                throw new ArgumentException($"{a.ScreenId}: {targetProblem}.", nameof(a));
            }
        }

        if (a.BackgroundAssetId is { } id)
        {
            if (!AssetStore.IsValidId(id))
            {
                throw new ArgumentException($"{a.ScreenId}: '{id}' is not an asset id.", nameof(a));
            }

            if (assetExists is not null && !assetExists(id))
            {
                throw new ArgumentException($"{a.ScreenId}: asset {id} does not exist.", nameof(a));
            }
        }
    }
}

/// <summary>The persisted screen scene (&lt;DataDirectory&gt;\screens\scene.json), separate from the RGB active.json.</summary>
internal sealed partial class ScreenSceneStore(string path, ILogger log)
{
    public const int MaxBytes = 256 * 1024;

    public string Path => path;

    /// <summary>The stored scene when it exists and is structurally valid; otherwise an empty scene (logged).</summary>
    public ScreenSceneDto Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ScreenSceneDto([]);
            }

            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                LogIgnored(log, path, $"larger than {MaxBytes} bytes");
                return new ScreenSceneDto([]);
            }

            var scene = JsonSerializer.Deserialize(File.ReadAllBytes(path), ContractsJsonContext.Default.ScreenSceneDto);
            ScreenSceneValidator.Validate(scene, null);
            return scene!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LogIgnored(log, path, ex.Message);
            return new ScreenSceneDto([]);
        }
    }

    /// <summary>Atomic replace (temp file + rename). I/O errors propagate to the caller, which logs them.</summary>
    public void Save(ScreenSceneDto scene)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(scene, ContractsJsonContext.Default.ScreenSceneDto);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen scene {Path} ignored: {Reason}")]
    private static partial void LogIgnored(ILogger logger, string path, string reason);
}
