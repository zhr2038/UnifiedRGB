using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>The selected event collection and the physical container it shares with the receiver's control interface.</summary>
public sealed record RogKeyboardEventBinding(HidCollectionInfo EventCollection, HidPhysicalContainer Container);

/// <summary>
/// The single paired-keyboard event collection proven on the owner's unit, and the join that selects it. Control (MI02) and
/// events (MI03) are separate interface-scoped device nodes; they are joined ONLY by a positively identical, fresh platform
/// physical container. VID/PID alone and the interface-scoped <see cref="RogReceiver.ParentKey"/> are never a join. FFC1
/// (RID5, mouse), FFC2 (RID3, receiver), MI02 and ordinary keyboard/mouse collections never match the event rule and are
/// never resolved.
/// </summary>
public static class RogKeyboardInputContract
{
    public const ushort ReceiverProductId = 0x1ACE;
    public const ushort KeyboardProductId = 0x1C25;
    public const byte ControlReportId = 2;
    public const byte NotifyReportId = 4;
    public const int ControlInterface = 2;
    public const int EventInterface = 3;
    public const ushort EventUsagePage = 0xFFC0;
    public const ushort EventUsage = 1;

    /// <summary>Descriptor maximum = actual native report length, including the report id byte.</summary>
    public const int EventInputReportLength = RogControlNotificationParser.NativeLength;

    /// <summary>MI03, FFC0/1, input 21, output 0, feature 0, descriptor input report ids exactly {4}.</summary>
    public static HidVendorInputRule EventRule { get; } = new(RogProtocol.VendorId, ReceiverProductId, EventInterface,
        EventUsagePage, EventUsage, EventInputReportLength, [NotifyReportId]);

    public static bool IsVerifiedTarget(RogPairedDevice target) =>
        target is { ProductId: KeyboardProductId, ReportId: ControlReportId, NotifyReportId: NotifyReportId };

    /// <summary>
    /// Selects the event collection for <paramref name="target"/> or throws <see cref="DeviceBlockedException"/>. Requires the
    /// verified 1ACE receiver, the exact 1C25/RID2/notify4 route exactly once in <paramref name="freshPairing"/>, one fresh
    /// container shared by every vendor control channel of the receiver, fresh containers for every exact event candidate,
    /// and exactly one candidate on the control container. Missing, stale, future-dated or conflicting metadata blocks.
    /// </summary>
    public static RogKeyboardEventBinding Bind(RogReceiver receiver, RogPairedDevice target, IReadOnlyList<RogPairedDevice> freshPairing,
        IReadOnlyList<HidCollectionInfo> collections, IHidContainerResolver containers, TimeProvider time, TimeSpan maximumMetadataAge)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(freshPairing);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(time);
        if (receiver.ProductId != ReceiverProductId) throw new DeviceBlockedException("Keyboard events are verified only on the 1ACE RF receiver.");
        if (!IsVerifiedTarget(target) || freshPairing.Count(p => p == target) != 1)
            throw new DeviceBlockedException("The fresh pairing does not contain exactly the verified Azoth X route 1C25/RID2/notify4.");
        if (!receiver.Channels.ContainsKey(ControlReportId)) throw new DeviceBlockedException("The receiver's keyboard control channel is unavailable.");

        HidPhysicalContainer? control = null;
        foreach (var channel in receiver.Channels.OrderBy(c => c.Key).Select(c => c.Value))
        {
            if (channel.Vid != RogProtocol.VendorId || channel.Pid != ReceiverProductId || channel.Interface != ControlInterface)
                throw new DeviceBlockedException("A receiver control channel is not an MI02 interface of the 1ACE receiver.");
            var container = Fresh(containers.Resolve(channel), time, maximumMetadataAge, "Control interface");
            if (control is null) control = container;
            else if (!control.SameContainer(container))
                throw new DeviceBlockedException("The receiver's control interfaces report different physical containers.");
        }

        var candidates = collections.Where(c => EventRule.Reject(c) is null).ToArray();
        if (candidates.Length == 0) throw new DeviceBlockedException("No exact FFC0/RID4 keyboard event collection is enumerated.");
        HidCollectionInfo? match = null;
        var matches = 0;
        foreach (var candidate in candidates)
        {
            // Every candidate must resolve: an unresolved one could be on the control container, so it is not ignored.
            if (Fresh(containers.Resolve(candidate), time, maximumMetadataAge, "Event interface").SameContainer(control!))
            {
                match = candidate;
                matches++;
            }
        }

        return matches switch
        {
            1 => new RogKeyboardEventBinding(match!, control!),
            0 => throw new DeviceBlockedException("No exact keyboard event collection shares the control receiver's physical container."),
            _ => throw new DeviceBlockedException("More than one keyboard event collection shares the control receiver's physical container."),
        };
    }

    /// <summary>The resolved container, or <see cref="DeviceBlockedException"/> when unavailable, future-dated or older than <paramref name="maximumAge"/>.</summary>
    public static HidPhysicalContainer Fresh(HidContainerResolution resolution, TimeProvider time, TimeSpan maximumAge, string what)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);
        if (resolution.Container is not { } container) throw new DeviceBlockedException($"{what} physical container is unavailable: {resolution.Failure}");
        var now = time.GetTimestamp();
        if (container.ObservedTimestamp > now) throw new DeviceBlockedException($"{what} physical container metadata is dated in the future.");
        if (time.GetElapsedTime(container.ObservedTimestamp, now) > maximumAge) throw new DeviceBlockedException($"{what} physical container metadata is stale.");
        return container;
    }
}
