using System.Globalization;
using System.Text;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.AsusAura;

/// <summary>
/// Report builders and reply decoders of the ASUS Aura USB mainboard protocol (NATIVE design §4.1, E0). Every report is
/// 65 bytes with report id 0xEC and zero padding. Only the two queries, the direct mode select (0x35 … 0xFF) and direct
/// colour reports (0x40) exist here: no SetGen1 (0x52/0x53), no commit (0x3F), no other mode.
/// </summary>
public static class AuraReports
{
    /// <summary>Largest channel the 0x40 offset encoding addresses (offset ≤ 255 + 256 with flag 0x10).</summary>
    public const int MaxDirectLeds = 512;

    /// <summary>A query (0x82 firmware or 0xB0 config table); every other byte zero.</summary>
    public static byte[] Query(byte command)
    {
        if (command is not (AuraProtocol.RequestFirmware or AuraProtocol.RequestConfig))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "only the 0x82 and 0xB0 queries exist");
        }

        var r = new byte[AuraProtocol.ReportLength];
        r[0] = AuraProtocol.ReportId;
        r[1] = command;
        return r;
    }

    /// <summary><c>[EC, 35, effectChannel, 00, 00, FF]</c>: direct mode on one effect channel, no shutdown effect.</summary>
    public static byte[] DirectMode(byte effectChannel)
    {
        var r = new byte[AuraProtocol.ReportLength];
        r[0] = AuraProtocol.ReportId;
        r[1] = AuraProtocol.Effect;
        r[2] = effectChannel;
        r[5] = AuraProtocol.ModeDirect;
        return r;
    }

    /// <summary>Reports of one direct-channel frame: ⌈N / 20⌉.</summary>
    public static int ReportsFor(int leds) => (leds + AuraProtocol.LedsPerReport - 1) / AuraProtocol.LedsPerReport;

    /// <summary>
    /// One complete direct-channel frame: <c>[EC, 40, directChannel | 0x80 on the last report | 0x10 above offset 255, offset,
    /// count, R,G,B × count]</c>, ≤ 20 LEDs per report, colours in strip order from LED 0.
    /// </summary>
    public static IReadOnlyList<byte[]> Direct(byte directChannel, ReadOnlySpan<Rgb> colors)
    {
        if ((directChannel & ~AuraProtocol.DirectChannelMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directChannel), directChannel, "direct channel outside the 0x6F mask");
        }

        if (colors.Length < 1 || colors.Length > MaxDirectLeds)
        {
            throw new ArgumentOutOfRangeException(nameof(colors), colors.Length, $"a direct frame has 1..{MaxDirectLeds} LEDs");
        }

        var reports = new List<byte[]>(ReportsFor(colors.Length));
        for (var offset = 0; offset < colors.Length; offset += AuraProtocol.LedsPerReport)
        {
            var count = Math.Min(AuraProtocol.LedsPerReport, colors.Length - offset);
            var command = directChannel;
            if (offset + count == colors.Length)
            {
                command |= AuraProtocol.DirectApply;
            }

            if (offset > byte.MaxValue)
            {
                command |= AuraProtocol.DirectOffsetHigh;
            }

            var r = new byte[AuraProtocol.ReportLength];
            r[0] = AuraProtocol.ReportId;
            r[1] = AuraProtocol.Direct;
            r[2] = command;
            r[3] = (byte)(offset > byte.MaxValue ? offset - 256 : offset);
            r[4] = (byte)count;
            for (var k = 0; k < count; k++)
            {
                var c = colors[offset + k];
                r[5 + (3 * k)] = c.R;
                r[6 + (3 * k)] = c.G;
                r[7 + (3 * k)] = c.B;
            }

            reports.Add(r);
        }

        return reports;
    }

    /// <summary>The 0x82 reply: exactly 65 bytes returned, <c>EC 02</c>, firmware string in bytes 2..17 (ASCII up to the first NUL).</summary>
    public static bool TryDecodeFirmware(int returned, ReadOnlySpan<byte> reply, out string firmware, out string why)
    {
        firmware = string.Empty;
        if (!Check(returned, reply, AuraProtocol.FirmwareReply, out why))
        {
            return false;
        }

        var text = reply.Slice(2, 16);
        var end = text.IndexOf((byte)0);
        var sb = new StringBuilder();
        foreach (var b in end >= 0 ? text[..end] : text)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }

        firmware = sb.ToString();
        return true;
    }

    /// <summary>The 0xB0 reply: exactly 65 bytes returned, <c>EC 30</c>, the 60-byte table from offset 4.</summary>
    public static bool TryDecodeConfig(int returned, ReadOnlySpan<byte> reply, out byte[] table, out string why)
    {
        table = [];
        if (!Check(returned, reply, AuraProtocol.ConfigReply, out why))
        {
            return false;
        }

        table = reply.Slice(AuraProtocol.ConfigOffset, AuraProtocol.ConfigLength).ToArray();
        return true;
    }

    private static bool Check(int returned, ReadOnlySpan<byte> reply, byte expectedId, out string why)
    {
        if (returned != AuraProtocol.ReportLength)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"returned size {returned}, expected {AuraProtocol.ReportLength}");
            return false;
        }

        if (reply.Length < AuraProtocol.ReportLength)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"reply buffer of {reply.Length} bytes");
            return false;
        }

        if (reply[0] != AuraProtocol.ReportId)
        {
            why = $"report id 0x{reply[0]:X2} is not 0xEC";
            return false;
        }

        if (reply[1] != expectedId)
        {
            why = $"reply id 0x{reply[1]:X2} is not 0x{expectedId:X2}";
            return false;
        }

        why = string.Empty;
        return true;
    }
}

/// <summary>The firmware string and 60-byte config table of one board (0x82 + 0xB0), with the derived counts (design §4.1).</summary>
public sealed record AuraConfig
{
    public AuraConfig(string firmware, byte[] table)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Length != AuraProtocol.ConfigLength)
        {
            throw new ArgumentException($"config table has {table.Length} bytes, not {AuraProtocol.ConfigLength}", nameof(table));
        }

        Firmware = firmware;
        Table = table;
    }

    public string Firmware { get; }

    public IReadOnlyList<byte> Table { get; }

    /// <summary>[0x1B]: LEDs of the onboard direct channel 0x04 (the 12 V RGB headers' LEDs are among them).</summary>
    public int OnboardLeds => Table[AuraProtocol.ConfigOnboardLeds];

    /// <summary>[0x1D] as read.</summary>
    public int RgbHeadersRaw => Table[AuraProtocol.ConfigRgbHeaders];

    /// <summary>[0x1D] clamped like the reference: more RGB headers than onboard LEDs is inconsistent and counts as 0.</summary>
    public int RgbHeaders => OnboardLeds < RgbHeadersRaw ? 0 : RgbHeadersRaw;

    /// <summary>[0x02]: addressable headers; their strip lengths are not in the table.</summary>
    public int AddressableHeaders => Table[AuraProtocol.ConfigAddressableHeaders];

    /// <summary>Effect channels (0x35 namespace): onboard first when it has LEDs, then one per addressable header.</summary>
    public int EffectChannels => (OnboardLeds > 0 ? 1 : 0) + AddressableHeaders;

    /// <summary>Effect channel of addressable header <paramref name="header"/> (0-based).</summary>
    public int EffectChannelOfHeader(int header) => (OnboardLeds > 0 ? 1 : 0) + header;

    public string TableHex => Convert.ToHexString(Table.ToArray());
}

/// <summary>
/// One drivable channel: <paramref name="Marker"/> is the bring-up/map channel number (0 = onboard, i + 1 = addressable
/// header i), <paramref name="EffectChannel"/> the 0x35 index, <paramref name="DirectChannel"/> the 0x40 channel. The two
/// namespaces are never mixed: onboard is effect 0 / direct 0x04, header i is effect (onboard ? 1 : 0) + i / direct i.
/// </summary>
public sealed record AuraChannel(int Marker, string Name, byte EffectChannel, byte DirectChannel, int Leds);

/// <summary>Channels derived from a config table and the owner-entered header LED counts, with notes on what was not exposed.</summary>
public sealed record AuraTopology(IReadOnlyList<AuraChannel> Channels, int EffectChannels, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Onboard when <c>config[0x1B] &gt; 0</c>; addressable header i only when the owner entered <c>LedCount &gt; 0</c>, the table
    /// reports that header, and its direct channel is a valid 0x40 channel distinct from onboard 0x04.
    /// </summary>
    public static AuraTopology Derive(AuraConfig config, IReadOnlyList<int> headerLeds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(headerLeds);
        var channels = new List<AuraChannel>();
        var notes = new List<string>();
        if (config.OnboardLeds > 0)
        {
            channels.Add(new AuraChannel(0, "Onboard", 0, AuraProtocol.OnboardDirectChannel, config.OnboardLeds));
        }
        else
        {
            notes.Add("config[0x1B] = 0: no onboard channel");
        }

        if (config.RgbHeadersRaw != config.RgbHeaders)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"config[0x1D] = {config.RgbHeadersRaw} exceeds config[0x1B] = {config.OnboardLeds}: RGB headers counted as 0"));
        }

        for (var i = 0; i < headerLeds.Count; i++)
        {
            var leds = headerLeds[i];
            if (leds <= 0)
            {
                continue;
            }

            if (i >= config.AddressableHeaders)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"header {i + 1}: {leds} LEDs configured but the table reports {config.AddressableHeaders} addressable header(s): not exposed"));
                continue;
            }

            if ((i & ~AuraProtocol.DirectChannelMask) != 0 || i == AuraProtocol.OnboardDirectChannel)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"header {i + 1}: direct channel 0x{i:X2} is not a distinct 0x40 channel: not exposed"));
                continue;
            }

            if (leds > AuraReports.MaxDirectLeds)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"header {i + 1}: {leds} LEDs exceed {AuraReports.MaxDirectLeds}: not exposed"));
                continue;
            }

            channels.Add(new AuraChannel(i + 1, string.Create(CultureInfo.InvariantCulture, $"ARGB header {i + 1}"), (byte)config.EffectChannelOfHeader(i), (byte)i, leds));
        }

        return new AuraTopology(channels, config.EffectChannels, notes);
    }

    public AuraChannel? ByMarker(int marker) => Channels.FirstOrDefault(c => c.Marker == marker);

    /// <summary>What the firewall admits: effect channels below the table's count, the derived direct channels with their LEDs.</summary>
    public AuraChannelTable FirewallTable() => new(EffectChannels, Channels.ToDictionary(c => c.DirectChannel, c => c.Leds));

    public string DirectChannelsText => string.Join(",", Channels.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.DirectChannel:X2}:{c.Leds}")));
}
