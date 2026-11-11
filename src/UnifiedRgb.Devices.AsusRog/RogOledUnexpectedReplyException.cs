namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// A complete wireless report had an unmatched header at an exchange's optional-renewal boundary.
/// It is rejected, not identity or acknowledgement evidence. The owner is poisoned; this type alone authorizes no retry.
/// </summary>
public sealed class RogOledUnexpectedReplyException(string message, Exception inner) : IOException(message, inner);
