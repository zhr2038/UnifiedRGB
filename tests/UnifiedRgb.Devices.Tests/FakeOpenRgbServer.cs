using System.Buffers.Binary;
using System.Text;

namespace UnifiedRgb.Devices.Tests;

/// <summary>Test-side controller description, encoded by <see cref="OrgbSpec"/> independently of the product parser.</summary>
internal sealed record SpecZone(string Name, int Type, uint Leds, uint Width = 0, uint Height = 0);

internal sealed record SpecController(int Type, string Name, string Vendor, string Location, string[] Modes, SpecZone[] Zones);

/// <summary>
/// Encodes OpenRGB SDK controller blobs straight from the protocol description (NetworkProtocol.md, v3/v4), field by
/// field, sharing no code with UnifiedRgb.Transports.OpenRgb.
/// </summary>
internal static class OrgbSpec
{
    public static SpecController EneDram(string location) =>
        new(1, "ENE DRAM", "ENE", location,
            ["Direct", "Off", "Static", "Breathing", "Flashing", "Spectrum Cycle", "Rainbow", "Chase Fade", "Chase", "Random Flicker"],
            [new("DRAM", 1, 8)]);

    public static SpecController AsusMb() =>
        new(0, "ASUS ROG STRIX B850-F GAMING WIFI", "ASUS", "HID: \\\\?\\HID#VID_0B05&PID_19AF",
            ["Direct", "Off", "Static", "Breathing", "Flashing", "Spectrum Cycle", "Rainbow", "Chase Fade", "Chase"],
            [new("Aura Mainboard", 1, 2), new("Aura Addressable 1", 1, 20), new("Aura Addressable 2", 1, 20), new("Aura Addressable 3", 1, 20)]);

    public static SpecController MsiGpu() =>
        new(2, "MSI GeForce RTX 5090 SUPRIM", "MSI", "I2C: NVIDIA, address 0x68", ["Direct", "Static"], [new("GPU", 0, 1)]);

    public static SpecController LianLi() =>
        new(4, "Lian Li Uni Hub - SL V2", "Lian Li", "HID: 0CF2:A105", ["Direct"], [new("Channel 1", 1, 16)]);

    public static byte[] Controller(SpecController c, uint protocol)
    {
        var body = new List<byte>();
        I32(body, c.Type);
        Str(body, c.Name);
        Str(body, c.Vendor);
        Str(body, c.Name + " (spec)"); // description
        Str(body, "1.0"); // version
        Str(body, string.Empty); // serial
        Str(body, c.Location);
        U16(body, (ushort)c.Modes.Length);
        I32(body, 0); // active mode
        for (var i = 0; i < c.Modes.Length; i++)
        {
            Str(body, c.Modes[i]);
            I32(body, i == 0 ? 0xFFFF : i); // mode value
            U32(body, i == 0 ? 0x20u : 0x10u); // flags
            U32(body, 0); // speed min
            U32(body, 0); // speed max
            if (protocol >= 3)
            {
                U32(body, 0); // brightness min
                U32(body, 0); // brightness max
            }

            U32(body, 0); // colors min
            U32(body, i == 2 ? 1u : 0u); // colors max
            U32(body, 0); // speed
            if (protocol >= 3)
            {
                U32(body, 0); // brightness
            }

            U32(body, 0); // direction
            U32(body, i == 0 ? 1u : 0u); // color mode (per-LED for Direct)
            if (i == 2)
            {
                U16(body, 1);
                U32(body, 0x0000FF);
            }
            else
            {
                U16(body, 0);
            }
        }

        U16(body, (ushort)c.Zones.Length);
        var leds = 0u;
        foreach (var z in c.Zones)
        {
            Str(body, z.Name);
            I32(body, z.Type);
            U32(body, z.Leds); // min
            U32(body, z.Leds); // max
            U32(body, z.Leds); // count
            if (z.Type == 2)
            {
                U16(body, (ushort)((2 + (z.Width * z.Height)) * 4));
                U32(body, z.Height);
                U32(body, z.Width);
                for (var i = 0u; i < z.Width * z.Height; i++)
                {
                    U32(body, i);
                }
            }
            else
            {
                U16(body, 0);
            }

            if (protocol >= 4)
            {
                U16(body, 0); // segments
            }

            leds += z.Leds;
        }

        U16(body, (ushort)leds);
        for (var i = 0; i < leds; i++)
        {
            Str(body, $"LED {i + 1}");
            U32(body, (uint)i);
        }

        U16(body, (ushort)leds);
        for (var i = 0; i < leds; i++)
        {
            U32(body, 0);
        }

        var blob = new byte[4 + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)blob.Length);
        body.CopyTo(blob, 4);
        return blob;
    }

    public static byte[] Packet(uint dev, uint id, byte[] payload)
    {
        var p = new byte[16 + payload.Length];
        "ORGB"u8.CopyTo(p);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), dev);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(p, 16);
        return p;
    }

    private static void Str(List<byte> b, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        U16(b, (ushort)(bytes.Length + 1));
        b.AddRange(bytes);
        b.Add(0);
    }

    private static void U16(List<byte> b, ushort v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }

    private static void U32(List<byte> b, uint v)
    {
        for (var i = 0; i < 4; i++)
        {
            b.Add((byte)(v >> (8 * i)));
        }
    }

    private static void I32(List<byte> b, int v) => U32(b, unchecked((uint)v));
}

/// <summary>In-memory OpenRGB SDK server speaking the request/reply side of the protocol.</summary>
internal sealed class FakeOpenRgbServer : ITransport, IDisposable
{
    private readonly Queue<byte> _out = new();
    private readonly object _gate = new();

    public FakeOpenRgbServer(uint protocol = 4, params SpecController[] controllers)
    {
        Protocol = protocol;
        Controllers = [.. controllers];
    }

    public uint Protocol { get; }

    public List<SpecController> Controllers { get; }

    /// <summary>Every packet received: (device index, packet id, payload).</summary>
    public List<(uint Dev, uint Id, byte[] Payload)> Received { get; } = [];

    public string? ClientName { get; private set; }

    public bool Dead { get; set; }

    public bool Disposed { get; private set; }

    public void Notify() => Enqueue(OrgbSpec.Packet(0, 100, []));

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (Dead || Disposed)
        {
            throw new IOException("connection reset");
        }

        var f = frame.ToArray();
        var dev = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(4));
        var id = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(8));
        var payload = f[16..];
        lock (_gate)
        {
            Received.Add((dev, id, payload));
        }

        switch (id)
        {
            case 40:
                Enqueue(OrgbSpec.Packet(0, 40, U32(Math.Min(Protocol, BinaryPrimitives.ReadUInt32LittleEndian(payload)))));
                break;
            case 0:
                Enqueue(OrgbSpec.Packet(0, 0, U32((uint)Controllers.Count)));
                break;
            case 1:
                Enqueue(OrgbSpec.Packet(dev, 1, OrgbSpec.Controller(Controllers[(int)dev], BinaryPrimitives.ReadUInt32LittleEndian(payload))));
                break;
            case 50:
                ClientName = Encoding.ASCII.GetString(payload).TrimEnd('\0');
                break;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Dead || _out.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            var n = Math.Min(buffer.Length, _out.Count);
            var span = buffer.Span;
            for (var i = 0; i < n; i++)
            {
                span[i] = _out.Dequeue();
            }

            return ValueTask.FromResult(n);
        }
    }

    public List<(uint Dev, uint Id, byte[] Payload)> Sent(uint id)
    {
        lock (_gate)
        {
            return Received.Where(r => r.Id == id).ToList();
        }
    }

    public void Dispose() => Disposed = true;

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private void Enqueue(byte[] bytes)
    {
        lock (_gate)
        {
            foreach (var b in bytes)
            {
                _out.Enqueue(b);
            }
        }
    }
}
