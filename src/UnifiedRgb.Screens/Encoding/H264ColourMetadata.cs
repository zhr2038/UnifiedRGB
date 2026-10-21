namespace UnifiedRgb.Screens.Encoding;

/// <summary>
/// Writes the colour description that the inbox encoder can omit from its SPS, despite IMFMediaType attributes.
/// Only our controlled Baseline output is accepted. Slice/PPS/timing/HRD bits are preserved; no pixel gain or device setting.
/// H.264 Annex E: the actual sample range, BT.709 primaries/matrix, IEC 61966-2-1 transfer for the sRGB canvas.
/// </summary>
internal static class H264ColourMetadata
{
    private static readonly int[] EmptyVuiTail = [0, 0, 0, 0, 0, 0];
    private static readonly int[] CanvasColour = [1, 13, 1];
    public static byte[] SpecifyCanvasColour(ReadOnlySpan<byte> accessUnit, bool fullRange = false)
    {
        if (accessUnit.Length is 0 or > MfH264IntraEncoder.MaxEncodedBytes) throw new InvalidDataException("Invalid H.264 access-unit size.");
        using var output = new MemoryStream(accessUnit.Length + 32);
        var (start, prefix) = FindStart(accessUnit, 0);
        if (start != 0) throw new InvalidDataException("H.264 access unit must start with an Annex B prefix.");
        var found = false;
        while (start >= 0)
        {
            var payload = start + prefix;
            var (next, nextPrefix) = FindStart(accessUnit, payload);
            var end = next < 0 ? accessUnit.Length : next;
            if (payload >= end) throw new InvalidDataException("Empty H.264 NAL.");
            output.Write(accessUnit.Slice(start, prefix));
            if ((accessUnit[payload] & 31) == 7)
            {
                output.WriteByte(accessUnit[payload]);
                output.Write(DescribeSps(accessUnit.Slice(payload + 1, end - payload - 1), fullRange));
                found = true;
            }
            else output.Write(accessUnit[payload..end]);
            start = next; prefix = nextPrefix;
        }
        if (!found || output.Length > MfH264IntraEncoder.MaxEncodedBytes) throw new InvalidDataException("H.264 colour metadata needs a bounded SPS.");
        return output.ToArray();
    }

    private static (int Offset, int Prefix) FindStart(ReadOnlySpan<byte> bytes, int from)
    {
        for (var i = from; i + 3 <= bytes.Length; i++)
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                if (i + 4 <= bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1) return (i, 4);
                if (bytes[i + 2] == 1) return (i, 3);
            }
        return (-1, 0);
    }

    private static byte[] DescribeSps(ReadOnlySpan<byte> escaped, bool fullRange)
    {
        if (escaped.Length is < 4 or > 4096) throw new InvalidDataException("H.264 SPS exceeds the controlled Baseline bounds.");
        var rbsp = new List<byte>(escaped.Length); var zeros = 0;
        foreach (var value in escaped)
        {
            if (zeros >= 2 && value == 3) { zeros = 0; continue; }
            rbsp.Add(value); zeros = value == 0 ? zeros + 1 : 0;
        }
        var bits = new List<int>(rbsp.Count * 8);
        foreach (var value in rbsp) for (var i = 7; i >= 0; i--) bits.Add((value >> i) & 1);
        var stop = bits.FindLastIndex(b => b != 0);
        if (stop < 24) throw new InvalidDataException("H.264 SPS has no trailing stop bit.");
        var reader = new Bits(bits, stop);
        if (reader.Read(8) != 66) throw new InvalidDataException("Only controlled Baseline SPS colour metadata is supported.");
        reader.Read(16); reader.Ue(); reader.Ue();
        var order = reader.Ue();
        if (order == 0) reader.Ue();
        else if (order == 1)
        {
            reader.Read(1); reader.Ue(); reader.Ue();
            var cycle = reader.Ue(); if (cycle > 255) throw new InvalidDataException("H.264 POC cycle exceeds the SPS budget.");
            for (var i = 0; i < cycle; i++) reader.Ue(); // signed values have the same Exp-Golomb bit width
        }
        else if (order != 2) throw new InvalidDataException("Unsupported H.264 POC syntax.");
        reader.Ue(); reader.Read(1); reader.Ue(); reader.Ue();
        if (reader.Read(1) == 0) reader.Read(1);
        reader.Read(1);
        if (reader.Read(1) != 0) for (var i = 0; i < 4; i++) reader.Ue();
        var vuiFlag = reader.Position;
        var hasVui = reader.Read(1) != 0;
        var patched = new List<int>(bits.Count + 40);
        if (hasVui)
        {
            if (reader.Read(1) != 0) { var aspect = reader.Read(8); if (aspect == 255) reader.Read(32); }
            if (reader.Read(1) != 0) reader.Read(1);
            var signal = reader.Position;
            if (reader.Read(1) != 0) { reader.Read(4); if (reader.Read(1) != 0) reader.Read(24); }
            patched.AddRange(bits.Take(signal)); WriteSignal(patched, fullRange);
            patched.AddRange(bits.Skip(reader.Position).Take(stop - reader.Position));
        }
        else
        {
            patched.AddRange(bits.Take(vuiFlag)); patched.Add(1); // vui_parameters_present_flag
            patched.Add(0); patched.Add(0); // aspect ratio / overscan absent
            WriteSignal(patched, fullRange);
            patched.AddRange(EmptyVuiTail); // chroma location, timing, both HRD, picture structure, restrictions absent
        }
        patched.Add(1); while ((patched.Count & 7) != 0) patched.Add(0);
        var result = new List<byte>(escaped.Length + 16); zeros = 0;
        for (var i = 0; i < patched.Count; i += 8)
        {
            byte value = 0; for (var b = 0; b < 8; b++) value = (byte)((value << 1) | patched[i + b]);
            if (zeros >= 2 && value <= 3) { result.Add(3); zeros = 0; }
            result.Add(value); zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }

    private static void WriteSignal(List<int> bits, bool fullRange)
    {
        // video_signal_type_present=1, video_format=5, full_range matches the samples, colour_description_present=1
        bits.Add(1); bits.Add(1); bits.Add(0); bits.Add(1); bits.Add(fullRange ? 1 : 0); bits.Add(1);
        foreach (var value in CanvasColour) for (var i = 7; i >= 0; i--) bits.Add((value >> i) & 1);
    }

    private sealed class Bits(List<int> bits, int end)
    {
        public int Position { get; private set; }
        public uint Read(int count)
        {
            if (count is < 1 or > 32 || Position + count > end) throw new InvalidDataException("Truncated H.264 SPS.");
            uint value = 0; for (var i = 0; i < count; i++) value = (value << 1) | (uint)bits[Position++];
            return value;
        }
        public uint Ue()
        {
            var zeros = 0; while (Read(1) == 0) if (++zeros > 24) throw new InvalidDataException("H.264 Exp-Golomb exceeds the SPS budget.");
            return zeros == 0 ? 0 : (1u << zeros) - 1 + Read(zeros);
        }
    }
}
