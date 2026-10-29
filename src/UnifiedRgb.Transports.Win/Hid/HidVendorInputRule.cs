using System.Collections.ObjectModel;
using System.Globalization;

namespace UnifiedRgb.Transports.Hid;

/// <summary>
/// Attributes and caps measured on an OPENED read handle (<c>HidD_GetAttributes</c>, <c>HidP_GetCaps</c> and the input
/// button/value caps), not copied from the access-0 enumeration snapshot. Report lengths include the report id byte.
/// </summary>
public sealed class HidInputDescriptor
{
    public HidInputDescriptor(ushort vid, ushort pid, ushort usagePage, ushort usage, int inputReportLength, int outputReportLength,
        int featureReportLength, ReadOnlySpan<byte> inputReportIds)
    {
        Vid = vid;
        Pid = pid;
        UsagePage = usagePage;
        Usage = usage;
        InputReportLength = inputReportLength;
        OutputReportLength = outputReportLength;
        FeatureReportLength = featureReportLength;
        var ids = new SortedSet<byte>();
        foreach (var id in inputReportIds) ids.Add(id);
        InputReportIds = new ReadOnlyCollection<byte>([.. ids]);
    }

    public ushort Vid { get; }
    public ushort Pid { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int InputReportLength { get; }
    public int OutputReportLength { get; }
    public int FeatureReportLength { get; }

    /// <summary>Distinct report ids of the input button and value caps, ascending.</summary>
    public IReadOnlyList<byte> InputReportIds { get; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Vid:X4}:{Pid:X4} UP:{UsagePage:X4} U:{Usage:X4} in {InputReportLength} out {OutputReportLength} feature {FeatureReportLength} input RIDs [{string.Join(",", InputReportIds)}]");
}

/// <summary>
/// The one vendor-defined, input-only HID collection a <see cref="HidVendorInputTransport"/> may open. Every field is
/// mandatory: VID/PID, the <c>MI_xx</c> interface (never MI_00), a vendor-defined usage page (0xFF00-0xFFFF, so ordinary
/// keyboard/mouse/consumer collections can never be described), usage, the exact input report length, no output or feature
/// reports, and the exact set of report ids the descriptor may advertise. The same rule is checked against the access-0
/// enumeration before opening and against the opened read handle afterwards; there is no wildcard.
/// </summary>
public sealed class HidVendorInputRule
{
    public const int MaximumInputReportLength = 64;
    public const int MaximumReportIds = 8;

    private readonly byte[] _reportIds;

    public HidVendorInputRule(ushort vid, ushort pid, int interfaceNumber, ushort usagePage, ushort usage, int inputReportLength,
        ReadOnlySpan<byte> inputReportIds)
    {
        if (vid == 0 || pid == 0) throw new ArgumentException("An exact non-zero VID and PID are required.", nameof(vid));
        ArgumentOutOfRangeException.ThrowIfLessThan(interfaceNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interfaceNumber, 255);
        if (!IsVendorUsagePage(usagePage))
            throw new ArgumentOutOfRangeException(nameof(usagePage), "Only vendor-defined usage pages (0xFF00-0xFFFF) can be opened for input.");
        ArgumentOutOfRangeException.ThrowIfLessThan(inputReportLength, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputReportLength, MaximumInputReportLength);
        if (inputReportIds.Length is < 1 or > MaximumReportIds)
            throw new ArgumentException("One to eight exact input report ids are required.", nameof(inputReportIds));
        var ids = new SortedSet<byte>();
        foreach (var id in inputReportIds)
        {
            if (id == 0 || !ids.Add(id)) throw new ArgumentException("Input report ids must be distinct and non-zero.", nameof(inputReportIds));
        }

        Vid = vid;
        Pid = pid;
        Interface = interfaceNumber;
        UsagePage = usagePage;
        Usage = usage;
        InputReportLength = inputReportLength;
        _reportIds = [.. ids];
        InputReportIds = new ReadOnlyCollection<byte>(_reportIds);
    }

    public ushort Vid { get; }
    public ushort Pid { get; }
    public int Interface { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }

    /// <summary>Exact native input report length including the report id byte (the descriptor maximum).</summary>
    public int InputReportLength { get; }

    /// <summary>The exact descriptor-advertised input report ids, ascending.</summary>
    public IReadOnlyList<byte> InputReportIds { get; }

    /// <summary>HID usage tables 1.x: 0xFF00-0xFFFF is vendor-defined. Generic Desktop, Keyboard and Consumer pages are below it.</summary>
    public static bool IsVendorUsagePage(ushort usagePage) => usagePage >= 0xFF00;

    public bool Admits(byte reportId) => Array.IndexOf(_reportIds, reportId) >= 0;

    /// <summary>Null when the access-0 enumeration entry is exactly this collection; otherwise why it must not be opened.</summary>
    public string? Reject(HidCollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.Vid != Vid || collection.Pid != Pid) return Invariant($"device {collection.Vid:X4}:{collection.Pid:X4} is not {Vid:X4}:{Pid:X4}");
        if (collection.Interface is null or 0 || collection.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase)) return "MI_00 or an unnumbered interface is never opened for input";
        if (HidCollectionInfo.InterfaceOf(collection.Path) != collection.Interface) return "the interface number does not match the enumerated path";
        if (collection.Interface != Interface) return Invariant($"interface MI_{collection.Interface:X2} is not MI_{Interface:X2}");
        if (collection.Error is not null) return "caps unavailable";
        if (!IsVendorUsagePage(collection.UsagePage)) return "not a vendor-defined usage page; ordinary HID input is never opened";
        if (collection.UsagePage != UsagePage || collection.Usage != Usage) return Invariant($"usage {collection.UsagePage:X4}/{collection.Usage:X4} is not {UsagePage:X4}/{Usage:X4}");
        if (collection.InputReportLength != InputReportLength) return Invariant($"input length {collection.InputReportLength} is not {InputReportLength}");
        if (collection.OutputReportLength != 0 || collection.FeatureReportLength != 0) return "not an input-only collection (output or feature reports present)";
        return null;
    }

    /// <summary>Null when the opened handle still describes exactly this collection; otherwise the descriptor drift.</summary>
    public string? Reject(HidInputDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Vid != Vid || descriptor.Pid != Pid) return "opened device identity differs";
        if (!IsVendorUsagePage(descriptor.UsagePage) || descriptor.UsagePage != UsagePage || descriptor.Usage != Usage) return "opened collection usage differs";
        if (descriptor.InputReportLength != InputReportLength) return "opened input report length differs";
        if (descriptor.OutputReportLength != 0 || descriptor.FeatureReportLength != 0) return "opened collection has output or feature reports";
        if (!descriptor.InputReportIds.SequenceEqual(_reportIds)) return "opened collection advertises a different input report id set";
        return null;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Vid:X4}:{Pid:X4} MI_{Interface:X2} UP:{UsagePage:X4} U:{Usage:X4} in {InputReportLength} out 0 feature 0 input RIDs [{string.Join(",", _reportIds)}]");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
