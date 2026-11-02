namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Specifically tagged standalone read failure; never setter or restoration verification.</summary>
public abstract class RogReadOnlyObservationException(RogReadOnlyOperation operation, string deviceKey, string connectionKind,
    byte? profileToken, string reason, Exception inner)
    : IOException("operation=" + operation + "; standalone read " + reason + "; old connection remains poisoned. " + inner.Message, inner)
{
    public RogReadOnlyOperation Operation { get; } = operation;
    public string DeviceKey { get; } = deviceKey;
    public string ConnectionKind { get; } = connectionKind;
    public byte? ProfileToken { get; } = profileToken;
}

/// <summary>Only a tagged standalone read received a fully framed unmatched wireless reply. No same-owner I/O or packet retry.</summary>
public sealed class RogReadOnlyObservationUnexpectedReplyException(RogReadOnlyOperation operation, string deviceKey,
    string connectionKind, byte? profileToken, RogOledUnexpectedReplyException inner)
    : RogReadOnlyObservationException(operation, deviceKey, connectionKind, profileToken, "received an unexpected reply", inner);
