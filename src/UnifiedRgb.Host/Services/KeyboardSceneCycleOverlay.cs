using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// One immutable keyboard OLED scene caption: the selection's 1-based <see cref="Index"/> (0 when nothing is selected), the list
/// length, its name and a short <see cref="State"/> tag that says whether it is only selected, held (pending) or applied, never more
/// than the scene cycle reported. The packed caption band is rendered once, off the keyboard actor.
/// </summary>
public sealed class KeyboardSceneCycleCaption
{
    internal KeyboardSceneCycleCaption(long revision, int index, int count, string name, string state, string text, ReadOnlyMemory<byte> strip)
    {
        Revision = revision;
        Index = index;
        Count = count;
        Name = name;
        State = state;
        Text = text;
        Strip = strip;
    }

    public long Revision { get; }

    public int Index { get; }

    public int Count { get; }

    public string Name { get; }

    public string State { get; }

    /// <summary>The caption exactly as drawn: left text, then the state tag when there is one.</summary>
    public string Text { get; }

    /// <summary>Packed 4-bit rows <see cref="KeyboardSceneCycleOverlay.StripTop"/>..63; empty when rendering failed (nothing is drawn).</summary>
    internal ReadOnlyMemory<byte> Strip { get; }
}

/// <summary>
/// The <c>nativeFrameOverlay</c> of the keyboard display: <see cref="Apply"/> reads only the current immutable caption snapshot and,
/// when one is shown, returns a clone of the packed frame with the pre-rendered caption band copied into its bottom rows. It is
/// pure: no I/O, no clock, no rendering, and the input frame (authored content, its hash, rotation and shared timeline) is never
/// modified. Without a caption the same frame is returned.
/// </summary>
public sealed class KeyboardSceneCycleOverlay
{
    public const int Width = 208;
    public const int Height = 64;
    public const int RowBytes = Width / 2;
    public const int FrameBytes = RowBytes * Height;
    public const int StripRows = 17;
    public const int StripTop = Height - StripRows;
    public const int StripBytes = RowBytes * StripRows;
    private const double TagWidth = 72;

    private KeyboardSceneCycleCaption? _caption;
    private readonly object _sync = new();
    private readonly ConditionalWeakTable<byte[], FrameRevision> _frames = new();
    private TaskCompletionSource<bool>? _transmitted;

    /// <summary>The caption shown now, or null.</summary>
    public KeyboardSceneCycleCaption? Current => Volatile.Read(ref _caption);

    internal void Show(KeyboardSceneCycleCaption? caption)
    {
        lock (_sync)
        {
            if (_caption?.Revision == caption?.Revision) return;
            _transmitted?.TrySetResult(false);
            _transmitted = caption is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _caption, caption);
        }
    }

    /// <summary>Upload-completion feedback only, never optical proof. Unknown frames and captions superseded since upload are ignored.</summary>
    public void Acknowledge(byte[] frame)
    {
        if (frame is null || !_frames.TryGetValue(frame, out var sent)) return;
        lock (_sync)
        {
            if (_caption?.Revision == sent.Revision) _transmitted?.TrySetResult(true);
        }
    }

    internal Task<bool> WaitForTransmissionAsync(long revision, CancellationToken ct)
    {
        lock (_sync)
        {
            return _caption?.Revision == revision && _caption.Strip.Length == StripBytes && _transmitted is { } pending
                ? pending.Task.WaitAsync(ct) : Task.FromResult(false);
        }
    }

    public byte[] Apply(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var caption = Volatile.Read(ref _caption);
        if (caption is null || caption.Strip.Length != StripBytes || frame.Length != FrameBytes)
        {
            return frame;
        }

        var shown = (byte[])frame.Clone();
        caption.Strip.Span.CopyTo(shown.AsSpan(StripTop * RowBytes));
        _frames.Add(shown, new FrameRevision(caption.Revision));
        return shown;
    }

    private sealed record FrameRevision(long Revision);

    /// <summary>
    /// Renders the caption band (black, a one-pixel rule, the left text and a right-aligned tag) on a short-lived STA thread, packed
    /// like the existing keyboard artwork. Called by the bridge only, never on the keyboard actor.
    /// </summary>
    internal static byte[] RenderStrip(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        byte[]? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
#pragma warning disable CA1031 // any rendering failure is returned to the caller, which then draws no caption
            try
            {
                var visual = new DrawingVisual();
                TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Width, StripRows));
                    dc.DrawLine(new Pen(Brushes.White, 1), new Point(0, 0.5), new Point(Width, 0.5));
                    var tagWidth = right.Length == 0 ? 0 : TagWidth;
                    if (tagWidth > 0)
                    {
                        dc.DrawText(Text(right, tagWidth, TextAlignment.Right), new Point(Width - 2 - tagWidth, 2));
                    }

                    dc.DrawText(Text(left, Width - 4 - tagWidth - (tagWidth > 0 ? 4 : 0), TextAlignment.Left), new Point(2, 2));
                }

                var bitmap = new RenderTargetBitmap(Width, StripRows, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var bgra = new byte[Width * StripRows * 4];
                bitmap.CopyPixels(bgra, Width * 4, 0);
                var packed = new byte[StripBytes];
                for (var p = 0; p < packed.Length; p++)
                {
                    packed[p] = (byte)(bgra[p * 8] / 17 | (bgra[p * 8 + 4] / 17) << 4);
                }

                result = packed;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
#pragma warning restore CA1031
        })
        { IsBackground = true, Name = "Keyboard scene caption" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The keyboard scene caption exceeded its rendering bound.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("The keyboard scene caption could not be rendered.", failure);
        }

        return result!;
    }

    private static FormattedText Text(string value, double width, TextAlignment alignment) => new(value, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        12, Brushes.White, 1)
    {
        MaxTextWidth = Math.Max(1, width),
        MaxLineCount = 1,
        Trimming = TextTrimming.CharacterEllipsis,
        TextAlignment = alignment,
    };
}
