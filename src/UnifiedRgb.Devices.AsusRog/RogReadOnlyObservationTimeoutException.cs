namespace UnifiedRgb.Devices.AsusRog;

public enum RogReadOnlyOperation { IdleBrightnessObservation, IdleScopeObservation, PreKeyFrameScope, NativeStepCapability }

/// <summary>Only a specifically tagged standalone read timed out. Setter/restoration verification never uses this classification.</summary>
public sealed class RogReadOnlyObservationTimeoutException(RogReadOnlyOperation operation, string deviceKey, string connectionKind, byte? profileToken, Exception inner)
    : RogReadOnlyObservationException(operation, deviceKey, connectionKind, profileToken, "timed out", inner);
