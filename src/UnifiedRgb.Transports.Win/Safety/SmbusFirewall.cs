using Microsoft.Extensions.Logging;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Transports.Safety;

public sealed class ForbiddenSmbusException : InvalidOperationException
{
    public ForbiddenSmbusException()
    {
    }

    public ForbiddenSmbusException(string message)
        : base(message)
    {
    }

    public ForbiddenSmbusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Default-deny firewall "EneDram" over compound SMBus operations (NATIVE design §5.3), one per port. Stateless per
/// decision: each compound carries its own pointer, so the pair is judged at once.
/// <list type="bullet">
/// <item>Probe / ReadSig(0x00, 0xA0..0xAF): only while <see cref="DiscoveryOpen"/>, only at a candidate address.</item>
/// <item>RegRead: 0x1000..0x100F, 0x1030..0x1035, 0x1C00..0x1C3F, 0x8020, 0x8021 at a candidate address.</item>
/// <item>RegWrite: 0x8020 ← {0, 1} and 0x80A0 ← 0x01 at a discovered controller; 0x80F8 ← slot ≤ 7 and
/// 0x80F9 ← (candidate &lt;&lt; 1) only at 0x77 and only while the remap gate is on; the single-byte colour fallback
/// inside the controller's direct range.</item>
/// <item>RegBlock: 1..3 bytes wholly inside [direct, direct + 3·ledCount) of a discovered controller.</item>
/// <item>Everything else, including the save value 0x80A0 ← 0xAA, mode/speed/direction and effect colours: denied.</item>
/// </list>
/// The direct range is validated when registered: base 0x8000 or 0x8100 only, and [base, base + 3·ledCount) must end at
/// or below that version's effect colours (0x8010 / 0x8160), so a malformed config-table count can never make a colour
/// block or byte write reach 0x8020/0x8021/0x80A0/0x80F8/0x80F9. Candidates outside
/// <see cref="EneRegisters.PhysicalCandidates"/> (incl. SPD 0x50..0x57) are refused at construction.
/// </summary>
public sealed partial class SmbusFirewall
{
    private readonly HashSet<byte> _candidates;
    private readonly Dictionary<byte, (ushort Base, int Leds)> _direct = [];
    private readonly HashSet<string> _logged = [];
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private long _denied;

    public SmbusFirewall(IEnumerable<byte> candidates, bool remapEnabled, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = [.. candidates];
        foreach (var a in _candidates)
        {
            if (EneRegisters.IsSpd(a) || !EneRegisters.PhysicalCandidates.Contains(a))
            {
                throw new ArgumentException($"0x{a:X2} is not an ENE DRAM candidate address (physical allow-list only; SPD 0x50..0x57 never).", nameof(candidates));
            }
        }

        RemapEnabled = remapEnabled;
        _logger = logger;
    }

    /// <summary>The <c>EneDram.RemapOnOpen</c> gate: admits the two bootstrap registers at 0x77.</summary>
    public bool RemapEnabled { get; }

    /// <summary>True while the adapter runs discovery (scan, re-scan after remap); Probe/ReadSig are refused otherwise.</summary>
    public bool DiscoveryOpen { get; set; }

    public long Denied => Interlocked.Read(ref _denied);

    /// <summary>
    /// Registers a discovered controller's direct colour range. Throws for a non-candidate address, a base other than
    /// 0x8000/0x8100, or a count whose range would reach that version's effect colours or any control register.
    /// </summary>
    public void SetDirect(byte address, ushort directBase, int ledCount)
    {
        if (!_candidates.Contains(address))
        {
            throw new ArgumentException($"0x{address:X2} is not a candidate", nameof(address));
        }

        var max = EneRegisters.MaxDirectLeds(directBase);
        if (max == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directBase), $"direct base 0x{directBase:X4} is not 0x8000 or 0x8100");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ledCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ledCount, max);
        lock (_gate)
        {
            _direct[address] = (directBase, ledCount);
        }
    }

    public void ForgetDirect(byte address)
    {
        lock (_gate)
        {
            _direct.Remove(address);
        }
    }

    /// <summary>Null when admitted, else the reason.</summary>
    public string? Check(SmbusCompound c)
    {
        if (!_candidates.Contains(c.Address))
        {
            return $"address 0x{c.Address:X2} is not a candidate";
        }

        switch (c.Kind)
        {
            case SmbusCompoundKind.Probe:
                return DiscoveryOpen ? null : "probe outside discovery";

            case SmbusCompoundKind.ReadSig:
                if (!DiscoveryOpen)
                {
                    return "signature read outside discovery";
                }

                return c.Command == 0x00 || (c.Command >= 0xA0 && c.Command <= 0xAF) ? null : $"signature command 0x{c.Command:X2} denied";

            case SmbusCompoundKind.RegRead:
                return IsReadable(c.Register) ? null : $"register 0x{c.Register:X4} not readable";

            case SmbusCompoundKind.RegWrite:
                return CheckWrite(c);

            case SmbusCompoundKind.RegBlock:
            {
                (ushort Base, int Leds) d;
                lock (_gate)
                {
                    if (!_direct.TryGetValue(c.Address, out d))
                    {
                        return $"0x{c.Address:X2} is not a discovered controller";
                    }
                }

                var start = (int)c.Register;
                var end = start + c.Data.Length;
                var limit = d.Base + (3 * d.Leds);
                return c.Data.Length is >= 1 and <= EneRegisters.MaxBlock && start >= d.Base && end <= limit
                    ? null
                    : $"block 0x{start:X4}+{c.Data.Length} outside direct range 0x{d.Base:X4}..0x{limit:X4}";
            }

            default:
                return $"compound {c.Kind} denied";
        }
    }

    /// <summary>Throws <see cref="ForbiddenSmbusException"/> unless admitted; each distinct denial is logged once, all are counted.</summary>
    public void Ensure(SmbusCompound c)
    {
        var reason = Check(c);
        if (reason is null)
        {
            return;
        }

        Interlocked.Increment(ref _denied);
        if (_logger is not null)
        {
            bool first;
            lock (_gate)
            {
                first = _logged.Add(reason);
            }

            if (first && _logger.IsEnabled(LogLevel.Critical))
            {
                LogBlocked(_logger, c, reason);
            }
        }

        throw new ForbiddenSmbusException($"SMBus compound {c} refused: {reason}");
    }

    private static bool IsReadable(ushort r) =>
        (r >= EneRegisters.DeviceName && r < EneRegisters.DeviceName + EneRegisters.DeviceNameLength)
        || (r >= EneRegisters.MicronCheck && r < EneRegisters.MicronCheck + EneRegisters.MicronCheckLength)
        || (r >= EneRegisters.ConfigTable && r < EneRegisters.ConfigTable + EneRegisters.ConfigTableLength)
        || r == EneRegisters.Direct
        || r == EneRegisters.Mode;

    private string? CheckWrite(SmbusCompound c)
    {
        lock (_gate)
        {
            // single-byte colour fallback: one byte inside the registered (bounded) direct range
            if (_direct.TryGetValue(c.Address, out var d) && c.Register >= d.Base && c.Register < d.Base + (3 * d.Leds))
            {
                return null;
            }
        }

        switch (c.Register)
        {
            case EneRegisters.Direct:
            case EneRegisters.Apply:
                bool known;
                lock (_gate)
                {
                    known = _direct.ContainsKey(c.Address);
                }

                if (!known)
                {
                    return $"0x{c.Address:X2} is not a discovered controller";
                }

                if (c.Register == EneRegisters.Direct)
                {
                    return c.Command <= 1 ? null : $"0x8020 value 0x{c.Command:X2} is not 0 or 1";
                }

                return c.Command == EneRegisters.ApplyValue ? null : $"0x80A0 value 0x{c.Command:X2} denied (apply 0x01 only; never save)";

            case EneRegisters.SlotIndex:
                if (!RemapEnabled || c.Address != EneRegisters.DefaultDramAddress)
                {
                    return "0x80F8 only at 0x77 with RemapOnOpen";
                }

                return c.Command <= 7 ? null : $"slot {c.Command} is not 0..7";

            case EneRegisters.I2cAddress:
            {
                if (!RemapEnabled || c.Address != EneRegisters.DefaultDramAddress)
                {
                    return "0x80F9 only at 0x77 with RemapOnOpen";
                }

                var target = (byte)(c.Command >> 1);
                return (c.Command & 1) == 0 && target != EneRegisters.DefaultDramAddress && _candidates.Contains(target)
                    ? null
                    : $"0x80F9 value 0x{c.Command:X2} is not (candidate << 1)";
            }

            default:
                return $"register 0x{c.Register:X4} not writable";
        }
    }

    [LoggerMessage(EventId = 9006, Level = LogLevel.Critical, Message = "Blocked SMBus compound {Compound}: {Reason}")]
    private static partial void LogBlocked(ILogger logger, SmbusCompound compound, string reason);
}
