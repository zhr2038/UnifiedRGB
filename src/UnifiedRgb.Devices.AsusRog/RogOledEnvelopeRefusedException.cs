namespace UnifiedRgb.Devices.AsusRog;

/// <summary>An exact FF/AA negative envelope was received. It has no originating opcode; the connection stays poisoned.</summary>
public sealed class RogOledEnvelopeRefusedException(string message, Exception? inner = null) : IOException(message, inner)
{
    internal RogOledEnvelopeRefusedException(string message, Exception inner, byte unsentCommand) : this(message, inner)
        => UnsentCommand = unsentCommand;

    /// <summary>The proposed command, when the transport proved the refusal arrived before it could be sent.</summary>
    public byte? UnsentCommand { get; }
}
