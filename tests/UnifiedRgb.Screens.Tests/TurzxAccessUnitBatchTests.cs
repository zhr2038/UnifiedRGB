using System.Buffers.Binary;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;
using Xunit.Abstractions;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The UNWIRED access-unit batch builder and block ledger (S1a part B): today's SendFrame rule exactly (proven against SendFrame on
/// a fake channel), every refusal before anything is written, exact bytes and explicit order, byte edges at one chunk, high-entropy
/// and real encoder pictures, and blocks versus pictures with uncertain acceptance kept UNKNOWN. No USB, no session change.
/// </summary>
public sealed class TurzxAccessUnitBatchTests(ITestOutputHelper output)
{
    private const byte Fill = 0xCC;
    private const int Chunk = TurzxVideoProtocol.MaxChunkBytes;

    [Fact]
    public void A_batch_is_the_exact_concatenation_in_the_given_order_with_explicit_picture_ranges()
    {
        var aus = new[] { Picture(18, 1), Picture(5000, 2), Picture(33, 3), Picture(70_001, 4), Picture(40, 5), Picture(12_345, 6) };
        var tags = new long[] { 60, 61, 62, 63, 64, 65 };
        var (batch, payload, pictures) = Build(aus, tags);

        (batch.Problem, batch.Built, batch.Blocks, batch.Pictures, batch.Bytes).Should().Be((TurzxBatchProblem.None, true, 1, 6, aus.Sum(a => a.Length)));
        payload.AsSpan(0, batch.Bytes).ToArray().Should().Equal(aus.SelectMany(a => a));
        payload.Skip(batch.Bytes).Should().OnlyContain(b => b == Fill, "nothing is written past the batch");
        var offset = 0;
        for (var i = 0; i < aus.Length; i++)
        {
            pictures[i].Should().Be(new TurzxBatchPicture(i, tags[i], offset, aus[i].Length));
            payload.AsSpan(offset, aus[i].Length).SequenceEqual(aus[i]).Should().BeTrue();
            offset += aus[i].Length;
        }

        var (reversed, reversedPayload, reversedPictures) = Build([.. Enumerable.Reverse(aus)], [.. Enumerable.Reverse(tags)]);
        reversed.Bytes.Should().Be(batch.Bytes);
        reversedPayload.AsSpan(0, reversed.Bytes).ToArray().Should().Equal(Enumerable.Reverse(aus).SelectMany(a => a), "order is the caller's, never re-sorted");
        reversedPictures.Take(6).Select(p => p.Tag).Should().Equal(65, 64, 63, 62, 61, 60);
        aus.Should().OnlyContain(a => TurzxAccessUnitBatcher.Classify(a) == TurzxAccessUnitClass.Batchable);
    }

    [Fact]
    public void Byte_edges_are_exact_at_one_chunk_and_at_the_encoder_maximum()
    {
        var full = Split(Chunk, 6, seed: 10);
        full.Sum(a => a.Length).Should().Be(Chunk);
        Build(full).Batch.Should().Be(new TurzxBatch(TurzxBatchProblem.None, -1, TurzxAccessUnitClass.Batchable, 6, Chunk));

        var over = Split(Chunk + 1, 6, seed: 11);
        var (refused, payload, pictures) = Build(over);
        refused.Should().Be(new TurzxBatch(TurzxBatchProblem.TooManyBytes, -1, TurzxAccessUnitClass.Batchable, 0, 0));
        payload.Should().OnlyContain(b => b == Fill);
        pictures.Should().OnlyContain(p => p == default);

        TurzxAccessUnitBatcher.Classify(Picture(Chunk, 12)).Should().Be(TurzxAccessUnitClass.Batchable);
        Build([Picture(Chunk, 12)]).Batch.Bytes.Should().Be(Chunk);
        var fragment = Picture(Chunk + 1, 13);
        TurzxAccessUnitBatcher.Classify(fragment).Should().Be(TurzxAccessUnitClass.FragmentOnly);
        Refused([Picture(18, 1), fragment], TurzxBatchProblem.NeedsFragmentPath, 1, TurzxAccessUnitClass.FragmentOnly);
        TurzxAccessUnitBatcher.Classify(Picture(MfH264IntraEncoder.MaxEncodedBytes, 14)).Should().Be(TurzxAccessUnitClass.FragmentOnly);
        var tooLarge = Picture(MfH264IntraEncoder.MaxEncodedBytes + 1, 15);
        TurzxAccessUnitBatcher.Classify(tooLarge).Should().Be(TurzxAccessUnitClass.TooLarge);
        Refused([tooLarge], TurzxBatchProblem.InvalidPicture, 0, TurzxAccessUnitClass.TooLarge);
    }

    [Fact]
    public void Picture_counts_tags_and_buffers_are_checked_before_anything_is_written()
    {
        Refused([], TurzxBatchProblem.NoPictures, -1, TurzxAccessUnitClass.Batchable);
        Refused([.. Enumerable.Range(0, 7).Select(i => Picture(18, i))], TurzxBatchProblem.TooManyPictures, -1, TurzxAccessUnitClass.Batchable);
        var aus = new[] { Picture(18, 1), Picture(30, 2) };
        var destination = Filled(100);
        var pictures = new TurzxBatchPicture[6];
        FluentActions.Invoking(() => TurzxAccessUnitBatcher.Build(aus, new long[1], destination, pictures)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => TurzxAccessUnitBatcher.Build(aus, new long[2], destination.AsSpan(0, 47), pictures)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => TurzxAccessUnitBatcher.Build(aus, new long[2], destination, pictures.AsSpan(0, 1))).Should().Throw<ArgumentException>();
        destination.Should().OnlyContain(b => b == Fill);
        pictures.Should().OnlyContain(p => p == default);
        TurzxAccessUnitBatcher.Build(aus, new long[2], destination.AsSpan(0, 48), pictures.AsSpan(0, 2)).Bytes.Should().Be(48);
    }

    private static readonly string[] InvalidNames =
        ["p-slice", "partition-a", "partition-b", "partition-c", "no-sps", "no-pps", "no-idr", "empty", "null", "no-start-code"];

    public static TheoryData<string> InvalidPictures() => new(InvalidNames);

    [Theory]
    [MemberData(nameof(InvalidPictures))]
    public void Any_invalid_picture_rejects_the_whole_batch_before_any_byte_is_copied(string name)
    {
        var invalid = Invalid(name);
        TurzxAccessUnitBatcher.Classify(invalid).Should().Be(name is "empty" or "null" ? TurzxAccessUnitClass.Empty : TurzxAccessUnitClass.NotIndependent);
        foreach (var at in new[] { 0, 3, 5 })
        {
            var aus = Enumerable.Range(0, 6).Select(i => Picture(100 + i, i)).ToArray();
            aus[at] = invalid!;
            Refused(aus, TurzxBatchProblem.InvalidPicture, at, TurzxAccessUnitBatcher.Classify(invalid));
        }
    }

    [Fact]
    public void Classification_is_exactly_todays_SendFrame_rule_and_chunking()
    {
        var corpus = new (string Name, byte[] Au)[]
        {
            ("minimal", Picture(18, 1)), ("three-byte start codes", ThreeByteStartCodes()), ("aud-sei-and-two-idr-slices", Extended()),
            ("chunk", Picture(Chunk, 2)), ("chunk+1", Picture(Chunk + 1, 3)), ("two chunks+1", Picture((2 * Chunk) + 1, 4)),
            ("max", Picture(MfH264IntraEncoder.MaxEncodedBytes, 5)), ("max+1", Picture(MfH264IntraEncoder.MaxEncodedBytes + 1, 6)),
        };
        var all = corpus.Concat(InvalidNames.Where(n => n != "null").Select(n => (Name: n, Au: Invalid(n)!)));
        foreach (var (name, au) in all)
        {
            using var channel = new AckChannel();
            var session = new TurzxVideoSession(channel, new ScreenTimestamp(ScreenFamily.Turzx, () => Fixed, () => Fixed), () => false);
            session.Begin(Png);
            var before = channel.DataWrites;
            var accepted = true;
            try { session.SendFrame(au); }
            catch (InvalidDataException) { accepted = false; }

            var chunks = channel.DataWrites - before;
            var kind = TurzxAccessUnitBatcher.Classify(au);
            (name, accepted, chunks > 0).Should().Be((name, kind is TurzxAccessUnitClass.Batchable or TurzxAccessUnitClass.FragmentOnly, accepted));
            if (accepted) (name, chunks).Should().Be((name, (au.Length + Chunk - 1) / Chunk));
            (name, kind == TurzxAccessUnitClass.Batchable).Should().Be((name, accepted && chunks == 1), "batchable means exactly one of today's chunks");
        }
    }

    [Fact]
    public void High_entropy_pictures_with_large_idr_slices_batch_or_refuse_exactly()
    {
        var random = new Random(20261022);
        var sizes = new[] { 9_000, 30_000, 33_000, 33_752, 47_000, 50_000 }; // 202752 together: one exact chunk
        var aus = sizes.Select(size => Picture(size, random.Next())).ToArray();
        aus.Should().OnlyContain(a => H264AnnexB.Types(a).SequenceEqual(new[] { 7, 8, 5 }), "the random payload never emulates a start code");
        var (batch, payload, _) = Build(aus);
        batch.Bytes.Should().Be(Chunk);
        payload.Should().Equal(aus.SelectMany(a => a));
        var entropy = Entropy(payload);
        output.WriteLine(FormattableString.Invariant($"six high-entropy pictures: {batch.Bytes} bytes, {entropy:F4} bits/byte"));
        entropy.Should().BeGreaterThan(7.9);
        aus[5] = Picture(50_001, 99);
        Refused(aus, TurzxBatchProblem.TooManyBytes, -1, TurzxAccessUnitClass.Batchable);
    }

    [MediaFoundationFact]
    public async Task Real_encoder_pictures_from_flat_to_noise_batch_exactly_or_keep_the_fragment_path()
    {
        var results = await Sta.RunAsync(() =>
        {
            var rows = new List<(string Name, int Bytes, TurzxAccessUnitClass Kind, byte[] Au)>();
            void Add(string name, byte[] au) => rows.Add((name, au.Length, TurzxAccessUnitBatcher.Classify(au), au));
            using var small = new MfH264IntraEncoder(64, 64, true, true, 100);
            for (var i = 0; i < 6; i++)
            {
                var au = small.Encode(PixelFrame.Solid(64, 64, (byte)(40 * i), 90, 200));
                Add(FormattableString.Invariant($"64x64 solid {i}"), au);
            }

            using var portrait = new MfH264IntraEncoder(480, 1920, true, true, 100);
            foreach (var amplitude in new[] { 0, 8, 64, 256 })
            {
                var frame = PixelFrame.Solid(480, 1920, 90, 120, 150);
                var random = new Random(amplitude + 7);
                for (var p = 0; p < frame.Pixels.Length; p += 4)
                {
                    for (var c = 0; c < 3; c++) frame.Pixels[p + c] = (byte)Math.Clamp(frame.Pixels[p + c] + random.Next(amplitude + 1) - (amplitude / 2), 0, 255);
                }

                try { Add(FormattableString.Invariant($"480x1920 q100 noise ±{amplitude / 2}"), portrait.Encode(frame)); }
                catch (InvalidDataException ex) { output.WriteLine(FormattableString.Invariant($"480x1920 noise ±{amplitude / 2}: encoder refused ({ex.Message})")); }
            }

            return rows;
        });

        foreach (var (name, bytes, kind, _) in results) output.WriteLine(FormattableString.Invariant($"{name}: {bytes} bytes, {kind}"));
        results.Should().OnlyContain(r => r.Kind == (r.Bytes <= Chunk ? TurzxAccessUnitClass.Batchable : TurzxAccessUnitClass.FragmentOnly));
        var six = results.Take(6).Select(r => r.Au).ToArray();
        var (batch, payload, _) = Build(six);
        batch.Built.Should().BeTrue();
        payload.AsSpan(0, batch.Bytes).ToArray().Should().Equal(six.SelectMany(a => a));
        foreach (var large in results.Skip(6).Where(r => r.Kind == TurzxAccessUnitClass.FragmentOnly))
            Refused([large.Au], TurzxBatchProblem.NeedsFragmentPath, 0, TurzxAccessUnitClass.FragmentOnly);
        results.Skip(6).Should().Contain(r => r.Kind == TurzxAccessUnitClass.Batchable, "a flat portrait picture fits one chunk");
    }

    [Fact]
    public void Building_allocates_only_the_rules_own_nal_type_lists_and_retains_nothing()
    {
        var aus = Enumerable.Range(0, 6).Select(i => Picture(30_000, i)).ToArray();
        var tags = new long[6];
        var destination = new byte[Chunk];
        var pictures = new TurzxBatchPicture[6];
        TurzxAccessUnitBatcher.Build(aus, tags, destination, pictures);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) TurzxAccessUnitBatcher.Build(aus, tags, destination, pictures);
        var perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / 100.0;
        output.WriteLine(FormattableString.Invariant($"allocated per 6-picture build: {perBuild:F0} bytes (payload {aus.Sum(a => a.Length)} bytes, caller-owned)"));
        perBuild.Should().BeLessThan(2048, "only the NAL type lists of the rule's scan; never the payload");
    }

    [Fact]
    public void The_ledger_counts_blocks_and_pictures_separately_and_unknown_acceptance_stays_unknown()
    {
        var ledger = new TurzxBlockLedger();
        ledger.PicturesInNewestBlocks(0).Should().Be(0);
        ledger.PicturesInNewestBlocks(1).Should().BeNull("nothing recorded yet");
        ledger.Record(6, TurzxBlockAcceptance.Acknowledged);
        ledger.Record(3, TurzxBlockAcceptance.Acknowledged);
        ledger.Record(1, TurzxBlockAcceptance.Acknowledged);
        ledger.PicturesInNewestBlocks(1).Should().Be(1);
        ledger.PicturesInNewestBlocks(2).Should().Be(4, "two untaken BLOCKS are four pictures here, not two");
        ledger.PicturesInNewestBlocks(3).Should().Be(10);
        ledger.PicturesInNewestBlocks(4).Should().BeNull();
        ledger.Record(2, TurzxBlockAcceptance.Unknown);
        ledger.PicturesInNewestBlocks(1).Should().BeNull("an uncertain block is neither accepted nor absent");
        ledger.PicturesInNewestBlocks(4).Should().BeNull();
        ledger.Record(5, TurzxBlockAcceptance.Acknowledged);
        ledger.PicturesInNewestBlocks(1).Should().Be(5);
        ledger.PicturesInNewestBlocks(2).Should().BeNull();
        (ledger.AcknowledgedBlocks, ledger.AcknowledgedPictures, ledger.UnknownBlocks, ledger.UnknownPictures).Should().Be((4L, 15L, 1L, 2L));

        for (var i = 0; i < TurzxBlockLedger.Capacity; i++) ledger.Record(1 + (i % 6), TurzxBlockAcceptance.Acknowledged);
        ledger.PicturesInNewestBlocks(TurzxBlockLedger.Capacity).Should().Be(Enumerable.Range(0, 8).Sum(i => 1 + (i % 6)), "the unknown block left the window");
        ledger.PicturesInNewestBlocks(TurzxBlockLedger.Capacity + 1).Should().BeNull("older than the fixed window");
        foreach (var invalid in new[] { 0, 7 })
            FluentActions.Invoking(() => ledger.Record(invalid, TurzxBlockAcceptance.Acknowledged)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ledger.PicturesInNewestBlocks(-1)).Should().Throw<ArgumentOutOfRangeException>();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) { ledger.Record(3, TurzxBlockAcceptance.Acknowledged); _ = ledger.PicturesInNewestBlocks(i % 9); }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    // ---- helpers ----

    private static readonly DateTime Fixed = new(2026, 10, 22, 12, 0, 0);
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1];

    private static (TurzxBatch Batch, byte[] Payload, TurzxBatchPicture[] Pictures) Build(byte[][] aus, long[]? tags = null)
    {
        var payload = Filled(Chunk);
        var pictures = new TurzxBatchPicture[TurzxAccessUnitBatcher.MaxPictures + 1];
        var batch = TurzxAccessUnitBatcher.Build(aus, tags ?? new long[aus.Length], payload, pictures);
        return (batch, payload, pictures);
    }

    private static void Refused(byte[][] aus, TurzxBatchProblem problem, int picture, TurzxAccessUnitClass kind)
    {
        var (batch, payload, pictures) = Build(aus);
        batch.Should().Be(new TurzxBatch(problem, picture, kind, 0, 0));
        (batch.Built, batch.Blocks).Should().Be((false, 0));
        payload.Should().OnlyContain(b => b == Fill, "a refused batch writes nothing");
        pictures.Should().OnlyContain(p => p == default);
    }

    private static byte[] Filled(int length)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(Fill);
        return bytes;
    }

    /// <summary>
    /// A synthetic independent picture of exactly <paramref name="length"/> bytes: SPS, PPS and an IDR slice with a seeded
    /// high-entropy payload in which no 00 00 0x (x &lt;= 3) occurs, so the AU holds exactly NAL types 7, 8, 5.
    /// </summary>
    private static byte[] Picture(int length, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 18);
        byte[] head = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65];
        var au = new byte[length];
        head.CopyTo(au, 0);
        new Random(seed).NextBytes(au.AsSpan(head.Length));
        Escape(au, head.Length);
        return au;
    }

    /// <summary>Synthetic pictures of exactly <paramref name="total"/> bytes in <paramref name="count"/> unequal parts.</summary>
    private static byte[][] Split(int total, int count, int seed)
    {
        var sizes = Enumerable.Range(1, count).Select(i => total * i / (count * (count + 1) / 2)).ToArray();
        sizes[^1] += total - sizes.Sum();
        return [.. sizes.Select((size, i) => Picture(size, seed + i))];
    }

    private static void Escape(byte[] au, int from)
    {
        for (var i = from; i < au.Length; i++)
        {
            if (i >= 2 && au[i - 2] == 0 && au[i - 1] == 0 && au[i] <= 3) au[i] = 3; // emulation prevention, length kept
        }

        au[^1] = 0x80; // rbsp stop bit: never a trailing zero
    }

    private static byte[]? Invalid(string name) => name switch
    {
        "p-slice" => [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1, 0, 0, 0, 1, 0x41, 1],
        "partition-a" => [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1, 0, 0, 1, 0x02, 1],
        "partition-b" => [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1, 0, 0, 1, 0x03, 1],
        "partition-c" => [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1, 0, 0, 1, 0x04, 1],
        "no-sps" => [0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1],
        "no-pps" => [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x65, 1],
        "no-idr" => [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1],
        "empty" => [],
        "null" => null,
        "no-start-code" => [0x67, 1, 0x68, 1, 0x65, 1, 0, 0],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] ThreeByteStartCodes() => [0, 0, 1, 0x67, 1, 0, 0, 1, 0x68, 1, 0, 0, 1, 0x65, 1];

    private static byte[] Extended() =>
        [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x06, 5, 0, 0, 0, 1, 0x65, 1, 0, 0, 1, 0x65, 2];

    private static double Entropy(byte[] bytes)
    {
        var counts = new long[256];
        foreach (var b in bytes) counts[b]++;
        return -counts.Where(c => c > 0).Sum(c => (double)c / bytes.Length * Math.Log2((double)c / bytes.Length));
    }

    /// <summary>Acknowledges every command exactly (512-byte status, depth 0); counts cmd-121 data writes. No USB.</summary>
    private sealed class AckChannel : IScreenUsbChannel
    {
        private byte[] _reply = [];

        public int DataWrites { get; private set; }

        public byte OutEndpoint => 1;

        public byte InEndpoint => 0x81;

        public void Write(ReadOnlySpan<byte> data, int timeoutMs)
        {
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, data[..512]);
            if (header[0] == TurzxVideoProtocol.DataCommand) DataWrites++;
            _reply = new byte[512];
            _reply[0] = header[0];
            _reply[1] = 200;
            BinaryPrimitives.WriteUInt32LittleEndian(_reply.AsSpan(2), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            _reply.CopyTo(buffer);
            return _reply.Length;
        }

        public void Abort()
        {
        }

        public void Dispose()
        {
        }
    }
}
