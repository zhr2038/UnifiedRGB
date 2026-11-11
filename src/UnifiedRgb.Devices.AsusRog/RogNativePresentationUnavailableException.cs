namespace UnifiedRgb.Devices.AsusRog;

/// <summary>A scoped ephemeral output failed; settings mutations and mandatory readbacks are outside this classification.</summary>
public sealed class RogNativePresentationUnavailableException : IOException
{
    internal RogNativePresentationUnavailableException(string deviceKey, byte profileToken, string phase, Exception inner)
        : this(deviceKey, "wireless", profileToken, phase, inner) { }

    internal RogNativePresentationUnavailableException(string deviceKey, string connectionKind, byte profileToken, string phase, Exception inner)
        : base("Ephemeral native presentation transport failed; phase=" + phase + "; close the old owner before a fresh snapshot. " + inner.Message, inner)
    { DeviceKey = deviceKey; ConnectionKind = connectionKind; ProfileToken = profileToken; Phase = phase; }
    public string DeviceKey { get; }
    public string ConnectionKind { get; }
    public byte ProfileToken { get; }
    public string Phase { get; }
}
