namespace UnifiedRgb.Core.Engine;

/// <summary>Immutable list of assignments. Later assignments win where targets overlap.</summary>
public sealed record Scene(IReadOnlyList<Assignment> Assignments)
{
    public static Scene Empty { get; } = new(Array.Empty<Assignment>());

    /// <summary>
    /// OPUS-GENERIC-SCENE-PACKAGES: the external package this scene was materialized from (immutable content, immutable binding
    /// snapshot, playback contract); null for every other scene. Provenance only: playback reads the frame sets themselves.
    /// </summary>
    public ScenePackageProvenance? Package { get; init; }
}

/// <summary>The pinned origin of a package scene: content address, binding snapshot hash and versioned playback contract.</summary>
public sealed record ScenePackageProvenance(string ContentId, string BindingId, string PlaybackContract);
