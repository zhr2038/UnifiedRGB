namespace UnifiedRgb.Devices.AsusRog;

/// <summary>A valid RF BasicInfo response named no keyboard during a standalone read; never a setter or mandatory verification.</summary>
public sealed class RogReadOnlyObservationPeerUnavailableException(RogReadOnlyOperation operation, string deviceKey,
    byte profileToken)
    : RogReadOnlyObservationException(operation, deviceKey, "wireless", profileToken, "found the keyboard temporarily unavailable",
        new IOException("The RF receiver returned a complete BasicInfo envelope without keyboard identity; retire this owner and wait for a fresh identity."));
