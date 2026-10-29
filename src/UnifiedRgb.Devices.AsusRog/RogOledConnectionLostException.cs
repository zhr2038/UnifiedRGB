namespace UnifiedRgb.Devices.AsusRog;

/// <summary>A transport failure accompanied by disappearance of the same pinned keyboard/receiver interface.</summary>
public sealed class RogOledConnectionLostException(string message, Exception innerException) : IOException(message, innerException);
