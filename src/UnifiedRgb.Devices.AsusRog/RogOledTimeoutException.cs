namespace UnifiedRgb.Devices.AsusRog;

/// <summary>A bounded command wait expired while the pinned interface remained present.</summary>
public sealed class RogOledTimeoutException(string message, Exception innerException) : IOException(message, innerException);
