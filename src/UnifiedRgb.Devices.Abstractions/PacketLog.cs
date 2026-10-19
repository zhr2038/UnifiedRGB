namespace UnifiedRgb.Devices;

/// <summary>One frame that reached a physical transport: opcode and length only, never the payload.</summary>
public readonly record struct PacketRecord(DateTimeOffset Timestamp, string Channel, int Opcode, int Length);

/// <summary>
/// Diagnostics ring buffer of the last <see cref="Capacity"/> frames sent to hardware (ARCHITECTURE §10), exposed through
/// GetStatus(diagnostics: true). Adapters record through <see cref="PacketTap"/>; <see cref="Shared"/> is the process-wide log.
/// </summary>
public sealed class PacketLog
{
    public const int DefaultCapacity = 2000;

    private readonly PacketRecord[] _ring;
    private readonly object _gate = new();
    private long _count;

    public PacketLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new PacketRecord[capacity];
    }

    public static PacketLog Shared { get; } = new();

    public int Capacity => _ring.Length;

    /// <summary>Total frames recorded since start (including those already overwritten).</summary>
    public long Total
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Record(string channel, int opcode, int length)
    {
        var r = new PacketRecord(DateTimeOffset.UtcNow, channel, opcode, length);
        lock (_gate)
        {
            _ring[_count % _ring.Length] = r;
            _count++;
        }
    }

    /// <summary>Oldest → newest copy of the retained records.</summary>
    public PacketRecord[] Snapshot()
    {
        lock (_gate)
        {
            var n = (int)Math.Min(_count, _ring.Length);
            var result = new PacketRecord[n];
            var start = _count - n;
            for (var i = 0; i < n; i++)
            {
                result[i] = _ring[(start + i) % _ring.Length];
            }

            return result;
        }
    }
}

/// <summary>
/// Innermost transport decorator: records every frame the physical transport accepted. Placed below the firewall, so a
/// blocked frame never appears here (the firewall logs those at Critical instead).
/// </summary>
public sealed class PacketTap(ITransport inner, string channel, Func<ReadOnlyMemory<byte>, int> opcodeOf, PacketLog? log = null) : ITransport
{
    private readonly ITransport _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly PacketLog _log = log ?? PacketLog.Shared;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _inner.SendAsync(frame, ct).ConfigureAwait(false);
        _log.Record(channel, frame.IsEmpty ? -1 : opcodeOf(frame), frame.Length);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
}
