using Microsoft.Extensions.Logging;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>
/// Read-only discovery: present RF SENDER dongles (<see cref="LianLiUsbIds"/>; <c>MasterDevice.SendRfData</c> writes every RF
/// packet to <c>RFSender</c>) paired with the configured wireless devices. Optional delivery feedback separately opens only
/// the measured receiver for a literal list query; hubs, LCDs and HID are never opened here. Devices whose family is unknown,
/// whose LED count is implausible for the family, or whose RF channel / rx
/// slot is not configured are skipped.
/// </summary>
public static partial class LianLiDiscovery
{
    public static IReadOnlyList<UsbDeviceInfo> Senders(LianLiOptions options, IUsbEnumerator enumerator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(enumerator);
        if (!Guid.TryParse(options.ReceiverInterfaceGuid, out var guid) || guid == Guid.Empty)
        {
            return [];
        }

        return enumerator.Enumerate(guid)
            .Where(d => LianLiUsbIds.RoleOf(d.Vid, d.Pid) == RfDongleRole.Sender)
            .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<RfDevice> Discover(LianLiOptions options, IUsbEnumerator enumerator, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var senders = Senders(options, enumerator);
        var list = new List<RfDevice>();
        foreach (var c in options.Devices)
        {
            var family = FamilyOf(c);
            if (c.Sender < 0 || c.Sender >= senders.Count || family is null || c.Channel is < 1 or > 254 || c.Rx is < 1 or > 13
                || !FamilyDetector.IsDrivable(family, c.Leds) || !MatchesIlLedNum(c) || !TryMac(c.Mac, out var mac))
            {
                LogSkipped(logger, c.Mac, family?.Family ?? (c.Family.Length > 0 ? c.Family : $"RecType {c.RecType}"), c.Leds);
                continue;
            }

            if (list.Any(d => d.Mac == mac))
            {
                continue;
            }

            var slave = (byte)list.Count(d => d.Sender == senders[c.Sender]);
            list.Add(new RfDevice(mac, c.Name ?? $"{family.Family} {mac}", family, c.Leds, senders[c.Sender], new RfAddress((byte)c.Channel, (byte)c.Rx), slave));
        }

        return list;
    }

    /// <summary>The configured family; when <c>Family</c> is empty, the single family whose RecTypes contain <c>RecType</c>.</summary>
    public static PacketAssembler? FamilyOf(LianLiDeviceConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!string.IsNullOrWhiteSpace(c.Family))
        {
            var byName = FamilyDetector.ByFamily(c.Family);
            return byName is not null && (c.RecType == 0 || byName.Spec.RecTypes.Contains((byte)c.RecType)) ? byName : null;
        }

        var byType = c.RecType is > 0 and <= byte.MaxValue ? FamilyDetector.ByRecType((byte)c.RecType) : [];
        return byType.Count == 1 ? byType[0] : null;
    }

    /// <summary>With <c>RecType</c> given, <c>Leds</c> must be what L-Connect computes (<see cref="RfLedCounts.LedNum"/>).</summary>
    public static bool MatchesIlLedNum(LianLiDeviceConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.RecType == 0
            || (c.RecType <= byte.MaxValue && c.DevType is >= 0 and <= byte.MaxValue && c.FanNum is >= 0 and <= 4
                && RfLedCounts.LedNum((byte)c.DevType, (byte)c.RecType, c.FanNum) == c.Leds);
    }

    private static bool TryMac(string s, out RfMac mac)
    {
        try
        {
            mac = RfMac.Parse(s);
            return mac != RfMac.Broadcast;
        }
        catch (FormatException)
        {
            mac = default;
            return false;
        }
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Warning, Message = "Skipping configured Lian Li device {Mac} (family {Family}, {Leds} LEDs): sender, family, LED count or RF address not recognised")]
    private static partial void LogSkipped(ILogger logger, string mac, string family, int leds);
}

public sealed class LianLiAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.LianLi;

    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var options = sp.GetService(typeof(LianLiOptions)) as LianLiOptions ?? new LianLiOptions();
        var logger = (sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<LianLiAdapter>();
        var enumerator = new SetupApiEnumerator();
        IHotplugSource? hotplug = Guid.TryParse(options.ReceiverInterfaceGuid, out var g) && g != Guid.Empty ? new HotplugWatcher(g) : null;
        return new LianLiAdapter(
            options,
            enumerator,
            d => WinUsbTransport.Open(d.Path, OpcodeLayout.LianLi, LianLiWire.OutEndpoint, LianLiWire.InEndpoint),
            hotplug,
            logger,
            encoderFactory: () => RgbAnimationEncoders.Create(options),
            receiverProbe: () =>
            {
                if (!Guid.TryParse(options.ReceiverInterfaceGuid, out var guid) || Senders().Count != 1)
                { throw new InvalidOperationException("RF receiver/sender pairing is ambiguous."); }
                var receivers = enumerator.Enumerate(guid).Where(RfReceiverQuery.Supports).ToArray();
                return receivers.Length == 1 ? RfReceiverQuery.ReadList(receivers[0])
                    : throw new InvalidOperationException("Exactly one verified RF receiver is required.");
            });

        IReadOnlyList<UsbDeviceInfo> Senders() => LianLiDiscovery.Senders(options, enumerator);
    }
}
