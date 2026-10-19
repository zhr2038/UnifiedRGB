using System.Text.Json.Serialization;

namespace UnifiedRgb.Contracts.Dto;

/// <summary>
/// One effect on one zone, or on a group of zones (rendered as one continuous strip along u in list order).
/// Colors are "#RRGGBB"; an empty list means the effect's default palette. Speed and Brightness are 0..1.
/// LoopPolicy is "LConnect" (default; also when absent, so older profiles load unchanged) or "Generic" (WP-11).
/// Strategy (NATIVE design §7.2) is "Vendor", "Program" or "Frames" and wins over LoopPolicy when present; LoopPolicy stays
/// readable for old profiles (LConnect → Vendor, Generic → Program) and is still written as the legacy alias.
/// FrameSetId names the frame set of a "Frames" assignment.
/// SpatialPath (optional; absent = the list-order concatenation) lays the targets out on explicit source stages. It is not
/// written when null, so a profile without a path serializes byte for byte as before.
/// LoopReturn (optional; absent = the historical loop) appends a hold and a reverse-flow drain after the final full frame;
/// supported only on Vendor Stacking with a SpatialPath, and likewise not written when null.
/// </summary>
public sealed record AssignmentDto(
    IReadOnlyList<ZoneRefDto> Targets,
    string EffectId,
    IReadOnlyList<string> Colors,
    float Speed = 0.5f,
    float Brightness = 1f,
    bool Reverse = false,
    uint Seed = 1,
    string? GroupName = null,
    string? LoopPolicy = null,
    string? Strategy = null,
    string? FrameSetId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpatialPathDto? SpatialPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LoopReturnDto? LoopReturn = null);

/// <summary>
/// An explicit source axis: stages in LOW-to-HIGH source order, each <c>Length</c> abstract source dots (1..720, total ≤ 720)
/// shared by its parallel bindings. Every target of the assignment is bound exactly once.
/// </summary>
public sealed record SpatialPathDto(IReadOnlyList<SpatialStageDto> Stages);

public sealed record SpatialStageDto(int Length, IReadOnlyList<SpatialBindingDto> Bindings);

/// <summary>One zone of a stage; Reverse samples the stage high-to-low before the zone's physical projection (not mounting).</summary>
public sealed record SpatialBindingDto(ZoneRefDto Target, bool Reverse = false);

/// <summary>
/// STACK-RETURN: after the final full frame, hold it HoldMs (0..2000), then drain it over DurationMs (1000..20000) with a soft
/// WaveWidthDots-wide (2..96) feather carrying two ripples of WaveStrength (0..0.5), from where the forward moving bar finished
/// back to where it started, ending on black. Milliseconds are authored physical time on the 1600 Hz RF master model.
/// </summary>
public sealed record LoopReturnDto(double DurationMs = 4000, double HoldMs = 600, int WaveWidthDots = 24, double WaveStrength = 0.25);

/// <summary>
/// The running scene. Screens (optional, OPUS-SCREENS) carries USB screen content, never RGB zones: null means "screens unchanged"
/// on SetScene/ApplyProfile and "no screen content" on GetScene; it is not written when null, so an RGB-only profile serializes
/// byte for byte as before. Package (optional, OPUS-GENERIC-SCENE-PACKAGES) pins the external package content/binding/contract the
/// scene was materialized from; likewise not written when null.
/// </summary>
public sealed record SceneDto(
    IReadOnlyList<AssignmentDto> Assignments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenSceneDto? Screens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScenePackageRefDto? Package = null);
