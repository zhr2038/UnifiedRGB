namespace UnifiedRgb.Contracts.Dto;

/// <summary>ACK counters are communication evidence, not optical acknowledgement.</summary>
public sealed record KeyboardDisplayStatusDto(bool Enabled, string State, string Detail, long FramesAcknowledged,
    long RenewalsWritten, DateTimeOffset? UpdatedAt, string? LastError);
