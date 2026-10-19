using UnifiedRgb.Core.Engine;

namespace UnifiedRgb.Host.Services;

/// <summary>Recognizes the persisted all-static-black scene, never the dark frames of an animation or a package.</summary>
internal static class WholeCaseBlackout
{
    public static bool IsRequested(Scene scene)
    {
        if (scene.Package is not null || scene.Assignments.Count == 0) return false;
        foreach (var a in scene.Assignments)
        {
            if (a.EffectId != "StaticColor" || a.FrameSetId is not null) return false;
            if (a.Params.Brightness <= 0) continue;
            if (a.Params.Colors.Count == 0) return false;
            foreach (var c in a.Params.Colors) if (c.R != 0 || c.G != 0 || c.B != 0) return false;
        }
        return true;
    }
}
