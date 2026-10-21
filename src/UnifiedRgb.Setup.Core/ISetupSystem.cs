using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup;

/// <summary>Facts about one file. <see cref="Sha256"/> is upper-case hex; null when the file is absent or unreadable.</summary>
public sealed record SetupFileFacts(bool Exists, long Length, string? Sha256, string? Version, string? Error = null);

/// <summary>
/// The operating-system surface setup inspection may use. Every member is read-only, except
/// <see cref="QueryRfReceiverList"/>, which sends the single literal GetDev(16,1) query to the measured receiver
/// (<see cref="RfReceiverQuery.ReadList"/>). No member writes lighting/screen/PWM data, resets USB, installs drivers or stops
/// services. Fake it in tests.
/// </summary>
public interface ISetupSystem
{
    /// <summary>Instance ids of present devices (e.g. <c>PCI\VEN_10DE&amp;DEV_2B85&amp;SUBSYS_53011462&amp;REV_A1\...</c>).</summary>
    IReadOnlyList<string> PresentDeviceInstanceIds();

    /// <summary>Present device interfaces of one class (SetupAPI, read-only).</summary>
    IReadOnlyList<UsbDeviceInfo> DeviceInterfaces(Guid interfaceClass);

    /// <summary>HID collections of one VID/PID, capabilities only (opened with no access rights, no report traffic).</summary>
    IReadOnlyList<HidCollectionInfo> HidCollections(ushort vid, ushort pid);

    /// <summary>The receiver's device list via <see cref="RfReceiverQuery.ReadList"/>; throws on any failure.</summary>
    byte[] QueryRfReceiverList(UsbDeviceInfo receiver);

    SetupFileFacts InspectFile(string fullPath);

    /// <summary>
    /// True only when neither <paramref name="fullPath"/> nor any ancestor directory can be modified, replaced or renamed by a
    /// non-administrative principal and no component is a reparse point. <paramref name="reason"/> explains a refusal.
    /// </summary>
    bool IsAdminOnlyWritable(string fullPath, out string reason);

    /// <summary>
    /// Loads the pinned PawnIOLib.dll and verifies its exported API version through the existing
    /// <c>PawnIoNative.Load</c> (pin + <c>pawnio_version</c> only; the driver is not opened). Returns the evidence text; throws
    /// when refused.
    /// </summary>
    string ProbePawnIoApi(string libraryPath, string sha256, string apiVersion);

    string ProgramFilesDirectory { get; }
}
