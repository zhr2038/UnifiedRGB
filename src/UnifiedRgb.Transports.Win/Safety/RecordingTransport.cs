using System.Buffers.Binary;
using System.Diagnostics;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Safety;

public enum RecordDirection : byte
{
    Out = 0,
    In = 1,
}

public sealed record RecordedFrame(RecordDirection Direction, long TimestampTicks, byte[] Data);

/// <summary>
/// Records every frame sent (optionally passing through to an inner transport) and replays scripted
/// responses for reads. File format: magic URGBREC1 then entries [u8 dir][i64 ticks LE][i32 len LE][bytes].
/// </summary>
public sealed class RecordingTransport : ITransport
{
    private readonly ITransport? _inner;
    private readonly object _gate = new();
    private readonly List<RecordedFrame> _frames = [];
    private readonly Queue<byte[]> _scriptedReads = new();

    public RecordingTransport(ITransport? inner = null) => _inner = inner;

    private static ReadOnlySpan<byte> Magic => "URGBREC1"u8;

    public IReadOnlyList<RecordedFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }
    }

    public IReadOnlyList<byte[]> SentFrames
    {
        get
        {
            lock (_gate)
            {
                return _frames.Where(f => f.Direction == RecordDirection.Out).Select(f => f.Data).ToArray();
            }
        }
    }

    public void EnqueueRead(byte[] response)
    {
        lock (_gate)
        {
            _scriptedReads.Enqueue(response);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _scriptedReads.Clear();
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        lock (_gate)
        {
            _frames.Add(new RecordedFrame(RecordDirection.Out, Stopwatch.GetTimestamp(), frame.ToArray()));
        }

        if (_inner is not null)
        {
            await _inner.SendAsync(frame, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        byte[]? scripted = null;
        lock (_gate)
        {
            if (_scriptedReads.Count > 0)
            {
                scripted = _scriptedReads.Dequeue();
            }
        }

        int n;
        if (scripted is not null)
        {
            n = Math.Min(buffer.Length, scripted.Length);
            scripted.AsSpan(0, n).CopyTo(buffer.Span);
        }
        else if (_inner is not null)
        {
            n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        else
        {
            return 0;
        }

        lock (_gate)
        {
            _frames.Add(new RecordedFrame(RecordDirection.In, Stopwatch.GetTimestamp(), buffer.Span[..n].ToArray()));
        }

        return n;
    }

    public void Save(Stream stream) => Save(stream, Frames);

    public static void Save(Stream stream, IEnumerable<RecordedFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frames);
        stream.Write(Magic);
        Span<byte> hdr = stackalloc byte[13];
        foreach (var f in frames)
        {
            hdr[0] = (byte)f.Direction;
            BinaryPrimitives.WriteInt64LittleEndian(hdr[1..], f.TimestampTicks);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[9..], f.Data.Length);
            stream.Write(hdr);
            stream.Write(f.Data);
        }
    }

    public static IReadOnlyList<RecordedFrame> Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a URGBREC1 recording.");
        }

        var list = new List<RecordedFrame>();
        Span<byte> hdr = stackalloc byte[13];
        while (stream.ReadAtLeast(hdr, hdr.Length, throwOnEndOfStream: false) == hdr.Length)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(hdr[9..]);
            if (len < 0 || len > 1 << 20)
            {
                throw new InvalidDataException("Corrupt recording entry length.");
            }

            var data = new byte[len];
            stream.ReadExactly(data);
            list.Add(new RecordedFrame((RecordDirection)hdr[0], BinaryPrimitives.ReadInt64LittleEndian(hdr[1..]), data));
        }

        return list;
    }
}
