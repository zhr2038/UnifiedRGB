using Microsoft.Extensions.Logging;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Safety;

/// <summary>Constants of the ASUS Aura USB mainboard protocol (NATIVE design §4.1, E0).</summary>
public static class AuraProtocol
{
    public const int ReportLength = 65;
    public const byte ReportId = 0xEC;
    public const byte RequestFirmware = 0x82;
    public const byte FirmwareReply = 0x02;
    public const byte RequestConfig = 0xB0;
    public const byte ConfigReply = 0x30;
    public const int ConfigOffset = 4;
    public const int ConfigLength = 60;
    public const byte Effect = 0x35;
    public const byte ModeDirect = 0xFF;
    public const byte Direct = 0x40;
    public const byte DirectApply = 0x80;
    public const byte DirectOffsetHigh = 0x10;
    public const byte DirectChannelMask = 0x6F;
    public const byte OnboardDirectChannel = 0x04;
    public const int LedsPerReport = 20;

    /// <summary>Config table offsets: onboard LED total, RGB header count, addressable header count.</summary>
    public const int ConfigOnboardLeds = 0x1B;
    public const int ConfigRgbHeaders = 0x1D;
    public const int ConfigAddressableHeaders = 0x02;
}

/// <summary>
/// Channels the firewall admits once the config table is known: the number of effect channels (0x35 byte2 namespace)
/// and, per direct channel (0x40 byte2 &amp; 0x6F namespace), its LED count.
/// </summary>
public sealed record AuraChannelTable(int EffectChannels, IReadOnlyDictionary<byte, int> DirectLeds);

public sealed class ForbiddenReportException : InvalidOperationException
{
    public ForbiddenReportException()
    {
    }

    public ForbiddenReportException(string message)
        : base(message)
    {
    }

    public ForbiddenReportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Default-deny firewall "AuraMainboard" (NATIVE design §4.3), first decorator on the Aura HID transport. Queries 0x82/0xB0
/// with every other byte zero; 0x35 on a discovered effect channel selecting direct (0xFF) only, shutdown byte 0; 0x40 on a
/// discovered direct channel, 1..20 LEDs, offset + count within that channel, padding zero. SetGen1 (0x52/0x53), commit
/// (0x3F) and everything else are refused. Before <see cref="Configure"/> only the two queries pass.
/// </summary>
public sealed partial class AuraReportFirewall : ITransport
{
    private readonly ITransport _inner;
    private readonly ILogger? _logger;
    private readonly HashSet<string> _logged = [];
    private volatile AuraChannelTable? _table;
    private long _denied;

    public AuraReportFirewall(ITransport inner, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
    }

    public long Denied => Interlocked.Read(ref _denied);

    public AuraChannelTable? Table => _table;

    /// <summary>Admits the effect/direct channels derived from the config table (adapter Open).</summary>
    public void Configure(AuraChannelTable table) => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>Null when <paramref name="report"/> is admitted under <paramref name="table"/> (null = queries only), else the reason.</summary>
    public static string? Check(ReadOnlySpan<byte> report, AuraChannelTable? table)
    {
        if (report.Length != AuraProtocol.ReportLength)
        {
            return $"report length {report.Length} is not {AuraProtocol.ReportLength}";
        }

        if (report[0] != AuraProtocol.ReportId)
        {
            return $"report id 0x{report[0]:X2} is not 0xEC";
        }

        switch (report[1])
        {
            case AuraProtocol.RequestFirmware:
            case AuraProtocol.RequestConfig:
                return AllZero(report[2..]) ? null : $"query 0x{report[1]:X2} carries non-zero bytes";

            case AuraProtocol.Effect:
                if (table is null)
                {
                    return "0x35 before the config table was read";
                }

                if (report[2] >= table.EffectChannels)
                {
                    return $"0x35 effect channel {report[2]} is not below {table.EffectChannels}";
                }

                if (report[3] != 0 || report[4] != 0)
                {
                    return "0x35 bytes 3/4 must be zero (no shutdown effect)";
                }

                if (report[5] != AuraProtocol.ModeDirect)
                {
                    return $"0x35 mode 0x{report[5]:X2} is not direct (0xFF)";
                }

                return AllZero(report[6..]) ? null : "0x35 carries non-zero padding";

            case AuraProtocol.Direct:
            {
                if (table is null)
                {
                    return "0x40 before the config table was read";
                }

                var channel = (byte)(report[2] & AuraProtocol.DirectChannelMask);
                if ((report[2] & ~(AuraProtocol.DirectChannelMask | AuraProtocol.DirectApply | AuraProtocol.DirectOffsetHigh)) != 0)
                {
                    return $"0x40 command byte 0x{report[2]:X2} has unknown flags";
                }

                if (!table.DirectLeds.TryGetValue(channel, out var leds))
                {
                    return $"0x40 direct channel 0x{channel:X2} was not discovered";
                }

                int count = report[4];
                if (count < 1 || count > AuraProtocol.LedsPerReport)
                {
                    return $"0x40 count {count} is not 1..{AuraProtocol.LedsPerReport}";
                }

                var offset = report[3] + ((report[2] & AuraProtocol.DirectOffsetHigh) != 0 ? 256 : 0);
                if (offset + count > leds)
                {
                    return $"0x40 offset {offset} + count {count} exceeds channel 0x{channel:X2} ({leds} LEDs)";
                }

                return AllZero(report[(5 + (3 * count))..]) ? null : "0x40 carries bytes beyond its colours";
            }

            default:
                return $"command 0x{report[1]:X2} denied";
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var reason = Check(frame.Span, _table);
        if (reason is not null)
        {
            Interlocked.Increment(ref _denied);
            if (_logger is not null)
            {
                var cmd = frame.Length > 1 ? frame.Span[1] : (byte)0;
                bool first;
                lock (_logged)
                {
                    first = _logged.Add($"{cmd:X2}:{reason}");
                }

                if (first)
                {
                    LogBlocked(_logger, cmd, reason);
                }
            }

            throw new ForbiddenReportException($"Aura report refused: {reason}");
        }

        return _inner.SendAsync(frame, ct);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);

    private static bool AllZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;

    [LoggerMessage(EventId = 9005, Level = LogLevel.Critical, Message = "Blocked Aura report 0x{Command:X2}: {Reason}")]
    private static partial void LogBlocked(ILogger logger, byte command, string reason);
}
