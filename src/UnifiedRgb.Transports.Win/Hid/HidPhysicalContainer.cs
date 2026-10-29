using System.Security.Cryptography;

namespace UnifiedRgb.Transports.Hid;

/// <summary>
/// Opaque identity of the physical PnP container (<c>DEVPKEY_Device_ContainerId</c>) of one enumerated HID interface, with
/// the monotonic <see cref="TimeProvider"/> timestamp at which it was resolved. Only the platform resolver in this assembly
/// can create one, and never from a VID/PID or path text. The container GUID is keyed with a per-process random secret, so
/// the value is comparable within this process but is neither the GUID nor a stable cross-process identifier; nothing about
/// it is printed.
/// </summary>
public sealed class HidPhysicalContainer
{
    /// <summary>Windows assigns this container to devices that are part of the computer itself; it does not identify a peripheral.</summary>
    internal static readonly Guid LocalMachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _opaque;

    private HidPhysicalContainer(byte[] opaque, long observedTimestamp)
    {
        _opaque = opaque;
        ObservedTimestamp = observedTimestamp;
    }

    /// <summary><see cref="TimeProvider.GetTimestamp"/> of the resolver's clock right after the platform metadata was read.</summary>
    public long ObservedTimestamp { get; }

    /// <summary>True when both resolutions read the same platform container (timestamps are not compared).</summary>
    public bool SameContainer(HidPhysicalContainer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CryptographicOperations.FixedTimeEquals(_opaque, other._opaque);
    }

    public override string ToString() => "HID physical container (opaque)";

    /// <summary>A specific peripheral container: not the empty GUID and not the local-machine container.</summary>
    internal static bool IsSpecific(Guid containerId) => containerId != Guid.Empty && containerId != LocalMachineContainer;

    /// <summary>Only for a GUID read from the platform container property (or a synthetic one in tests).</summary>
    internal static HidPhysicalContainer FromPlatformContainerId(Guid containerId, long observedTimestamp)
    {
        if (!IsSpecific(containerId)) throw new ArgumentException("Not a specific physical container.", nameof(containerId));
        Span<byte> raw = stackalloc byte[16];
        if (!containerId.TryWriteBytes(raw)) throw new InvalidOperationException("Container GUID could not be serialized.");
        return new HidPhysicalContainer(HMACSHA256.HashData(ProcessKey, raw), observedTimestamp);
    }
}

/// <summary>Result of resolving one interface: a container, or why it is unavailable (no path, serial or GUID in the text).</summary>
public sealed class HidContainerResolution
{
    private HidContainerResolution(HidPhysicalContainer? container, string? failure)
    {
        Container = container;
        Failure = failure;
    }

    public HidPhysicalContainer? Container { get; }
    public string? Failure { get; }

    public static HidContainerResolution Resolved(HidPhysicalContainer container) =>
        new(container ?? throw new ArgumentNullException(nameof(container)), null);

    public static HidContainerResolution Unavailable(string failure) =>
        new(null, string.IsNullOrWhiteSpace(failure) ? throw new ArgumentException("A reason is required.", nameof(failure)) : failure);
}

/// <summary>
/// Platform physical-container metadata for one requested, enumerated HID interface. Implementations query only that
/// interface; they never enumerate other devices, start processes, open the device or derive a container from VID/PID or
/// path strings. Unavailable, malformed or non-present metadata is a failure, never a guess.
/// </summary>
public interface IHidContainerResolver
{
    HidContainerResolution Resolve(HidCollectionInfo collection);
}
