namespace UnifiedRgb.Contracts.Dto;

/// <summary>A saved scene. Stored as <c>profiles\&lt;Name&gt;.json</c>; the running scene is mirrored to <c>profiles\active.json</c>.</summary>
public sealed record ProfileDto(string Name, SceneDto Scene, int SchemaVersion = ProfileDto.CurrentSchema)
{
    public const int CurrentSchema = 1;
}

public sealed record ProfileInfoDto(string Name, DateTimeOffset Modified);
