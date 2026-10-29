namespace UnifiedRgb.Devices.AsusRog;

public enum RogLeverDirection { Up = 1, Middle = 2, Down = 4 }
public enum RogLeverReadiness { Disarmed, AwaitingNeutral, Ready, Held, Faulted }

/// <summary>
/// Pure edge decoder for the owner-verified OMNI1ACE/AzothX1C25 vendor notification contract:
/// native21-byte/RID4/72/mask/zero extension. No device handle, ordinary-key decoding, remap or action execution.
/// A caller must independently bind the exact receiver/TLC, authorize a control mode and supply a fresh session.
/// Starting observation alone never enables a case-power action. Timeouts never synthesize a release.
/// </summary>
public sealed class RogLeverDecoder
{
    private long _generation;
    private TimeSpan _lastTimestamp;
    private byte _held;
    public RogLeverReadiness Readiness { get; private set; } = RogLeverReadiness.Disarmed;

    /// <summary>A new explicit session consumes a real neutral report before accepting any press.</summary>
    public void Begin(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        _generation = generation; _lastTimestamp = TimeSpan.Zero; _held = 0;
        Readiness = RogLeverReadiness.AwaitingNeutral;
    }

    public void Disarm() { _held = 0; Readiness = RogLeverReadiness.Disarmed; }

    /// <summary>Returns at most one direction per press/release pair; repeated held reports cannot repeat an action.</summary>
    public RogLeverDirection? Read(ReadOnlySpan<byte> nativeReport, long generation, TimeSpan timestamp)
    {
        if (Readiness is RogLeverReadiness.Disarmed or RogLeverReadiness.Faulted) return null;
        if (generation != _generation || timestamp < _lastTimestamp || timestamp < TimeSpan.Zero)
            return Fault();
        _lastTimestamp = timestamp;
        if (nativeReport.Length != 21 || nativeReport[0] != 4) return Fault();
        // Discard common/unknown report bodies without inspecting keycodes or retaining bytes.
        if (nativeReport[1] != 0x72) return null;
        var mask = nativeReport[2];
        if (mask is not (0 or 1 or 2 or 4) || nativeReport[3..].ContainsAnyExcept((byte)0)) return Fault();
        if (mask == 0)
        {
            _held = 0; Readiness = RogLeverReadiness.Ready; return null;
        }
        if (Readiness == RogLeverReadiness.AwaitingNeutral) return null;
        if (_held == mask) return null;
        if (_held != 0) return Fault(); // Different direction without a physical neutral is not a new gesture.
        _held = mask; Readiness = RogLeverReadiness.Held;
        return (RogLeverDirection)mask;
    }

    private RogLeverDirection? Fault() { _held = 0; Readiness = RogLeverReadiness.Faulted; return null; }
}
