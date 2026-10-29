using System.Runtime.Intrinsics;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The faster NV12 conversion against the verbatim scalar oracle (<see cref="Nv12Oracle"/>), for BOTH its 256-bit vector path and its
/// scalar path (the tail/no-acceleration path), in full and limited range. Chroma is a function of the rounded per-channel 2x2 averages
/// only, so uniform 2x2 blocks of all 2^24 colours exhaust every possible luma input and every possible chroma input; the averaging
/// itself is covered separately for every channel sum 0..1020.
/// </summary>
public sealed class Nv12ConverterTests
{
    private static readonly bool[] Ranges = [true, false];

    [Fact]
    public void The_vector_path_is_actually_exercised_on_this_host() =>
        Vector256.IsHardwareAccelerated.Should().BeTrue("otherwise the vector assertions below would silently test the scalar path twice");

    [Fact]
    public void Every_24_bit_colour_as_a_uniform_2x2_block_equals_the_oracle_in_both_ranges_and_paths()
    {
        const int width = 1024, height = 64; // 512 x 32 blocks = 16384 colours per raster, all in vector-width columns
        const int blocks = width / 2 * (height / 2);
        var frame = new PixelFrame(width, height);
        var actual = new byte[width * height * 3 / 2];
        for (var first = 0; first < 1 << 24; first += blocks)
        {
            for (var k = 0; k < blocks; k++)
            {
                var colour = first + k;
                var bx = k % (width / 2) * 2; var by = k / (width / 2) * 2;
                for (var yy = 0; yy < 2; yy++)
                for (var xx = 0; xx < 2; xx++)
                {
                    var i = (((by + yy) * width) + bx + xx) * 4;
                    frame.Pixels[i] = (byte)colour; frame.Pixels[i + 1] = (byte)(colour >> 8); frame.Pixels[i + 2] = (byte)(colour >> 16);
                    frame.Pixels[i + 3] = (byte)(colour * 31 + xx + yy); // alpha varies and must be ignored
                }
            }

            foreach (var full in Ranges)
            {
                var expected = Nv12Oracle.ToNv12(frame, full);
                foreach (var vector in new[] { true, false })
                {
                    Nv12Converter.Convert(frame.Pixels, width, height, full, actual, vector);
                    Nv12Rasters.FirstDifference(actual, expected).Should().Be(-1, $"colours {first:X6}.. full={full} vector={vector}");
                }
            }
        }
    }

    [Fact]
    public void Every_channel_sum_rounds_like_the_oracle_including_mixed_residues()
    {
        // Channel sums 0..1020 (each of the 4 pixels within 0..255), every residue mod 4, with the other channels' sums drawn from a
        // seeded generator so the packed vector fields see unrelated neighbours, plus varied alpha.
        var random = new Random(1021);
        const int width = 2048, height = 2; // 1024 blocks per raster: 1021 sums + 3 random blocks
        var frame = new PixelFrame(width, height);
        var actual = new byte[width * height * 3 / 2];
        for (var channel = 0; channel < 3; channel++)
        for (var pass = 0; pass < 8; pass++)
        {
            for (var k = 0; k < width / 2; k++)
            {
                var sums = new[] { random.Next(1021), random.Next(1021), random.Next(1021) };
                if (k < 1021) sums[channel] = k;
                var split = new int[3][];
                for (var c = 0; c < 3; c++) split[c] = Split(sums[c], random, pass);
                for (var p = 0; p < 4; p++)
                {
                    var i = ((p / 2 * width) + (k * 2) + (p % 2)) * 4;
                    frame.Pixels[i] = (byte)split[0][p]; frame.Pixels[i + 1] = (byte)split[1][p]; frame.Pixels[i + 2] = (byte)split[2][p];
                    frame.Pixels[i + 3] = (byte)random.Next(256);
                }
            }

            foreach (var full in Ranges)
            {
                var expected = Nv12Oracle.ToNv12(frame, full);
                foreach (var vector in new[] { true, false })
                {
                    Nv12Converter.Convert(frame.Pixels, width, height, full, actual, vector);
                    Nv12Rasters.FirstDifference(actual, expected).Should().Be(-1, $"channel {channel} pass {pass} full={full} vector={vector}");
                }
            }
        }
    }

    /// <summary>Four 0..255 values summing to <paramref name="sum"/>: even, skewed or random distributions.</summary>
    private static int[] Split(int sum, Random random, int pass)
    {
        var values = new int[4];
        if (pass % 2 == 0)
        {
            for (var p = 0; p < 4; p++) values[p] = (sum / 4) + (p < sum % 4 ? 1 : 0); // even
            if (pass % 4 == 2) Array.Reverse(values);
            return values;
        }

        var left = sum;
        for (var p = 0; p < 4; p++)
        {
            var rest = 3 - p; // values still to place after this one, each at most 255
            var low = Math.Max(0, left - (rest * 255)); var high = Math.Min(255, left);
            values[p] = pass % 4 == 1 ? high : random.Next(low, high + 1); // skewed or random
            if (values[p] < low) values[p] = low;
            left -= values[p];
        }

        return values;
    }

    [Fact]
    public void Saturated_corners_and_their_checkerboards_equal_the_oracle()
    {
        var corners = new List<(byte B, byte G, byte R)>();
        foreach (var b in new byte[] { 0, 1, 254, 255 })
        foreach (var g in new byte[] { 0, 1, 254, 255 })
        foreach (var r in new byte[] { 0, 1, 254, 255 })
            corners.Add((b, g, r));

        const int width = 64, height = 4;
        var frame = new PixelFrame(width, height);
        var actual = new byte[width * height * 3 / 2];
        foreach (var a in corners)
        foreach (var c in corners)
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                // Rows 0-1: checkerboard of the two corners; rows 2-3: uniform blocks alternating between them.
                var first = y < 2 ? ((x + y) & 1) == 0 : (x / 2 & 1) == 0;
                var (b, g, r) = first ? a : c;
                var i = ((y * width) + x) * 4;
                frame.Pixels[i] = b; frame.Pixels[i + 1] = g; frame.Pixels[i + 2] = r; frame.Pixels[i + 3] = (byte)(first ? 255 : 0);
            }

            foreach (var full in Ranges)
            {
                var expected = Nv12Oracle.ToNv12(frame, full);
                foreach (var vector in new[] { true, false })
                {
                    Nv12Converter.Convert(frame.Pixels, width, height, full, actual, vector);
                    Nv12Rasters.FirstDifference(actual, expected).Should().Be(-1, $"{a} / {c} full={full} vector={vector}");
                }
            }
        }
    }

    [Fact]
    public void Clamped_outputs_are_reached_and_preserved()
    {
        // The chroma clamps are live: full-range U/V reach 256 before clamping and limited-range U reaches 15 (cyan V lands on 16).
        foreach (var (b, g, r, full, index, expected) in new (byte, byte, byte, bool, int, byte)[]
                 {
                     (255, 0, 0, true, 4, 255), (0, 0, 255, true, 5, 255), // blue U and red V would be 256
                     (0, 255, 255, false, 4, 16), (255, 255, 0, false, 5, 16), // yellow U would be 15; cyan V is exactly 16
                     (0, 0, 0, false, 0, 16), (255, 255, 255, false, 0, 235), (255, 255, 255, true, 0, 255), (0, 0, 0, true, 0, 0),
                 })
        {
            var frame = PixelFrame.Solid(2, 2, r, g, b);
            Nv12Oracle.ToNv12(frame, full)[index].Should().Be(expected);
            MfH264IntraEncoder.ToNv12(frame, full)[index].Should().Be(expected);
        }
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 6)]
    [InlineData(30, 2)]
    [InlineData(32, 2)]
    [InlineData(34, 4)]
    [InlineData(62, 6)]
    [InlineData(64, 2)]
    [InlineData(96, 8)]
    [InlineData(126, 10)]
    [InlineData(480, 480)]
    [InlineData(480, 1920)]
    [InlineData(1920, 480)]
    [InlineData(1922, 4)]
    public void Random_rasters_with_random_alpha_and_every_tail_width_equal_the_oracle(int width, int height)
    {
        for (var seed = 0; seed < 3; seed++)
        {
            var frame = Nv12Rasters.Noise(width, height, (width * 7919) + height + seed);
            foreach (var full in Ranges)
            {
                var expected = Nv12Oracle.ToNv12(frame, full);
                MfH264IntraEncoder.ToNv12(frame, full).Should().Equal(expected, "the product entry point");
                var actual = new byte[expected.Length];
                Nv12Converter.Convert(frame.Pixels, width, height, full, actual, vector: false);
                Nv12Rasters.FirstDifference(actual, expected).Should().Be(-1, $"{width}x{height} seed {seed} full={full} scalar path");
            }
        }
    }

    [Fact]
    public async Task Representative_native_rasters_equal_the_oracle_on_both_paths()
    {
        foreach (var (name, frame) in await Nv12Rasters.RepresentativeAsync())
        foreach (var full in Ranges)
        {
            var expected = Nv12Oracle.ToNv12(frame, full);
            foreach (var vector in new[] { true, false })
            {
                var actual = new byte[expected.Length];
                Nv12Converter.Convert(frame.Pixels, frame.Width, frame.Height, full, actual, vector);
                Nv12Rasters.FirstDifference(actual, expected).Should().Be(-1, $"{name} full={full} vector={vector}");
            }
        }
    }

    [Fact]
    public void Writes_only_the_nv12_bytes_and_rejects_odd_or_short_buffers()
    {
        var frame = Nv12Rasters.Noise(64, 4, 11);
        var target = Enumerable.Repeat((byte)0xA5, (64 * 4 * 3 / 2) + 7).ToArray();
        Nv12Converter.Convert(frame.Pixels, 64, 4, true, target);
        target.AsSpan(0, 384).ToArray().Should().Equal(Nv12Oracle.ToNv12(frame, true));
        target.AsSpan(384).ToArray().Should().OnlyContain(v => v == 0xA5, "nothing beyond the NV12 picture is written");

        var odd = () => Nv12Converter.Convert(new byte[3 * 2 * 4], 3, 2, true, new byte[9]);
        odd.Should().Throw<ArgumentOutOfRangeException>();
        var shortSource = () => Nv12Converter.Convert(new byte[(64 * 4 * 4) - 1], 64, 4, true, new byte[384]);
        shortSource.Should().Throw<ArgumentException>();
        var shortTarget = () => Nv12Converter.Convert(frame.Pixels, 64, 4, true, new byte[383]);
        shortTarget.Should().Throw<ArgumentException>();
    }
}
