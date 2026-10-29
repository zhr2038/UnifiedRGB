using System.Globalization;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Services;

/// <summary>A selected native notice collection and the exact rule derived at its own interface.</summary>
internal sealed record NativeInputSelection(HidCollectionInfo Collection, HidVendorInputRule Rule);

/// <summary>
/// Selects the native notice collection for the one borrowed keyboard control owner. The shapes are Root's actual
/// metadata-only snapshot (evidence/ACTUAL-ROOT-METADATA.json): wired 0B05:1C24 FFC0/1 in21 out0 feature0 input RIDs [3],
/// enumerated on MI02 (COL03), and RF 0B05:1ACE FFC0/1 in21 out0 feature0 input RIDs [4], enumerated on MI03 (COL04) beside
/// FFC1/FFC2 collections of the same length. The interface number is taken from the single qualified candidate; there is no
/// fixed MI03 and no wildcard for any other field. A candidate must not share an interface with the control owner, must
/// resolve to a fresh physical container (an unresolved or stale eligible candidate fails the whole selection, because it
/// could be on the control's container) and exactly one candidate may share the control's container. Nothing is opened here.
/// </summary>
internal static class NativeInputDiscovery
{
    public const int MaximumDiagnostics = 24;

    public static NativeInputShape Wired { get; } = new(0x0B05, 0x1C24, 0xFFC0, 1, 21, [3]);
    public static NativeInputShape Receiver { get; } = new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, [4]);

    /// <summary>
    /// Native transport of the existing control owner: 1 wired, 2 RF, 0 none. It comes from the opened owner's actual route
    /// (its connection kind and OLED payload capacity) and, when captured, its control descriptors, which must agree: wired is
    /// the 1C24 MI01 FF00/1 65/65 control of this device key with no paired keyboard; RF is 1ACE MI02 control collections.
    /// The live CAP transport byte then confirms it (<see cref="RogNativeMenuCodec.ParseCapability"/>). No firmware header,
    /// build, image or contract value takes part. Without a captured binding only the existing capability read can happen;
    /// input binding and ARM still require the binding.
    /// </summary>
    public static byte Route(string kind, int payloadCapacity, string deviceKey, RogNativeControlBinding? binding)
    {
        var candidate = (kind, payloadCapacity) switch { ("wired", 60) => (byte)1, ("wireless", 59) => (byte)2, _ => (byte)0 };
        if (candidate == 0 || binding is null) return candidate;
        if (binding.Controls.Count == 0 || candidate == 1 && binding.Keyboard is not null) return 0;
        foreach (var control in binding.Controls)
        {
            var agrees = candidate == 1
                ? RogWiredOledProtocol.Matches(control) && string.Equals(RogWiredOledProtocol.DeviceKey(control), deviceKey, StringComparison.OrdinalIgnoreCase)
                : control is { Vid: 0x0B05, Pid: 0x1ACE, Interface: 2 };
            if (!agrees) return 0;
        }
        return candidate;
    }

    public static NativeInputShape ShapeFor(byte transport) => transport switch
    {
        1 => Wired,
        2 => Receiver,
        _ => throw new InvalidDataException("Native input transport is not verified."),
    };

    public static NativeInputSelection Select(NativeInputShape shape, IReadOnlyList<HidCollectionInfo> enumerated,
        IReadOnlyList<HidCollectionInfo> controls, HidPhysicalContainer control, IHidContainerResolver containers,
        TimeProvider time, TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(enumerated);
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(time);
        var controlInterfaces = controls.Select(c => c.Interface).ToHashSet();
        var notes = new List<string>();
        var eligible = 0;
        var matches = new List<HidCollectionInfo>();
        foreach (var candidate in enumerated)
        {
            var why = shape.Reject(candidate);
            if (why is null && controlInterfaces.Contains(candidate.Interface)) why = "shares the interface of the native control owner";
            if (why is not null) { notes.Add(candidate + ": " + why); continue; }
            eligible++;
            var current = RogKeyboardInputContract.Fresh(containers.Resolve(candidate), time, maximumAge, "Native event");
            if (control.SameContainer(current)) matches.Add(candidate);
            else notes.Add(candidate + ": eligible but on another physical container");
        }

        if (matches.Count == 1) return new(matches[0], shape.RuleFor(matches[0]));
        var shown = notes.Take(MaximumDiagnostics).ToList();
        if (notes.Count > shown.Count) shown.Add(string.Create(CultureInfo.InvariantCulture, $"{notes.Count - shown.Count} more"));
        var ambiguous = matches.Count > 1 ? " Ambiguous: " + string.Join("; ", matches.Take(MaximumDiagnostics)) + "." : string.Empty;
        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"Exactly one same-container native vendor21 collection is required: {matches.Count} of {eligible} eligible {shape} collections share the control container ({enumerated.Count} enumerated).{ambiguous} Not selected: {(shown.Count == 0 ? "none" : string.Join("; ", shown))}."));
    }
}
