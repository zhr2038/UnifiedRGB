using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup;

/// <summary>
/// Pure mapping of ONE receiver report (the reply of the literal GetDev(16,1) query) plus this computer's dongle inventory to
/// proposed <see cref="LianLiDeviceConfig"/> values. It reuses <see cref="RfReceiverReport.ParseComplete"/>,
/// <see cref="RfLedCounts.LedNum"/>, <see cref="LianLiDiscovery.FamilyOf"/>, <see cref="LianLiDiscovery.MatchesIlLedNum"/> and
/// <see cref="FamilyDetector.IsDrivable"/>, so a proposal is exactly what the adapter would accept. Any ambiguity refuses rather
/// than guesses. Mounting/calibration are never proposed: every proposal uses the identity mounting.
/// </summary>
public static class LianLiPairingPlanner
{
    /// <summary>Families validated on real hardware by this project. Other recognised families are reported, never proposed.</summary>
    public static IReadOnlyList<string> ValidatedFamilies { get; } = ["TL", "H2"];

    public static LianLiPairingResult Analyse(
        IReadOnlyList<UsbDeviceInfo> receivers,
        IReadOnlyList<UsbDeviceInfo> senders,
        byte[]? reply,
        string? queryError = null,
        RfMac? knownMaster = null)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        ArgumentNullException.ThrowIfNull(senders);
        var none = Array.Empty<LianLiProposal>();
        var noRejections = Array.Empty<LianLiRejection>();
        LianLiPairingResult Fail(LianLiPairingState state, string detail, IReadOnlyList<LianLiRejection>? rejections = null) =>
            new(state, null, null, none, rejections ?? noRejections, detail);

        if (receivers.Count == 0)
        {
            return Fail(LianLiPairingState.NoReceiver, "No Lian Li RF receiver dongle is present.");
        }

        if (receivers.Count > 1)
        {
            return Fail(LianLiPairingState.MultipleReceivers, $"{receivers.Count} RF receivers are present; exactly one is required to avoid mixing pairings.");
        }

        if (!RfReceiverQuery.Supports(receivers[0]))
        {
            return Fail(LianLiPairingState.UnverifiedReceiver,
                $"RF receiver {receivers[0].Vid:X4}:{receivers[0].Pid:X4} is not the measured 0416:8041 receiver; it is never queried.");
        }

        if (senders.Count == 0)
        {
            return Fail(LianLiPairingState.NoSender, "No Lian Li RF sender dongle is present.");
        }

        if (senders.Count > 1)
        {
            return Fail(LianLiPairingState.MultipleSenders, $"{senders.Count} RF senders are present; exactly one is required to assign devices.");
        }

        if (reply is null)
        {
            return Fail(LianLiPairingState.QueryFailed, $"The receiver list query failed: {queryError ?? "no reply"}.");
        }

        IReadOnlyList<RfReceiverReport> reports;
        try
        {
            reports = RfReceiverReport.ParseComplete(reply);
        }
        catch (InvalidDataException ex)
        {
            return Fail(LianLiPairingState.ReportInvalid, $"The receiver report was rejected: {ex.Message}");
        }

        var rejections = new List<LianLiRejection>();
        var paired = new List<RfDeviceRecord>();
        foreach (var r in reports.Select(r => r.Device))
        {
            if (r.Mac.Value == 0 || r.Mac == RfMac.Broadcast)
            {
                rejections.Add(new(Mac(r.Mac), "InvalidMac", "The record carries a zero/broadcast device MAC."));
            }
            else if (r.MasterMac.Value == 0 || r.MasterMac == RfMac.Broadcast)
            {
                rejections.Add(new(Mac(r.Mac), "Unpaired", "The device is not bound to any sender (zero/broadcast master)."));
            }
            else
            {
                paired.Add(r);
            }
        }

        RfMac master;
        if (knownMaster is { } known)
        {
            master = known;
        }
        else
        {
            var masters = paired.Select(r => r.MasterMac).Distinct().ToArray();
            if (masters.Length > 1)
            {
                rejections.AddRange(paired.Select(r => new LianLiRejection(Mac(r.Mac), "AmbiguousMaster", $"Bound to master {Mac(r.MasterMac)}.")));
                return Fail(LianLiPairingState.AmbiguousMaster,
                    $"The receiver hears devices bound to {masters.Length} different senders (e.g. a neighbouring PC); which one is this computer's sender cannot be decided without its USB 17 query.",
                    rejections);
            }

            if (masters.Length == 0)
            {
                return new(LianLiPairingState.NoPairedDevices, null, null, none, rejections, "No device bound to a sender was reported.");
            }

            master = masters[0];
        }

        var candidates = new List<(RfDeviceRecord Record, LianLiProposal Proposal)>();
        foreach (var r in paired)
        {
            if (r.MasterMac != master)
            {
                rejections.Add(new(Mac(r.Mac), "OtherMaster", $"Bound to another sender {Mac(r.MasterMac)}."));
                continue;
            }

            if (Propose(r, out var proposal, out var rejection))
            {
                candidates.Add((r, proposal));
            }
            else
            {
                rejections.Add(rejection);
            }
        }

        foreach (var clash in candidates.GroupBy(c => c.Record.Address).Where(g => g.Count() > 1).ToArray())
        {
            foreach (var c in clash)
            {
                candidates.Remove(c);
                rejections.Add(new(c.Proposal.Mac, "DuplicateAddress", $"RF address channel {clash.Key.Channel} / rx {clash.Key.Rx} is reported by more than one device."));
            }
        }

        var channels = candidates.Select(c => c.Proposal.Channel).Distinct().ToArray();
        if (channels.Length > 1)
        {
            rejections.AddRange(candidates.Select(c => new LianLiRejection(c.Proposal.Mac, "AmbiguousChannel", $"Channel {c.Proposal.Channel}.")));
            return Fail(LianLiPairingState.AmbiguousChannel, "Devices of one sender report different RF channels; refusing to guess the master channel.", rejections);
        }

        if (candidates.Count == 0)
        {
            return new(LianLiPairingState.NoPairedDevices, Mac(master), null, none, rejections,
                "No supported device of this sender was reported (see rejections).");
        }

        var proposals = candidates.Select(c => c.Proposal).OrderBy(p => p.Rx).ToArray();
        return new(LianLiPairingState.Ready, Mac(master), channels[0], proposals, rejections,
            $"{proposals.Length} supported device(s) bound to master {Mac(master)} (master read from receiver records; not confirmed by a sender query).");
    }

    /// <summary>The proposal as the adapter's config type, identity mounting.</summary>
    public static LianLiDeviceConfig ToConfig(LianLiProposal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new()
        {
            Mac = p.Mac,
            Family = string.Empty,
            RecType = p.RecType,
            DevType = p.DevType,
            FanNum = p.FanNum,
            Leds = p.Leds,
            Name = p.Name,
            Sender = p.Sender,
            Channel = p.Channel,
            Rx = p.Rx,
        };
    }

    /// <summary>Lower-case colon form used by the Host configuration (e.g. <c>0a:1b:2c:3d:4e:5f</c>).</summary>
    public static string Mac(RfMac mac) => mac.ToString().Replace('-', ':').ToLowerInvariant();

    private static bool Propose(RfDeviceRecord r, out LianLiProposal proposal, out LianLiRejection rejection)
    {
        proposal = null!;
        var mac = Mac(r.Mac);
        LianLiRejection Reject(string code, string message) => new(mac, code, message);

        if (r.Address.Channel is < 1 or > 254 || r.Address.Rx is < 1 or > 13)
        {
            rejection = Reject("InvalidAddress", $"RF address channel {r.Address.Channel} / rx {r.Address.Rx} is outside 1..254 / 1..13.");
            return false;
        }

        byte recType;
        int fans;
        if (r.DevType == 0)
        {
            fans = r.FanCount;
            if (fans is < 1 or > 4)
            {
                rejection = Reject("FanCount", $"Fan group reports {fans} fans; 1..4 are supported.");
                return false;
            }

            var types = r.FanTypes.Take(fans).Select(RfDeviceRecord.RecTypeOfFan).Distinct().ToArray();
            if (types.Length != 1 || types[0] == 0)
            {
                rejection = Reject("MixedFanTypes", $"Fan types {Convert.ToHexString(r.FanTypes.Take(fans).ToArray())} do not form one recognised family.");
                return false;
            }

            recType = types[0];
        }
        else if (r.DevType is 10 or 11)
        {
            fans = r.FanCount;
            recType = r.RecType;
        }
        else
        {
            rejection = Reject("UnsupportedDeviceType", $"Device type {r.DevType} is not supported by this program.");
            return false;
        }

        var families = FamilyDetector.ByRecType(recType);
        if (families.Count != 1)
        {
            rejection = Reject("UnknownFamily", $"RecType {recType} is not a recognised wireless family.");
            return false;
        }

        var family = families[0];
        if (!ValidatedFamilies.Contains(family.Family, StringComparer.OrdinalIgnoreCase))
        {
            rejection = Reject("UnvalidatedFamily", $"{family.Family} (RecType {recType}) is recognised but has never been validated on hardware; it is not configured.");
            return false;
        }

        var leds = RfLedCounts.LedNum(r.DevType, recType, fans);
        var name = fans > 0 ? $"{family.Family} x{fans} ({mac[..8]})" : $"{family.Family} ({mac[..8]})";
        proposal = new(mac, string.Empty, recType, r.DevType, fans, leds, 0, r.Address.Channel, r.Address.Rx, name);
        var config = ToConfig(proposal);
        var resolved = LianLiDiscovery.FamilyOf(config);
        if (resolved is null || !LianLiDiscovery.MatchesIlLedNum(config) || !FamilyDetector.IsDrivable(resolved, leds)
            || (resolved.Spec.WholeUnits && fans > 0 && resolved.UnitsIn(leds) != fans && resolved.Spec.MaxUnits > 1))
        {
            rejection = Reject("NotDrivable", $"{family.Family} with {fans} fan(s) = {leds} LEDs is outside what the adapter drives.");
            proposal = null!;
            return false;
        }

        rejection = null!;
        return true;
    }
}
