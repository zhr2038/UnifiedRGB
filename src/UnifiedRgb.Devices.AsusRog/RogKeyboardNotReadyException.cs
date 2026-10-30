namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// The first read-only identity of a new wireless owner had a valid receiver envelope but no keyboard firmware (the RF peer was
/// not linked yet). Raised before identity acceptance and before any write; that owner is closed, never refreshed or reused.
/// Malformed, refused, timed-out or changed identities keep their own exceptions.
/// </summary>
public sealed class RogKeyboardNotReadyException(string message) : IOException(message);
