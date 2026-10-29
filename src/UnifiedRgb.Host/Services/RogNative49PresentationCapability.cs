namespace UnifiedRgb.Host.Services;

/// <summary>
/// Pure local installation/scope policy, separate from live CAP and physical acceptance. The trusted local native options
/// carry the one reviewed image/contract/build and qualified route. Scene content and caller-supplied expected hashes cannot
/// admit a scope. Evaluation performs no I/O and creates/refreshes no pairing, connection or input timestamps.
/// </summary>
public sealed class RogNative49PresentationCapability(KeyboardNativeMenuOptions options) : IKeyboardNativePresentationCapability
{
    public KeyboardNativePresentationSupport Evaluate(KeyboardNativePresentationScope scope) => scope is not null
        && options.MatchesScope(scope.DeviceKey, scope.FirmwareHeader, scope.ConnectionKind)
        ? KeyboardNativePresentationSupport.Available : KeyboardNativePresentationSupport.Unavailable;
}
