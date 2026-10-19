namespace UnifiedRgb.Screens.Runtime;

/// <summary>
/// The RF side of the manual fan-LCD group restart, implemented by the Host over its own Lian Li adapter (the Screens project
/// stays independent of it). Optional: without one, RestartFanLcdAsync refuses with a Precheck result.
/// </summary>
public interface IFanLcdGroupRecovery
{
    /// <summary>No-I/O precondition for <paramref name="controllerId"/> driving <paramref name="lcdFans"/> LCD fans; throws
    /// <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/> with the reason.</summary>
    void Validate(string controllerId, int lcdFans);

    /// <summary>
    /// Sends the group reset and reports the receiver's acknowledgement. Throws (InvalidOperation/Argument/IO/Timeout/
    /// InvalidData) only BEFORE <paramref name="beforeFirstSend"/> runs; after it every failure is a non-accepted result.
    /// Cancellation throws <see cref="OperationCanceledException"/> at any point.
    /// </summary>
    Task<FanLcdGroupResetResult> ResetAsync(string controllerId, int lcdFans, Action beforeFirstSend, CancellationToken ct);
}

/// <summary>Accepted = fresh receiver report of the new command sequence; Attempts = RF sends; Detail = English evidence.</summary>
public sealed record FanLcdGroupResetResult(bool Accepted, int Attempts, string Detail);
