namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Exact negative envelope received in the explicit ephemeral native67/1 role; no originating opcode is inferred.</summary>
public sealed class RogNativeImageNackException(string message) : IOException(message);

/// <summary>The native source joined/validated its aborted image context; service RGB without restoring or renewing that image.</summary>
public sealed class RogNativeImageContinuationException(string message, Exception inner) : IOException(message, inner);
