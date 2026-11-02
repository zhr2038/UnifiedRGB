using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Services;

internal sealed class KeyboardTransportChangedException(Exception? cause = null)
    : IOException("The exact configured USB keyboard appeared; retire the old RF owner without replaying its settings and acquire the USB route.", cause);

/// <summary>A passive exact-route observation. It never opens a control writer or sends a HID report.</summary>
internal sealed class KeyboardTransportSwitch(TimeProvider time, Func<bool> wiredPresent)
{
    private long? _last;
    private bool _detected;
    internal bool Observe(bool force = false)
    {
        if (_detected) return true;
        var now = time.GetTimestamp();
        if (!force && _last is { } prior && time.GetElapsedTime(prior, now) < TimeSpan.FromMilliseconds(100)) return false;
        _last = now;
        return _detected = wiredPresent();
    }
    internal void Check()
    {
        if (Observe()) throw new KeyboardTransportChangedException();
    }
}

public sealed partial class KeyboardDisplayService
{
    private sealed record RetiredRouteFault(IRogOledConnection Connection, long Intent, long NativeIntent);
    private RetiredRouteFault? _retiredRouteFault;

    private void RememberRetiredRouteFault(IRogOledConnection? connection)
    {
        // RunOwnerWithObservationRecovery and the restore-only branch have disposed their old owner before this catch.
        // A failed input join deliberately retains the native actor and cannot authorize any replacement.
        _retiredRouteFault = connection is null || _nativeMenu?.OwnerDetached == false ? null
            : new(connection, Interlocked.Read(ref _intentRevision), _nativeMenu?.RequestRevision ?? 0);
    }

    private bool RetiredRouteChangeProven()
    {
        var retired = _retiredRouteFault;
        if (retired is null || retired.Intent != Interlocked.Read(ref _intentRevision)
            || retired.NativeIntent != (_nativeMenu?.RequestRevision ?? 0) || _nativeMenu?.OwnerDetached == false
            || (!_enabled && !_options.KeyboardDisplay.HasVerifiedLightingRoute && _nativeMenu?.Requested != true)
            || _configurationGate is { RogAcquisitionAllowed: false } || _configurationGate?.FreshResumeAllowed == true) return false;
        // PnP can be incomplete at the instant a write fails. Only a later COMPLETE passive observation of the original
        // route's absence (or an exact new USB route) changes the sticky outcome; no report or old owner is reopened.
        try
        {
            if (!retired.Connection.PhysicalRouteAbsent
                && !(retired.Connection.ConnectionKind == "wireless" && WiredSwitchKey is { } key && CanSwitchToKnownWired(key))) return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        _retiredRouteFault = null;
        return true;
    }

    private string? WiredSwitchKey => _nativeMenu?.QualifiedRoutes.FirstOrDefault(route => route.ConnectionKind == "wired")?.DeviceKey
        ?? (HasWiredPins(_options.KeyboardDisplay) ? _options.KeyboardDisplay.DeviceKey : null);

    private bool CanSwitchToKnownWired(string key)
    {
        if (_configurationGate is { RogAcquisitionAllowed: false } || _configurationGate?.FreshResumeAllowed == true) return false;
        try { return _wiredRoutePresent(key); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } // unknown enumeration is never a route-change proof
    }

    private static bool ExactWiredPresent(string key)
    {
        var matches = new WinHidReportDevices().EnumerateComplete(0x0B05, 0x1C24)
            .Where(c => RogWiredOledProtocol.Matches(c) && string.Equals(RogWiredOledProtocol.DeviceKey(c), key, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1;
    }
}
