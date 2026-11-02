namespace UnifiedRgb.Devices.AsusRog;

/// <summary>An exact RF FF/AA negative envelope was received. It has no originating opcode; the connection stays poisoned.</summary>
public sealed class RogOledEnvelopeRefusedException(string message, Exception? inner = null) : IOException(message, inner);
