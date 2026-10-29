namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// One owned keyboard OLED control route. Requests and replies at this boundary use the measured wired 65-byte/RID0
/// representation; wireless connections translate the report envelope and expose their smaller pixel-chunk capacity.
/// </summary>
public interface IRogOledConnection : IDisposable
{
    string DeviceKey { get; }
    string FirmwareHeader { get; }
    string ConnectionKind { get; }
    int PixelPayloadCapacity { get; }
    // Descriptor/pairing evidence captured by this existing control owner; accessing it opens no control handle.
    RogNativeControlBinding? NativeControlBinding => null;
    bool SameDevice();
    void RequireFirmwareIdentity(byte[] identity, string expectedHeader);
    Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct);
    Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct);

    /// <summary>
    /// RF pairing evidence of THIS exclusive control owner. Synchronous snapshot; no I/O, handle, query or timer.
    /// Null means "no RF evidence" (wired owners and every existing implementation), exactly like NativeControlBinding.
    /// </summary>
    RogPairingEvidence? PairingEvidence => null;
}

/// <summary>Connection-generation binding; RF pairing was verified before this exclusive control owner was opened.</summary>
public sealed record RogNativeControlBinding(IReadOnlyList<UnifiedRgb.Transports.Hid.HidCollectionInfo> Controls,
    RogPairedDevice? Keyboard, long PairingTimestamp);

/// <summary>
/// Immutable snapshot of one connection generation's RF pairing evidence. Keyboard and PairingTimestamp are the initial
/// provenance from NativeControlBinding and never change. LastObservedTimestamp and Observations advance only after a
/// validated reply to an exchange this same owner sent. Invalidated is terminal: once non-null it never clears, and the
/// other fields freeze. All timestamps come from the owner's EvidenceTime (TimeProvider.GetTimestamp).
/// </summary>
public sealed record RogPairingEvidence(RogPairedDevice Keyboard, long PairingTimestamp, long LastObservedTimestamp,
    long Observations, string? Invalidated);
