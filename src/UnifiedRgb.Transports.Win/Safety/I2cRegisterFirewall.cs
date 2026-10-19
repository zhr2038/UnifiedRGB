using Microsoft.Extensions.Logging;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Safety;

public sealed class ForbiddenRegisterException : InvalidOperationException
{
    public ForbiddenRegisterException(byte device, byte register)
        : base($"I2C write to device 0x{device:X2} register 0x{register:X2} is not on the allow-list")
    {
        Device = device;
        Register = register;
    }

    public ForbiddenRegisterException(byte device, byte register, string reason)
        : base($"I2C access to device 0x{device:X2} register 0x{register:X2} refused: {reason}")
    {
        Device = device;
        Register = register;
    }

    public ForbiddenRegisterException()
    {
    }

    public ForbiddenRegisterException(string message)
        : base(message)
    {
    }

    public ForbiddenRegisterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public byte Device { get; }

    public byte Register { get; }
}

/// <summary>Validates the payload of one admitted register (bytes after <c>[device, register]</c>); null reason = accept.</summary>
public delegate string? I2cPayloadRule(ReadOnlySpan<byte> payload);

/// <summary>
/// Default-deny register policy of one I2C device (NATIVE design §3.3): per register, a direction and a payload rule.
/// Anything not listed is denied. <see cref="Legacy"/> reproduces the original allow-list firewall exactly.
/// </summary>
public sealed class I2cFirewallPolicy
{
    private readonly Dictionary<byte, I2cPayloadRule?> _writes;
    private readonly Dictionary<byte, int> _reads;

    public I2cFirewallPolicy(string name, byte device, IReadOnlyDictionary<byte, I2cPayloadRule?> writes, IReadOnlyDictionary<byte, int>? reads = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(writes);
        Name = name;
        Device = device;
        _writes = new Dictionary<byte, I2cPayloadRule?>(writes);
        _reads = reads is null ? [] : new Dictionary<byte, int>(reads);
    }

    public string Name { get; }

    public byte Device { get; }

    /// <summary>The original firewall: one device and a register allow-list, any payload (dormant legacy MSI path).</summary>
    public static I2cFirewallPolicy Legacy(byte device, IEnumerable<byte> allowedRegisters)
    {
        ArgumentNullException.ThrowIfNull(allowedRegisters);
        return new I2cFirewallPolicy("Legacy", device, allowedRegisters.Distinct().ToDictionary(r => r, _ => (I2cPayloadRule?)null));
    }

    /// <summary>
    /// "MsiNv5000" (device 0x68 only): 0x06 ← {0x00, 0x01}; 0x04 ← [0x01, (i, R, G, B, 0x01) × N] with
    /// 1 ≤ N ≤ <paramref name="maxIndices"/>, i &lt; maxIndices, strictly increasing; read 0xEC of exactly 4 bytes.
    /// Every legacy register is denied. <paramref name="maxIndices"/> is bounded by the evidenced caller range
    /// (<see cref="Nv5000.EvidencedIndices"/> = 34, indices 0..33); no option can widen it.
    /// </summary>
    public static I2cFirewallPolicy MsiNv5000(int maxIndices)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIndices, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxIndices, Nv5000.EvidencedIndices);
        var writes = new Dictionary<byte, I2cPayloadRule?>
        {
            [Nv5000.SessionRegister] = SessionPayload,
            [Nv5000.FrameRegister] = p => FramePayload(p, maxIndices),
        };
        return new I2cFirewallPolicy("MsiNv5000", Nv5000.Device, writes, new Dictionary<byte, int> { [Nv5000.FamilyRegister] = Nv5000.FamilyLength });
    }

    /// <summary>Null when the write frame <c>[device, register, payload…]</c> is admitted, else the reason.</summary>
    public string? CheckWrite(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return "frame too short to classify";
        }

        if (frame[0] != Device)
        {
            return $"device 0x{frame[0]:X2} is not 0x{Device:X2}";
        }

        if (!_writes.TryGetValue(frame[1], out var rule))
        {
            return $"register 0x{frame[1]:X2} not writable under policy {Name}";
        }

        return rule?.Invoke(frame[2..]);
    }

    /// <summary>Null when a read of <paramref name="length"/> bytes at <paramref name="register"/> is admitted, else the reason.</summary>
    public string? CheckRead(byte device, byte register, int length)
    {
        if (device != Device)
        {
            return $"device 0x{device:X2} is not 0x{Device:X2}";
        }

        if (!_reads.TryGetValue(register, out var len))
        {
            return $"register 0x{register:X2} not readable under policy {Name}";
        }

        return length == len ? null : $"read length {length} is not {len}";
    }

    private static string? SessionPayload(ReadOnlySpan<byte> p) =>
        p.Length == 1 && p[0] <= 0x01 ? null : "0x06 takes exactly one byte 0x00 or 0x01";

    private static string? FramePayload(ReadOnlySpan<byte> p, int maxIndices)
    {
        if (p.Length < 1 + Nv5000.RecordLength || (p.Length - 1) % Nv5000.RecordLength != 0)
        {
            return $"0x04 payload length {p.Length} is not 1 + 5N with N ≥ 1";
        }

        var n = (p.Length - 1) / Nv5000.RecordLength;
        if (n > maxIndices)
        {
            return $"0x04 carries {n} records, more than {maxIndices}";
        }

        if (p[0] != Nv5000.FrameHeader)
        {
            return $"0x04 header 0x{p[0]:X2} is not 0x01";
        }

        var last = -1;
        for (var r = 0; r < n; r++)
        {
            var rec = p.Slice(1 + (r * Nv5000.RecordLength), Nv5000.RecordLength);
            if (rec[4] != Nv5000.RecordTrailer)
            {
                return $"record {r} trailer 0x{rec[4]:X2} is not 0x01";
            }

            if (rec[0] >= maxIndices)
            {
                return $"record {r} index {rec[0]} is not below {maxIndices}";
            }

            if (rec[0] <= last)
            {
                return $"record {r} index {rec[0]} is not strictly increasing";
            }

            last = rec[0];
        }

        return null;
    }
}

/// <summary>Constants of the recovered MSI NV5000 indexed protocol (NATIVE design §3.1, E0).</summary>
public static class Nv5000
{
    public const byte Device = 0x68;
    public const byte SessionRegister = 0x06;
    public const byte SessionEnter = 0x00;
    public const byte SessionRelease = 0x01;
    public const byte FrameRegister = 0x04;
    public const byte FrameHeader = 0x01;
    public const byte RecordTrailer = 0x01;
    public const int RecordLength = 5;
    public const byte FamilyRegister = 0xEC;
    public const int FamilyLength = 4;

    /// <summary>Index slots the recovered caller uses (0..33). Not a LED count; the hard upper bound of every NV5000 frame.</summary>
    public const int EvidencedIndices = 34;

    /// <summary>Wire length of an N-record frame <c>[0x68, 0x04, 0x01, (i,R,G,B,1) × N]</c>: 3 + 5N.</summary>
    public static int WireLength(int records) => 3 + (RecordLength * records);
}

/// <summary>
/// Default-deny I2C firewall (first decorator on NVAPI transports): exactly one device address and a register policy.
/// Anything else throws before reaching the bus; each distinct denial is logged Critical once and every denial is counted.
/// </summary>
public sealed partial class I2cRegisterFirewall : ITransport
{
    private readonly ITransport _inner;
    private readonly ILogger? _logger;
    private readonly HashSet<string> _logged = [];
    private long _denied;

    public I2cRegisterFirewall(ITransport inner, byte device, IEnumerable<byte> allowedRegisters, ILogger? logger = null)
        : this(inner, I2cFirewallPolicy.Legacy(device, allowedRegisters), logger)
    {
    }

    public I2cRegisterFirewall(ITransport inner, I2cFirewallPolicy policy, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger;
    }

    public I2cFirewallPolicy Policy { get; }

    /// <summary>Writes and reads refused since construction.</summary>
    public long Denied => Interlocked.Read(ref _denied);

    public static void Check(ReadOnlySpan<byte> frame, byte device, IReadOnlySet<byte> allowed)
    {
        if (frame.Length < 2)
        {
            throw new ForbiddenRegisterException("I2C frame too short to classify; refused.");
        }

        if (frame[0] != device || !allowed.Contains(frame[1]))
        {
            throw new ForbiddenRegisterException(frame[0], frame[1]);
        }
    }

    /// <summary>Throws <see cref="ForbiddenRegisterException"/> unless the policy admits this read (reads bypass SendAsync).</summary>
    public void EnsureRead(byte device, byte register, int length)
    {
        var reason = Policy.CheckRead(device, register, length);
        if (reason is not null)
        {
            Deny(device, register, "R " + reason);
            throw new ForbiddenRegisterException(device, register, reason);
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var span = frame.Span;
        if (span.Length < 2)
        {
            Deny(0, 0, "short");
            throw new ForbiddenRegisterException("I2C frame too short to classify; refused.");
        }

        var reason = Policy.CheckWrite(span);
        if (reason is not null)
        {
            Deny(span[0], span[1], "W " + reason);
            throw Policy.Name == "Legacy"
                ? new ForbiddenRegisterException(span[0], span[1])
                : new ForbiddenRegisterException(span[0], span[1], reason);
        }

        return _inner.SendAsync(frame, ct);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);

    private void Deny(byte device, byte register, string key)
    {
        Interlocked.Increment(ref _denied);
        if (_logger is null)
        {
            return;
        }

        bool first;
        lock (_logged)
        {
            first = _logged.Add($"{device:X2}:{register:X2}:{key}");
        }

        if (first)
        {
            LogBlocked(_logger, device, register);
        }
    }

    [LoggerMessage(EventId = 9002, Level = LogLevel.Critical, Message = "Blocked I2C write to device 0x{Device:X2} register 0x{Register:X2}")]
    private static partial void LogBlocked(ILogger logger, byte device, byte register);
}
