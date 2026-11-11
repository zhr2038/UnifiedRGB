namespace UnifiedRgb.Devices.AsusRog;

/// <summary>An exact negative envelope received during a standalone getter; its originating opcode is not inferred.</summary>
public sealed class RogReadOnlyObservationEnvelopeRefusedException : RogReadOnlyObservationException
{
    public RogReadOnlyObservationEnvelopeRefusedException(RogReadOnlyOperation operation, string deviceKey,
        byte? profileToken, RogOledEnvelopeRefusedException inner) : this(operation, deviceKey, "wireless", profileToken, inner) { }

    public RogReadOnlyObservationEnvelopeRefusedException(RogReadOnlyOperation operation, string deviceKey,
        string connectionKind, byte? profileToken, RogOledEnvelopeRefusedException inner)
        : base(operation, deviceKey, connectionKind, profileToken,
            "received a negative envelope; no old command will be replayed", inner) { }
}
