using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The isolated filter-NONE repack of WIC RGBA8 overlay PNGs: exact straight RGBA and byte-identical metadata on real OverlayRenderer
/// layers, exact unfiltering of every PNG filter type across IDAT splits, explicit refusal of every invalid or unsupported input
/// (never a silent result), bounded inflation, and the budget contract. Offline only: no device, Host or USB.
/// </summary>
public sealed class LosslessOverlayPngTests
{
    /// <summary>Set to a directory to also write the benchmark table, summary and a few original/candidate pairs there.</summary>
    public const string BenchmarkDirectoryVariable = "UNIFIEDRGB_LOSSLESS_PNG_BENCH_DIR";

    private static readonly DateTimeOffset Now = HeadlessProbe.Now;
    private static readonly ScreenOverlayLayoutDto ExampleRegion = new(0.5, 0.2, 0.47, 0.66, 2, 0); // representative local layout
    private static readonly PcTelemetryDto Idle = new(Now, 0, 4L << 30, 32L << 30, 0, 30, 1L << 30, 8L << 30, "bench", null);
    private static readonly PcTelemetryDto Busy = new(Now, 57, (long)(21.3 * (1L << 30)), 64L << 30, 88, 74, (long)(11.2 * (1L << 30)), 24L << 30, "bench", null);

    private static readonly Dictionary<string, (ScreenAssignmentDto Assignment, PcTelemetryDto? Telemetry)> Overlays = new()
    {
        ["example-region"] = (Example(), HeadlessProbe.Telemetry),
        ["example-region-missing"] = (Example(), null),
        ["example-region-stale"] = (Example(), HeadlessProbe.Telemetry with { Timestamp = Now.AddSeconds(-6) }),
        ["example-region-longest"] = (Example(), RoundExamples.Long),
        ["example-region-rotated-dimmed"] = (Example() with { Rotation = 180, Brightness = 0.6 }, Busy),
        ["dashboard"] = (new("screen:turzx:a", Overlay: "dashboard", Fps: 30), HeadlessProbe.Telemetry),
        ["dashboard-missing"] = (new("screen:turzx:a", Overlay: "dashboard", Fps: 30), null),
        ["compact"] = (new("screen:turzx:a", Overlay: "compact", Fps: 30), HeadlessProbe.Telemetry),
        ["compact-no-gpu"] = (new("screen:turzx:a", Overlay: "compact", Fps: 30), RoundExamples.NoGpu),
        ["region-backed"] = (Example() with { OverlayLayout = new(0.70, 0.0, 0.30, 1.0, 1, 0.6) }, HeadlessProbe.Telemetry),
    };

    public static TheoryData<string> OverlayNames() => Data([.. Overlays.Keys]);

    private static ScreenAssignmentDto Example() =>
        new("screen:turzx:a", Overlay: "dashboard", Fps: 30, AccentColor: "#FAD61D", Fit: "contain", OverlayLayout: ExampleRegion);

    [Theory]
    [MemberData(nameof(OverlayNames))]
    public async Task Real_overlay_pngs_repack_to_identical_straight_rgba_and_identical_metadata(string name)
    {
        var (assignment, telemetry) = Overlays[name];
        var (original, result, before, after) = await Sta.RunAsync(() =>
        {
            var png = OverlayPng(assignment, telemetry);
            var r = LosslessOverlayPng.Repack(png);
            return (png, r, Straight(png), r.Candidate is null ? default : Straight(r.Candidate));
        });

        result.Outcome.Should().Be(LosslessOverlayPngOutcome.Repacked);
        result.Original.Should().BeSameAs(original);
        var candidate = result.Candidate!;
        candidate.Length.Should().BeLessThanOrEqualTo(ScreenProtocol.TurzxMaxPngBytes).And.Be(result.CandidateBytes);
        (result.Width, result.Height).Should().Be((480, 1920), "the TURZX overlay is sent in the native portrait orientation");
        result.InputFilterRows.Sum().Should().Be(1920);

        (after.Width, after.Height).Should().Be((before.Width, before.Height));
        after.Bgra.Should().Equal(before.Bgra, "every straight RGBA value decodes identically");
        NonIdat(candidate).Should().Equal(NonIdat(original), "IHDR, sRGB, gAMA, pHYs and IEND are copied byte-for-byte in place");
        Types(original).Should().ContainInOrder("IHDR", "sRGB", "gAMA", "pHYs", "IDAT", "IEND");
        Chunks(candidate).Where(c => c.Type == "IDAT").Should().OnlyContain(c => c.Bytes.Length - 12 <= LosslessOverlayPng.MaxIdatPayload);
        CrcsMatchTheExistingChunkWriter(candidate);

        var again = LosslessOverlayPng.Repack(candidate);
        again.InputFilterRows.Should().Equal(1920, 0, 0, 0, 0);
        again.Candidate.Should().Equal(candidate, "the candidate is itself a valid strict input and the repack is deterministic");
    }

    public static TheoryData<string, int> FilterSchemes() => new()
    {
        { "cycle", 97 }, { "cycle", 1 }, { "cycle", int.MaxValue }, { "none", 7 }, { "sub", 4096 },
        { "up", 13 }, { "average", 65524 }, { "paeth", 3 }, { "cross-row", 61 },
    };

    [Theory]
    [MemberData(nameof(FilterSchemes))]
    public async Task Every_filter_type_across_idat_splits_unfilters_to_the_exact_source(string scheme, int split)
    {
        var sample = split <= 3 ? Pattern(9, 7, seed: split) : Pattern(37, 29, seed: split); // a few hundred IDATs, within MaxChunks
        Func<int, byte> filterOf = scheme switch
        {
            "cycle" => y => (byte)(y % 5),
            "none" => _ => 0,
            "sub" => _ => 1,
            "up" => _ => 2,
            "average" => _ => 3,
            "paeth" => _ => 4,
            _ => y => (byte)(y % 2 == 0 ? 4 : 2 + (y % 3)), // only the filters that read the row above
        };
        var chunks = Build(sample, Zlib(Filter(sample, filterOf)), split);
        if (split == 1) chunks.Insert(5, ("IDAT", [])); // an empty IDAT inside the run is valid
        var png = Assemble(chunks);
        if (split <= 3) Chunks(png).Count(c => c.Type == "IDAT").Should().BeGreaterThan(50);

        var (result, before, after) = await Sta.RunAsync(() =>
        {
            var r = LosslessOverlayPng.Repack(png);
            return (r, Straight(png), Straight(r.Candidate!));
        });

        before.Bgra.Should().Equal(Bgra(sample), "the crafted input itself decodes to the source");
        after.Bgra.Should().Equal(Bgra(sample));
        result.InputFilterRows.Sum().Should().Be(sample.Height);
        for (var f = 0; f < 5; f++) result.InputFilterRows[f].Should().Be(Enumerable.Range(0, sample.Height).Count(y => filterOf(y) == f));
        NonIdat(result.Candidate!).Should().Equal(NonIdat(png));
        LosslessOverlayPng.Repack(result.Candidate!).InputFilterRows.Should().Equal(sample.Height, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Fully_transparent_rgb_and_alpha_edges_survive_where_a_premultiplied_round_trip_would_not()
    {
        var sample = Pattern(64, 48, seed: 3);
        var (repacked, source, viaRaster) = await Sta.RunAsync(() =>
        {
            var png = Encode(BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, Bgra(sample), 64 * 4));
            var r = LosslessOverlayPng.Repack(png);
            var roundTrip = WpfCodec.EncodePng(WpfCodec.DecodePng(png, LosslessOverlayPng.MaxPixels)); // the raster path this helper avoids
            return (Straight(r.Candidate!), Straight(png), Straight(roundTrip));
        });

        source.Bgra.Should().Equal(Bgra(sample), "WIC keeps straight RGBA as given, RGB under alpha 0 included");
        repacked.Bgra.Should().Equal(source.Bgra);
        var transparentWithColour = Enumerable.Range(0, 64 * 48).Count(p => sample.Rgba[(p * 4) + 3] == 0 && sample.Rgba[p * 4] != 0);
        transparentWithColour.Should().BeGreaterThan(100);
        viaRaster.Bgra.Should().NotEqual(source.Bgra, "a premultiplied raster round trip loses RGB under alpha 0 and rounds edges");
    }

    [Fact]
    public void Text_time_and_unknown_safe_to_copy_chunks_are_kept_byte_for_byte_in_place()
    {
        var sample = Pattern(9, 7, seed: 5);
        var chunks = Build(sample, Zlib(Filter(sample, y => (byte)(y % 5))), 11);
        chunks.Insert(4, ("iTXt", "Title\0\0\0\0overlay"u8.ToArray()));
        chunks.Insert(4, ("prIv", [1, 2, 3])); // ancillary, private, reserved bit clear, safe to copy
        chunks.Insert(chunks.Count - 1, ("tEXt", "Comment\0after IDAT"u8.ToArray()));
        chunks.Insert(chunks.Count - 1, ("tIME", [7, 234, 10, 22, 12, 0, 0]));
        var png = Assemble(chunks);

        var candidate = LosslessOverlayPng.Repack(png).Candidate!;

        NonIdat(candidate).Should().Equal(NonIdat(png));
        Types(candidate).Should().Equal("IHDR", "sRGB", "gAMA", "pHYs", "prIv", "iTXt", "IDAT", "tEXt", "tIME", "IEND");
    }

    [Theory]
    [InlineData(1920u, 480u)]
    [InlineData(480u, 1920u)]
    public async Task The_largest_raster_is_accepted_in_both_orientations(uint width, uint height)
    {
        var (png, result) = await Sta.RunAsync(() =>
        {
            var p = WpfCodec.EncodePng(new PixelFrame((int)width, (int)height));
            return (p, LosslessOverlayPng.Repack(p));
        });
        BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)).Should().Be(width);
        result.Outcome.Should().Be(LosslessOverlayPngOutcome.Repacked);
        (result.Width, result.Height).Should().Be(((int)width, (int)height));
    }

    [Theory]
    [InlineData(1921u, 1u)]
    [InlineData(1u, 1921u)]
    [InlineData(1920u, 481u)]
    [InlineData(481u, 1920u)]
    [InlineData(961u, 960u)]
    [InlineData(0x7FFFFFFFu, 1u)]
    public void A_raster_beyond_the_bound_is_refused_before_any_allocation_from_ihdr(uint width, uint height)
    {
        // The IDAT is a tiny valid stream: only the IHDR claim is large.
        var png = Assemble(Build(Ihdr(width, height), Zlib([0, 0, 0, 0, 0]), int.MaxValue));
        var (error, allocated) = Allocations(png);
        error.Should().BeOfType<NotSupportedException>().Which.Message.Should().Contain("exceeds");
        allocated.Should().BeLessThan(64 * 1024);
    }

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(1920u, 480u)]
    public void A_deflate_bomb_stops_one_byte_past_the_declared_size(uint width, uint height)
    {
        var bomb = Zlib(new byte[32 << 20]); // 32 MiB of zero rows (valid filter type 0), far beyond either declared raster
        bomb.Length.Should().BeLessThan(LosslessOverlayPng.MaxInputBytes);
        var png = Assemble(Build(Ihdr(width, height), bomb, LosslessOverlayPng.MaxIdatPayload));
        var clock = Stopwatch.StartNew();

        var (error, allocated) = Allocations(png);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        error.Should().BeOfType<InvalidDataException>().Which.Message.Should().Contain("more than");
        var declared = ((width * 4) + 1) * height;
        allocated.Should().BeLessThan(declared + bomb.Length + (256 * 1024), "only the declared raster and the joined IDAT data");
    }

    [Fact]
    public void Input_beyond_the_encoded_bound_is_refused_before_parsing()
    {
        var png = new byte[LosslessOverlayPng.MaxInputBytes + 1];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        var (error, allocated) = Allocations(png);
        error.Should().BeOfType<NotSupportedException>().Which.Message.Should().Contain("exceeds");
        allocated.Should().BeLessThan(16 * 1024);
    }

    [Fact]
    public void More_chunks_than_the_bound_are_refused()
    {
        var sample = Pattern(4, 4, seed: 1);
        var chunks = Build(sample, Zlib(Filter(sample, _ => 0)), int.MaxValue);
        for (var i = 0; i < LosslessOverlayPng.MaxChunks; i++) chunks.Insert(4, ("tEXt", "k\0v"u8.ToArray()));
        Refused<NotSupportedException>(Assemble(chunks), "*chunks*");
    }

    [Theory]
    [InlineData("IHDR", 8)]
    [InlineData("sRGB", 8)]
    [InlineData("gAMA", 9)]
    [InlineData("pHYs", 16)]
    [InlineData("IDAT", 10)]
    [InlineData("IDAT", -1)]
    [InlineData("IEND", -1)]
    public void A_crc_mismatch_in_any_chunk_is_refused(string type, int byteInChunk)
    {
        var png = Valid();
        var chunk = Chunks(png).First(c => c.Type == type);
        png[chunk.Offset + (byteInChunk < 0 ? chunk.Bytes.Length - 1 : byteInChunk)] ^= 0x10; // data byte, or the CRC itself
        Refused<InvalidDataException>(png, "*CRC*");
    }

    public static TheoryData<string> StructureDefects() => Data(
        "bad-signature", "truncated-file", "length-beyond-input", "invalid-type-byte", "ihdr-not-first", "ihdr-wrong-size", "second-ihdr",
        "idat-split-by-text", "srgb-after-idat", "phys-after-idat", "second-gama", "second-time", "srgb-and-iccp", "phys-wrong-size",
        "srgb-wrong-size", "trns-in-rgba", "hist-in-rgba", "no-idat", "no-iend", "nonempty-iend", "byte-after-iend", "chunk-after-iend");

    [Theory]
    [MemberData(nameof(StructureDefects))]
    public void Invalid_structure_and_chunk_order_are_refused(string defect)
    {
        var sample = Pattern(5, 6, seed: 2);
        var chunks = Build(sample, Zlib(Filter(sample, y => (byte)(y % 5))), 17);
        var firstIdat = chunks.FindIndex(c => c.Type == "IDAT");
        var afterIdat = chunks.FindLastIndex(c => c.Type == "IDAT") + 1;
        byte[]? raw = null;
        switch (defect)
        {
            case "bad-signature": raw = Assemble(chunks); raw[1] ^= 1; break;
            case "truncated-file": raw = Assemble(chunks)[..^20]; break;
            case "length-beyond-input": raw = Assemble(chunks); raw[^9] = 5; break; // IEND's length field claims 5 data bytes
            case "invalid-type-byte": chunks.Insert(1, ("tE1t", [])); break;
            case "ihdr-not-first": (chunks[0], chunks[1]) = (chunks[1], chunks[0]); break;
            case "ihdr-wrong-size": chunks[0] = ("IHDR", [.. chunks[0].Data, 0]); break;
            case "second-ihdr": chunks.Insert(2, chunks[0]); break;
            case "idat-split-by-text": chunks.Insert(firstIdat + 1, ("tEXt", "k\0v"u8.ToArray())); break;
            case "srgb-after-idat": chunks.Insert(afterIdat, chunks[1]); chunks.RemoveAt(1); break;
            case "phys-after-idat": chunks.Insert(afterIdat, chunks[3]); chunks.RemoveAt(3); break;
            case "second-gama": chunks.Insert(3, chunks[2]); break;
            case "second-time": chunks.InsertRange(afterIdat, [("tIME", new byte[7]), ("tIME", new byte[7])]); break;
            case "srgb-and-iccp": chunks.Insert(4, ("iCCP", [(byte)'p', 0, 0, .. Zlib([1, 2, 3])])); break;
            case "phys-wrong-size": chunks[3] = ("pHYs", chunks[3].Data[..8]); break;
            case "srgb-wrong-size": chunks[1] = ("sRGB", [0, 0]); break;
            case "trns-in-rgba": chunks.Insert(4, ("tRNS", new byte[6])); break;
            case "hist-in-rgba": chunks.Insert(4, ("hIST", new byte[2])); break;
            case "no-idat": chunks.RemoveAll(c => c.Type == "IDAT"); break;
            case "no-iend": chunks.RemoveAt(chunks.Count - 1); break;
            case "nonempty-iend": chunks[^1] = ("IEND", [0]); break;
            case "byte-after-iend": raw = [.. Assemble(chunks), 0]; break;
            case "chunk-after-iend": chunks.Add(("tEXt", "k\0v"u8.ToArray())); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        Refused<InvalidDataException>(raw ?? Assemble(chunks));
    }

    [Theory]
    [InlineData((byte)8, (byte)6, (byte)1, (byte)0, (byte)0)]
    [InlineData((byte)8, (byte)6, (byte)0, (byte)1, (byte)0)]
    [InlineData((byte)8, (byte)6, (byte)0, (byte)0, (byte)2)]
    [InlineData((byte)8, (byte)5, (byte)0, (byte)0, (byte)0)]
    [InlineData((byte)4, (byte)6, (byte)0, (byte)0, (byte)0)]
    [InlineData((byte)16, (byte)3, (byte)0, (byte)0, (byte)0)]
    public void Ihdr_values_the_specification_forbids_are_invalid(byte depth, byte colour, byte compression, byte filter, byte interlace) =>
        Refused<InvalidDataException>(Assemble(Build(Ihdr(4, 4, depth, colour, compression, filter, interlace), Zlib(new byte[68]), int.MaxValue)), "*IHDR*");

    [Theory]
    [InlineData(0u, 4u)]
    [InlineData(4u, 0u)]
    [InlineData(0x80000000u, 4u)]
    public void Ihdr_dimensions_the_specification_forbids_are_invalid(uint width, uint height) =>
        Refused<InvalidDataException>(Assemble(Build(Ihdr(width, height), Zlib(new byte[68]), int.MaxValue)), "*width or height*");

    public static TheoryData<string> UnsupportedChunks() => Data("rgb", "gray", "gray-alpha", "rgba-16", "palette", "interlaced",
        "plte-in-rgba", "unknown-critical", "apng", "unknown-unsafe-ancillary", "reserved-bit");

    [Theory]
    [MemberData(nameof(UnsupportedChunks))]
    public void Valid_png_outside_the_profile_is_refused_as_unsupported(string kind)
    {
        var sample = Pattern(4, 4, seed: 4);
        var chunks = Build(sample, Zlib(Filter(sample, _ => 0)), int.MaxValue);
        switch (kind)
        {
            case "rgb": chunks[0] = ("IHDR", Ihdr(4, 4, colour: 2)); break;
            case "gray": chunks[0] = ("IHDR", Ihdr(4, 4, colour: 0)); break;
            case "gray-alpha": chunks[0] = ("IHDR", Ihdr(4, 4, colour: 4)); break;
            case "rgba-16": chunks[0] = ("IHDR", Ihdr(4, 4, depth: 16)); break;
            case "palette": chunks[0] = ("IHDR", Ihdr(4, 4, colour: 3)); chunks.Insert(4, ("PLTE", [0, 0, 0])); break;
            case "interlaced": chunks[0] = ("IHDR", Ihdr(4, 4, interlace: 1)); break;
            case "plte-in-rgba": chunks.Insert(4, ("PLTE", [0, 0, 0])); break;
            case "unknown-critical": chunks.Insert(4, ("CRiT", [1])); break;
            case "apng": chunks.Insert(4, ("acTL", [0, 0, 0, 1, 0, 0, 0, 0])); break;
            case "unknown-unsafe-ancillary": chunks.Insert(4, ("unSF", [1])); break;
            case "reserved-bit": chunks.Insert(4, ("abcd", [1])); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        Refused<NotSupportedException>(Assemble(chunks));
    }

    [Theory]
    [InlineData("Bgr24", 2, 8, false)]
    [InlineData("Gray8", 0, 8, false)]
    [InlineData("Indexed8", 3, 8, false)]
    [InlineData("Rgba64", 6, 16, false)]
    [InlineData("Bgra32", 6, 8, true)]
    public async Task Real_wic_pngs_of_other_formats_or_interlaced_are_refused_as_unsupported(string format, int colour, int depth, bool interlace)
    {
        var png = await Sta.RunAsync(() =>
        {
            var pixelFormat = (PixelFormat)typeof(PixelFormats).GetProperty(format)!.GetValue(null)!;
            var stride = ((24 * pixelFormat.BitsPerPixel) + 7) / 8;
            var pixels = new byte[stride * 16];
            new Random(colour).NextBytes(pixels);
            var palette = format == "Indexed8" ? BitmapPalettes.Halftone256 : null; // a grey palette is written as colour type 0
            return Encode(BitmapSource.Create(24, 16, 96, 96, pixelFormat, palette, pixels, stride), interlace);
        });
        (png[25], png[24], png[28]).Should().Be(((byte)colour, (byte)depth, (byte)(interlace ? 1 : 0)), "WIC wrote the intended format");
        Refused<NotSupportedException>(png);
    }

    public static TheoryData<string> ZlibDefects() => Data("cut-1", "cut-3", "cut-4", "cut-5", "cut-half", "adler-flipped",
        "trailing-byte", "second-stream", "excess-byte", "excess-row", "missing-row", "method-7", "window-64k", "fcheck", "preset-dictionary",
        "filter-type-5", "garbage-deflate", "too-short");

    [Theory]
    [MemberData(nameof(ZlibDefects))]
    public void Corrupt_truncated_or_mis_sized_zlib_data_is_refused(string defect)
    {
        var sample = Pattern(23, 19, seed: 6);
        var filtered = Filter(sample, y => (byte)(y % 5));
        var zlib = Zlib(filtered);
        zlib = defect switch
        {
            "cut-1" => zlib[..^1],
            "cut-3" => zlib[..^3],
            "cut-4" => zlib[..^4],
            "cut-5" => zlib[..^5],
            "cut-half" => zlib[..(zlib.Length / 2)],
            "adler-flipped" => [.. zlib[..^1], (byte)(zlib[^1] ^ 1)],
            "trailing-byte" => [.. zlib, 0],
            "second-stream" => [.. zlib, .. Zlib([0])],
            "excess-byte" => Zlib([.. filtered, 0]),
            "excess-row" => Zlib([.. filtered, .. new byte[(23 * 4) + 1]]),
            "missing-row" => Zlib(filtered[..^((23 * 4) + 1)]),
            "method-7" => Header(zlib, 0x77),
            "window-64k" => Header(zlib, 0x88),
            "fcheck" => [zlib[0], (byte)(zlib[1] ^ 1), .. zlib[2..]],
            "preset-dictionary" => [0x78, 0xBB, 0, 0, 0, 1, .. zlib[2..]], // FDICT with a dictionary id; 0x78BB is a multiple of 31
            "filter-type-5" => Zlib([5, .. filtered[1..]]),
            "garbage-deflate" => [zlib[0], zlib[1], .. Enumerable.Range(0, 200).Select(i => (byte)(i * 37)), 0, 0, 0, 0],
            "too-short" => [0x78, 0x9C, 3, 0, 0],
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        var error = Refused<InvalidDataException>(Assemble(Build(sample, zlib, 64)));
        if (defect.StartsWith("excess", StringComparison.Ordinal)) error.Message.Should().Contain("more than");
        if (defect == "missing-row") error.Message.Should().Contain("fewer than");
        if (defect is "trailing-byte" or "second-stream") error.Message.Should().Contain("before the IDAT data");
        if (defect is "cut-1" or "cut-3" or "cut-4") error.Message.Should().Contain("Adler-32");
    }

    [Fact]
    public async Task Noise_beyond_the_protocol_budget_is_over_budget_with_no_candidate()
    {
        var png = await Sta.RunAsync(() =>
        {
            var noise = new PixelFrame(480, 1920);
            new Random(917).NextBytes(noise.Pixels);
            for (var i = 3; i < noise.ByteCount; i += 4) noise.Pixels[i] = 255;
            return WpfCodec.EncodePng(noise);
        });

        var result = LosslessOverlayPng.Repack(png);

        result.Outcome.Should().Be(LosslessOverlayPngOutcome.OverBudget);
        result.Candidate.Should().BeNull("nothing oversized is ever returned");
        result.Original.Should().BeSameAs(png);
        result.OriginalWithinBudget.Should().BeFalse("WIC noise is itself beyond 524288 B");
        result.CandidateBytes.Should().BeGreaterThan(ScreenProtocol.TurzxMaxPngBytes);
        result.ZlibLevel.Should().BeOneOf(LosslessOverlayPng.PrimaryZlibLevel, LosslessOverlayPng.FallbackZlibLevel);
    }

    [Fact]
    public async Task The_budget_is_inclusive_level_9_is_the_only_fallback_and_the_original_is_retained()
    {
        var png = await Sta.RunAsync(() => OverlayPng(Example(), HeadlessProbe.Telemetry));
        var primary = LosslessOverlayPng.Repack(png);
        var none = LosslessOverlayPng.Repack(png, 1);
        none.Outcome.Should().Be(LosslessOverlayPngOutcome.OverBudget);
        none.Candidate.Should().BeNull();
        none.OriginalWithinBudget.Should().BeFalse();
        var smallest = none.CandidateBytes;
        smallest.Should().BeLessThanOrEqualTo(primary.CandidateBytes);
        primary.ZlibLevel.Should().Be(LosslessOverlayPng.PrimaryZlibLevel);

        var exact = LosslessOverlayPng.Repack(png, smallest);
        exact.Outcome.Should().Be(LosslessOverlayPngOutcome.Repacked);
        exact.Candidate!.Length.Should().Be(smallest);
        exact.ZlibLevel.Should().Be(none.ZlibLevel);

        var under = LosslessOverlayPng.Repack(png, smallest - 1);
        under.Outcome.Should().Be(LosslessOverlayPngOutcome.OverBudget);
        under.Candidate.Should().BeNull();
        under.Original.Should().BeSameAs(png);
        under.OriginalWithinBudget.Should().Be(png.Length <= smallest - 1);
    }

    [Fact]
    public void Arguments_outside_the_contract_throw()
    {
        FluentActions.Invoking(() => LosslessOverlayPng.Repack(null!)).Should().ThrowExactly<ArgumentNullException>();
        foreach (var budget in new[] { 0, -1, ScreenProtocol.TurzxMaxPngBytes + 1 })
            FluentActions.Invoking(() => LosslessOverlayPng.Repack(Valid(), budget)).Should().ThrowExactly<ArgumentOutOfRangeException>();
        foreach (var empty in new[] { Array.Empty<byte>(), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 } })
            Refused<InvalidDataException>(empty);
    }

    /// <summary>
    /// The offline benchmark (also a test: every overlay must be pixel-exact and within budget). 36 representative overlays from
    /// the real OverlayRenderer: six layouts by six telemetry states. Desktop timings only; nothing here measures a device.
    /// </summary>
    [Fact]
    public async Task Representative_overlays_benchmark_exact_pixels_and_bounds()
    {
        var layouts = new (string Name, ScreenAssignmentDto Assignment)[]
        {
            ("example-region", Example()),
            ("dashboard", new("screen:turzx:a", Overlay: "dashboard", Fps: 30)),
            ("compact", new("screen:turzx:a", Overlay: "compact", Fps: 30)),
            ("region-backed", Example() with { OverlayLayout = new(0.70, 0.0, 0.30, 1.0, 1, 0.6) }),
            ("region-full-4col", Example() with { OverlayLayout = new(0.0, 0.0, 1.0, 1.0, 4, 0.0) }),
            ("example-rotated-dimmed", Example() with { Rotation = 180, Brightness = 0.6 }),
        };
        var states = new (string Name, PcTelemetryDto? Telemetry)[]
        {
            ("typical", HeadlessProbe.Telemetry), ("longest", RoundExamples.Long), ("no-gpu", RoundExamples.NoGpu),
            ("missing", null), ("idle", Idle), ("busy", Busy),
        };
        var rows = await Sta.RunAsync(() =>
        {
            _ = Measure("warm-up", layouts[0].Assignment, states[0].Telemetry); // JIT and WIC start-up are not counted
            return layouts.SelectMany(l => states.Select(s => Measure($"{l.Name}/{s.Name}", l.Assignment, s.Telemetry))).ToList();
        });

        rows.Should().HaveCount(36);
        rows.Should().OnlyContain(r => r.ExactPixels == r.Pixels, "every decoded pixel is identical");
        rows.Should().OnlyContain(r => r.CandidateBytes <= ScreenProtocol.TurzxMaxPngBytes && r.Outcome == LosslessOverlayPngOutcome.Repacked);

        if (Environment.GetEnvironmentVariable(BenchmarkDirectoryVariable) is { Length: > 0 } directory)
            WriteBenchmark(directory, rows);
    }

    private sealed record BenchmarkRow(string Name, LosslessOverlayPngOutcome Outcome, int OriginalBytes, int CandidateBytes, int ZlibLevel,
        IReadOnlyList<int> InputFilters, int Pixels, int ExactPixels, double WicEncodeMs, double RepackMs, long RepackAllocatedBytes,
        double WicDecodeOriginalMs, double WicDecodeCandidateMs, string OriginalSha256, string CandidateSha256, byte[] Original, byte[] Candidate);

    private static BenchmarkRow Measure(string name, ScreenAssignmentDto assignment, PcTelemetryDto? telemetry)
    {
        var layer = ScreenWorker.RenderVideoOverlay(new OverlayRenderer(), ScreenFamilyInfo.Turzx, assignment, telemetry, Now)!;
        var pixels = OverlayCanvas(layer, assignment);
        var start = Stopwatch.GetTimestamp();
        var png = WpfCodec.EncodePng(pixels);
        var wicMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        var times = new double[5]; LosslessOverlayPngResult? result = null; long allocated = 0;
        for (var i = 0; i < times.Length; i++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            result = LosslessOverlayPng.Repack(png);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        }
        var candidate = result!.Candidate ?? [];
        var (decodeOriginal, before) = TimedStraight(png);
        var (decodeCandidate, after) = TimedStraight(candidate);
        var exact = 0;
        for (var p = 0; p < before.Bgra.Length / 4; p++)
        {
            if (after.Bgra.Length == before.Bgra.Length && after.Bgra.AsSpan(p * 4, 4).SequenceEqual(before.Bgra.AsSpan(p * 4, 4))) exact++;
        }
        return new(name, result.Outcome, png.Length, result.CandidateBytes, result.ZlibLevel, result.InputFilterRows, before.Bgra.Length / 4, exact,
            wicMs, Median(times), allocated, decodeOriginal, decodeCandidate, Sha256(png), Sha256(candidate), png, candidate);
    }

    private static (double Ms, (int Width, int Height, byte[] Bgra) Image) TimedStraight(byte[] png)
    {
        var times = new double[3]; (int, int, byte[]) image = default;
        for (var i = 0; i < times.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            image = Straight(png);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return (Median(times), image);
    }

    private static void WriteBenchmark(string directory, List<BenchmarkRow> rows)
    {
        Directory.CreateDirectory(directory);
        var c = CultureInfo.InvariantCulture;
        var table = new List<string>
        {
            "name,outcome,original_bytes,candidate_bytes,zlib_level,in_filter0,in_filter1,in_filter2,in_filter3,in_filter4,pixels,exact_pixels," +
            "wic_encode_ms,repack_median_ms,repack_allocated_bytes,wic_decode_original_ms,wic_decode_candidate_ms,original_sha256,candidate_sha256",
        };
        table.AddRange(rows.Select(r => string.Join(',', r.Name, r.Outcome, r.OriginalBytes.ToString(c), r.CandidateBytes.ToString(c), r.ZlibLevel.ToString(c),
            string.Join(',', r.InputFilters.Select(f => f.ToString(c))), r.Pixels.ToString(c), r.ExactPixels.ToString(c), r.WicEncodeMs.ToString("F3", c),
            r.RepackMs.ToString("F3", c), r.RepackAllocatedBytes.ToString(c), r.WicDecodeOriginalMs.ToString("F3", c), r.WicDecodeCandidateMs.ToString("F3", c),
            r.OriginalSha256, r.CandidateSha256)));
        File.WriteAllLines(Path.Combine(directory, "overlays.csv"), table);

        var filters = Enumerable.Range(0, 5).Select(f => rows.Sum(r => r.InputFilters[f])).ToArray();
        var summary = new List<string>
        {
            FormattableString.Invariant($"overlays={rows.Count} exact_pixels={rows.Sum(r => (long)r.ExactPixels)}/{rows.Sum(r => (long)r.Pixels)} repacked={rows.Count(r => r.Outcome == LosslessOverlayPngOutcome.Repacked)}"),
            FormattableString.Invariant($"original_bytes total={rows.Sum(r => (long)r.OriginalBytes)} median={Median(rows.Select(r => (double)r.OriginalBytes))} min={rows.Min(r => r.OriginalBytes)} max={rows.Max(r => r.OriginalBytes)}"),
            FormattableString.Invariant($"candidate_bytes total={rows.Sum(r => (long)r.CandidateBytes)} median={Median(rows.Select(r => (double)r.CandidateBytes))} min={rows.Min(r => r.CandidateBytes)} max={rows.Max(r => r.CandidateBytes)}"),
            FormattableString.Invariant($"candidate/original ratio median={Median(rows.Select(r => (double)r.CandidateBytes / r.OriginalBytes)):F3} min={rows.Min(r => (double)r.CandidateBytes / r.OriginalBytes):F3} max={rows.Max(r => (double)r.CandidateBytes / r.OriginalBytes):F3}"),
            FormattableString.Invariant($"zlib_levels 6={rows.Count(r => r.ZlibLevel == 6)} 9={rows.Count(r => r.ZlibLevel == 9)}"),
            FormattableString.Invariant($"input_filter_rows none={filters[0]} sub={filters[1]} up={filters[2]} average={filters[3]} paeth={filters[4]} (total {filters.Sum()})"),
            FormattableString.Invariant($"wic_encode_ms median={Median(rows.Select(r => r.WicEncodeMs)):F3} max={rows.Max(r => r.WicEncodeMs):F3}"),
            FormattableString.Invariant($"repack_ms median={Median(rows.Select(r => r.RepackMs)):F3} p90={Percentile(rows.Select(r => r.RepackMs), 0.9):F3} max={rows.Max(r => r.RepackMs):F3}"),
            FormattableString.Invariant($"repack_allocated_bytes median={Median(rows.Select(r => (double)r.RepackAllocatedBytes))} max={rows.Max(r => r.RepackAllocatedBytes)}"),
            FormattableString.Invariant($"wic_decode_ms original_median={Median(rows.Select(r => r.WicDecodeOriginalMs)):F3} candidate_median={Median(rows.Select(r => r.WicDecodeCandidateMs)):F3} (desktop WIC only, not a device measurement)"),
            FormattableString.Invariant($"process_peak_working_set_bytes={PeakWorkingSet()} (whole test host process)"),
        };
        File.WriteAllLines(Path.Combine(directory, "summary.txt"), summary);

        foreach (var name in new[] { "example-region/typical", "example-region/missing", "dashboard/longest", "compact/no-gpu", "region-backed/busy" })
        {
            var row = rows.Single(r => r.Name == name);
            var file = name.Replace('/', '-');
            File.WriteAllBytes(Path.Combine(directory, file + ".original.png"), row.Original);
            File.WriteAllBytes(Path.Combine(directory, file + ".candidate.png"), row.Candidate);
        }
    }

    // ---- helpers ----

    private sealed record Sample(int Width, int Height, byte[] Rgba);

    /// <summary>The overlay bytes main sends: the worker's layer, local rotation and brightness, then the landscape rotation, via WIC.</summary>
    private static byte[] OverlayPng(ScreenAssignmentDto assignment, PcTelemetryDto? telemetry) =>
        WpfCodec.EncodePng(OverlayCanvas(ScreenWorker.RenderVideoOverlay(new OverlayRenderer(), ScreenFamilyInfo.Turzx, assignment, telemetry, Now)!, assignment));

    /// <summary>Mirrors ScreenWorker.OverlayCanvas (private) with the default landscape rotation.</summary>
    private static PixelFrame OverlayCanvas(PixelFrame layer, ScreenAssignmentDto assignment)
    {
        var canvas = PixelOps.Rotate(layer, assignment.Rotation);
        PixelOps.ApplyBrightness(canvas, assignment.Brightness);
        return PixelOps.Rotate(canvas, new ScreensOptions().TurzxLandscapeRotation);
    }

    /// <summary>WIC decode in the file's own straight 32bpp BGRA, with no format conversion (STA).</summary>
    private static (int Width, int Height, byte[] Bgra) Straight(byte[] png)
    {
        using var ms = new MemoryStream(png, writable: false);
        var frame = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache,
            BitmapCacheOption.OnLoad).Frames[0];
        frame.Format.Should().Be(PixelFormats.Bgra32);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return (frame.PixelWidth, frame.PixelHeight, pixels);
    }

    private static byte[] Encode(BitmapSource source, bool interlace = false)
    {
        var encoder = new PngBitmapEncoder { Interlace = interlace ? PngInterlaceOption.On : PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Straight RGBA with smooth ramps (so every filter predicts something), noise, alpha 0/1/254/255 edges and colour under alpha 0.</summary>
    private static Sample Pattern(int width, int height, int seed)
    {
        var random = new Random(seed); var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                rgba[i] = (byte)((x * 5) + (y * 3)); rgba[i + 1] = (byte)(x * y); rgba[i + 2] = (byte)random.Next(256);
                rgba[i + 3] = ((x + (2 * y)) % 6) switch { 0 => 0, 1 => 1, 2 => 254, 3 => 255, _ => (byte)random.Next(256) };
                if (rgba[i + 3] == 0 && rgba[i] == 0) rgba[i] = 9; // transparent pixels keep a colour
            }
        return new(width, height, rgba);
    }

    private static byte[] Bgra(Sample s)
    {
        var bgra = (byte[])s.Rgba.Clone();
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    /// <summary>An independent forward PNG filter (bpp 4), one filter type per row; type 5 is written as-is with no prediction.</summary>
    private static byte[] Filter(Sample s, Func<int, byte> filterOf)
    {
        var stride = s.Width * 4; var output = new byte[(stride + 1) * s.Height];
        for (var y = 0; y < s.Height; y++)
        {
            var f = filterOf(y); output[y * (stride + 1)] = f;
            for (var i = 0; i < stride; i++)
            {
                int x = s.Rgba[(y * stride) + i], a = i >= 4 ? s.Rgba[(y * stride) + i - 4] : 0, b = y > 0 ? s.Rgba[((y - 1) * stride) + i] : 0;
                var c = i >= 4 && y > 0 ? s.Rgba[((y - 1) * stride) + i - 4] : 0;
                var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
                var predictor = f switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => 0 };
                output[(y * (stride + 1)) + 1 + i] = unchecked((byte)(x - predictor));
            }
        }
        return output;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal)) zlib.Write(data);
        return ms.ToArray();
    }

    private static byte[] Header(byte[] zlib, byte cmf)
    {
        var flg = (byte)(zlib[1] & 0xE0);
        flg += (byte)((31 - (((cmf << 8) | flg) % 31)) % 31);
        return [cmf, flg, .. zlib[2..]];
    }

    private static byte[] Ihdr(uint width, uint height, byte depth = 8, byte colour = 6, byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        (ihdr[8], ihdr[9], ihdr[10], ihdr[11], ihdr[12]) = (depth, colour, compression, filter, interlace);
        return ihdr;
    }

    /// <summary>IHDR, the three WIC metadata chunks, the IDAT run split every <paramref name="split"/> bytes, IEND.</summary>
    private static List<(string Type, byte[] Data)> Build(Sample s, byte[] zlib, int split) => Build(Ihdr((uint)s.Width, (uint)s.Height), zlib, split);

    private static List<(string Type, byte[] Data)> Build(byte[] ihdr, byte[] zlib, int split)
    {
        var chunks = new List<(string Type, byte[] Data)> { ("IHDR", ihdr), ("sRGB", [0]), ("gAMA", [0, 0, 0xB1, 0x8F]), ("pHYs", [0, 0, 0x0E, 0xC3, 0, 0, 0x0E, 0xC3, 1]) };
        var o = 0;
        do
        {
            var n = Math.Min(split, zlib.Length - o);
            chunks.Add(("IDAT", zlib[o..(o + n)]));
            o += n;
        }
        while (o < zlib.Length);
        chunks.Add(("IEND", []));
        return chunks;
    }

    /// <summary>Chunks written by the existing BoundedPngEncoder.Chunk, an independent CRC implementation.</summary>
    private static byte[] Assemble(IEnumerable<(string Type, byte[] Data)> chunks)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        foreach (var (type, data) in chunks) BoundedPngEncoder.Chunk(ms, System.Text.Encoding.ASCII.GetBytes(type), data);
        return ms.ToArray();
    }

    private static byte[] Valid()
    {
        var sample = Pattern(6, 5, seed: 8);
        return Assemble(Build(sample, Zlib(Filter(sample, y => (byte)(y % 5))), 40));
    }

    private static List<(string Type, int Offset, byte[] Bytes)> Chunks(byte[] png)
    {
        var chunks = new List<(string, int, byte[])>();
        for (var o = 8; o + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(o));
            chunks.Add((System.Text.Encoding.ASCII.GetString(png, o + 4, 4), o, png[o..(o + 12 + length)]));
            o += 12 + length;
        }
        return chunks;
    }

    private static List<string> Types(byte[] png) => Collapse(Chunks(png).Select(c => c.Type));

    /// <summary>Every non-IDAT chunk, whole (length, type, data, CRC), with the IDAT run as one marker in its place.</summary>
    private static List<string> NonIdat(byte[] png) => Collapse(Chunks(png).Select(c => c.Type == "IDAT" ? "IDAT" : Convert.ToHexString(c.Bytes)));

    private static List<string> Collapse(IEnumerable<string> items)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            if (item != "IDAT" || list.Count == 0 || list[^1] != "IDAT") list.Add(item);
        }
        return list;
    }

    private static void CrcsMatchTheExistingChunkWriter(byte[] png)
    {
        foreach (var (type, _, bytes) in Chunks(png))
        {
            using var ms = new MemoryStream();
            BoundedPngEncoder.Chunk(ms, bytes.AsSpan(4, 4), bytes.AsSpan(8, bytes.Length - 12));
            ms.ToArray().Should().Equal(bytes, $"the {type} CRC matches an independent implementation");
        }
    }

    private static T Refused<T>(byte[] png, string message = "*") where T : Exception
    {
        LosslessOverlayPngResult? result = null;
        var thrown = FluentActions.Invoking(() => result = LosslessOverlayPng.Repack(png)).Should().ThrowExactly<T>().WithMessage(message).Which;
        result.Should().BeNull("no result is ever returned for refused input");
        return thrown;
    }

    /// <summary>The exception (if any) and the managed bytes this thread allocated inside Repack alone.</summary>
    private static (Exception? Error, long Allocated) Allocations(byte[] png)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            LosslessOverlayPng.Repack(png);
            return (null, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return (ex, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private static TheoryData<string> Data(params string[] items)
    {
        var data = new TheoryData<string>();
        foreach (var item in items) data.Add(item);
        return data;
    }

    private static long PeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.PeakWorkingSet64;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Round(q * (sorted.Length - 1))];
    }
}
