using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Protocol;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>One access unit under today's TURZX picture rule, and where an accepted one may go.</summary>
internal enum TurzxAccessUnitClass : byte
{
    /// <summary>Accepted by today's rule and at most one chunk (<see cref="TurzxVideoProtocol.MaxChunkBytes"/>): may be one picture of a block.</summary>
    Batchable,

    /// <summary>Accepted by today's rule but larger than one chunk: only today's separate fragment path, never part of a block.</summary>
    FragmentOnly,

    /// <summary>Null or empty: rejected by today's rule.</summary>
    Empty,

    /// <summary>Above <see cref="MfH264IntraEncoder.MaxEncodedBytes"/>: rejected by today's rule.</summary>
    TooLarge,

    /// <summary>No SPS, PPS or IDR slice, or any non-IDR slice/partition (NAL types 1..4: P/B pictures): rejected by today's rule.</summary>
    NotIndependent,
}

internal enum TurzxBatchProblem : byte
{
    None,
    NoPictures,

    /// <summary>More than <see cref="TurzxAccessUnitBatcher.MaxPictures"/> access units.</summary>
    TooManyPictures,

    /// <summary>A picture fails today's rule (<see cref="TurzxBatch.FailedClass"/>).</summary>
    InvalidPicture,

    /// <summary>A valid picture larger than one chunk: it keeps today's fragment path and is never batched.</summary>
    NeedsFragmentPath,

    /// <summary>The concatenation would exceed <see cref="TurzxAccessUnitBatcher.MaxBytes"/>.</summary>
    TooManyBytes,
}

/// <summary>One picture of a built block: its explicit position, caller tag (e.g. a plan slot) and byte range in the payload.</summary>
internal readonly record struct TurzxBatchPicture(int Order, long Tag, int Offset, int Length);

/// <summary>
/// The outcome of one batch build. Built: ONE cmd-121 data block (<see cref="Blocks"/>) carrying <see cref="Pictures"/> access
/// units in <see cref="Bytes"/> bytes. Blocks and pictures are different units; neither says anything about device acceptance or
/// presentation. Not built: <see cref="FailedPicture"/> (-1 when the batch as a whole failed) and nothing was written.
/// </summary>
internal readonly record struct TurzxBatch(TurzxBatchProblem Problem, int FailedPicture, TurzxAccessUnitClass FailedClass, int Pictures, int Bytes)
{
    public bool Built => Problem == TurzxBatchProblem.None;

    public int Blocks => Built ? 1 : 0;
}

/// <summary>
/// Pure packing of 1..<see cref="MaxPictures"/> complete, independent access units into one block payload of at most
/// <see cref="MaxBytes"/> (one cmd-121 chunk), UNWIRED: no USB, no session change, and today's one-AU-per-SendFrame path and its
/// fragment loop are untouched. Every input is classified by exactly today's TurzxVideoSession.SendFrame rule (the same
/// <see cref="H264AnnexB.Types"/> scan and conditions; equivalence is tested against SendFrame itself) BEFORE anything is copied;
/// a P/B/invalid picture, one that needs the fragment path, or an over-size total rejects the whole batch with the destination
/// untouched. Bytes are copied unchanged, in the given order, back to back. Allocation: only the NAL-type lists of the rule's own
/// scan; the payload goes to a caller-owned buffer, so no block is retained here.
/// </summary>
internal static class TurzxAccessUnitBatcher
{
    public const int MaxPictures = 6;
    public const int MaxBytes = TurzxVideoProtocol.MaxChunkBytes;

    public static TurzxAccessUnitClass Classify(byte[]? accessUnit)
    {
        if (accessUnit is null || accessUnit.Length == 0) return TurzxAccessUnitClass.Empty;
        if (accessUnit.Length > MfH264IntraEncoder.MaxEncodedBytes) return TurzxAccessUnitClass.TooLarge;
        var types = H264AnnexB.Types(accessUnit);
        if (!types.Contains(7) || !types.Contains(8) || !types.Contains(5) || types.Any(t => t is >= 1 and <= 4))
            return TurzxAccessUnitClass.NotIndependent;
        return accessUnit.Length > MaxBytes ? TurzxAccessUnitClass.FragmentOnly : TurzxAccessUnitClass.Batchable;
    }

    /// <summary>
    /// Validates every access unit and the totals, then writes their exact concatenation to <paramref name="destination"/> and one
    /// <see cref="TurzxBatchPicture"/> per picture to <paramref name="pictures"/>. On any problem nothing is written to either.
    /// </summary>
    /// <param name="tags">One caller tag per access unit, kept with its picture (same length as <paramref name="accessUnits"/>).</param>
    /// <exception cref="ArgumentException">Mismatched tags, or a destination or picture span too small for a valid batch.</exception>
    public static TurzxBatch Build(ReadOnlySpan<byte[]> accessUnits, ReadOnlySpan<long> tags, Span<byte> destination,
        Span<TurzxBatchPicture> pictures)
    {
        if (tags.Length != accessUnits.Length) throw new ArgumentException("One tag per access unit is required.", nameof(tags));
        if (accessUnits.Length == 0) return Fail(TurzxBatchProblem.NoPictures);
        if (accessUnits.Length > MaxPictures) return Fail(TurzxBatchProblem.TooManyPictures);

        long total = 0;
        for (var i = 0; i < accessUnits.Length; i++)
        {
            var kind = Classify(accessUnits[i]);
            if (kind == TurzxAccessUnitClass.FragmentOnly) return Fail(TurzxBatchProblem.NeedsFragmentPath, i, kind);
            if (kind != TurzxAccessUnitClass.Batchable) return Fail(TurzxBatchProblem.InvalidPicture, i, kind);
            total += accessUnits[i].Length;
        }

        if (total > MaxBytes) return Fail(TurzxBatchProblem.TooManyBytes);
        if (destination.Length < total) throw new ArgumentException("The destination cannot hold this batch.", nameof(destination));
        if (pictures.Length < accessUnits.Length) throw new ArgumentException("One picture slot per access unit is required.", nameof(pictures));

        var offset = 0;
        for (var i = 0; i < accessUnits.Length; i++)
        {
            var au = accessUnits[i];
            au.CopyTo(destination[offset..]);
            pictures[i] = new(i, tags[i], offset, au.Length);
            offset += au.Length;
        }

        return new(TurzxBatchProblem.None, -1, TurzxAccessUnitClass.Batchable, accessUnits.Length, offset);
    }

    private static TurzxBatch Fail(TurzxBatchProblem problem, int picture = -1, TurzxAccessUnitClass kind = TurzxAccessUnitClass.Batchable) =>
        new(problem, picture, kind, 0, 0);
}

internal enum TurzxBlockAcceptance : byte
{
    /// <summary>The block's exact acknowledgement was read.</summary>
    Acknowledged,

    /// <summary>The write or its acknowledgement failed: the device may or may not hold the block.</summary>
    Unknown,
}

/// <summary>
/// Host record of the data blocks THIS host wrote, by pictures per block, UNWIRED. Blocks and pictures are counted separately and
/// never converted into each other by assumption. A block whose acceptance is uncertain is Unknown: never counted as accepted, and
/// never as not sent. <see cref="PicturesInNewestBlocks"/> only sums host-recorded picture counts (e.g. for a reported untaken
/// block count under the sample firmware's byte-8 reading, which is unverified live); it is not a device queue, decode or
/// presentation fact. Fixed window of <see cref="Capacity"/> blocks; no allocation after construction; not thread-safe.
/// </summary>
internal sealed class TurzxBlockLedger
{
    public const int Capacity = 8;

    private readonly byte[] _pictures = new byte[Capacity];
    private readonly bool[] _unknown = new bool[Capacity];
    private int _next, _count;

    public long AcknowledgedBlocks { get; private set; }

    public long AcknowledgedPictures { get; private set; }

    public long UnknownBlocks { get; private set; }

    public long UnknownPictures { get; private set; }

    public void Record(int pictures, TurzxBlockAcceptance acceptance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pictures, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pictures, TurzxAccessUnitBatcher.MaxPictures);
        var unknown = acceptance == TurzxBlockAcceptance.Unknown;
        if (unknown)
        {
            UnknownBlocks++;
            UnknownPictures += pictures;
        }
        else
        {
            AcknowledgedBlocks++;
            AcknowledgedPictures += pictures;
        }

        _pictures[_next] = (byte)pictures;
        _unknown[_next] = unknown;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// Pictures in the newest <paramref name="blocks"/> recorded blocks; null (unknown) when that span is older than the window or
    /// contains an Unknown block. 0 blocks: 0 pictures.
    /// </summary>
    public int? PicturesInNewestBlocks(int blocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blocks);
        if (blocks > _count) return null;
        var pictures = 0;
        for (var i = 1; i <= blocks; i++)
        {
            var at = (_next - i + Capacity) % Capacity;
            if (_unknown[at]) return null;
            pictures += _pictures[at];
        }

        return pictures;
    }
}
