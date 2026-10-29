using System.Collections.ObjectModel;
using System.Globalization;

namespace UnifiedRgb.Transports.Hid;

/// <summary>
/// Exact descriptor shape of one native vendor input collection whose USB interface number is not known in advance:
/// VID/PID, vendor usage page/usage, exact input report length, no output or feature report, and the exact descriptor
/// report-id set. A collection qualifies only when the <see cref="HidVendorInputRule"/> at the collection's own numbered,
/// path-consistent interface (never MI_00 or unnumbered) admits it; <see cref="RuleFor"/> derives exactly that rule, which
/// <see cref="HidVendorInputTransport.Open"/> re-checks before and after opening (report ids on the opened handle). The
/// interface number is the only field taken from the descriptor itself; it is never a wildcard for any other field, and a
/// caller still has to associate the candidate with a fresh physical container and require exactly one.
/// </summary>
public sealed class NativeInputShape
{
    private readonly byte[] _reportIds;

    public NativeInputShape(ushort vid, ushort pid, ushort usagePage, ushort usage, int inputReportLength, ReadOnlySpan<byte> inputReportIds)
    {
        // Same argument validation as an interface-specific rule (vendor page, length bounds, distinct non-zero ids).
        var probe = new HidVendorInputRule(vid, pid, 1, usagePage, usage, inputReportLength, inputReportIds);
        Vid = vid;
        Pid = pid;
        UsagePage = usagePage;
        Usage = usage;
        InputReportLength = inputReportLength;
        _reportIds = [.. probe.InputReportIds];
        InputReportIds = new ReadOnlyCollection<byte>(_reportIds);
    }

    public ushort Vid { get; }
    public ushort Pid { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }

    /// <summary>Exact native input report length including the report id byte.</summary>
    public int InputReportLength { get; }

    /// <summary>The exact descriptor-advertised input report ids, ascending.</summary>
    public IReadOnlyList<byte> InputReportIds { get; }

    /// <summary>Null when the access-0 enumeration entry has this shape on its own interface; otherwise why not.</summary>
    public string? Reject(HidCollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.Vid != Vid || collection.Pid != Pid)
            return string.Create(CultureInfo.InvariantCulture, $"device {collection.Vid:X4}:{collection.Pid:X4} is not {Vid:X4}:{Pid:X4}");
        if (collection.Interface is not { } mi || mi is < 1 or > 255 || collection.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase))
            return "MI_00 or an unnumbered interface is never opened for input";
        return At(mi).Reject(collection);
    }

    /// <summary>The exact rule at the qualified collection's own interface; throws when the collection does not qualify.</summary>
    public HidVendorInputRule RuleFor(HidCollectionInfo collection)
    {
        if (Reject(collection) is { } why) throw new InvalidOperationException("HID input collection does not have the native input shape: " + why);
        return At(collection.Interface!.Value);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Vid:X4}:{Pid:X4} UP:{UsagePage:X4} U:{Usage:X4} in {InputReportLength} out 0 feature 0 input RIDs [{string.Join(",", _reportIds)}]");

    private HidVendorInputRule At(int interfaceNumber) => new(Vid, Pid, interfaceNumber, UsagePage, Usage, InputReportLength, _reportIds);
}
