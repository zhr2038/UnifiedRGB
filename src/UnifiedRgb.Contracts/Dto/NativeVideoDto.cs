namespace UnifiedRgb.Contracts.Dto;

/// <summary>Temporary shared-clock H.264 transport, 1..60 FPS/1..60 seconds; optional existing media of the SAME loop duration.</summary>
public sealed record NativeVideoTrialRequestDto(string ScreenId, int DurationMs = 10000, int Fps = 30, string? AssetId = null);
public sealed record NativeVideoTrialDto(string TrialId, string ScreenId, int DurationMs, int Fps, string? AssetId);
