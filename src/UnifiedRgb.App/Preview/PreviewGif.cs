using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnifiedRgb.App.Preview;

/// <summary>Bounded local GIF compositor, including restore/clear disposal, off the UI dispatcher.</summary>
internal sealed record PreviewGif(BitmapSource[] Frames, int[] Delays)
{
    public BitmapSource At(double milliseconds)
    {
        var t = milliseconds % Math.Max(1, Delays.Sum());
        for (var i = 0; i < Frames.Length; i++) { if (t < Delays[i]) return Frames[i]; t -= Delays[i]; }
        return Frames[^1];
    }
    public static Task<PreviewGif> ReadAsync(string path, CancellationToken ct)
    {
        var result = new TaskCompletionSource<PreviewGif>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Read(path, ct)); }
#pragma warning disable CA1031 // propagate decoder errors to the owning preview
            catch (Exception ex) { result.SetException(ex); }
#pragma warning restore CA1031
        }) { IsBackground = true, Name = "GIF preview decoder" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    private static PreviewGif Read(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var n = decoder.Frames.Count;
        if (n is < 1 or > 300) throw new InvalidDataException("GIF 预演最多 300 帧。");
        var rects = decoder.Frames.Select(f => (X: Query(f.Metadata as BitmapMetadata, "/imgdesc/Left"), Y: Query(f.Metadata as BitmapMetadata, "/imgdesc/Top"), W: f.PixelWidth, H: f.PixelHeight,
            Delay: Query(f.Metadata as BitmapMetadata, "/grctlext/Delay") * 10, Disposal: Query(f.Metadata as BitmapMetadata, "/grctlext/Disposal"))).ToArray();
        var width = Math.Max(Query(decoder.Metadata, "/logscrdesc/Width"), rects.Max(r => r.X + r.W));
        var height = Math.Max(Query(decoder.Metadata, "/logscrdesc/Height"), rects.Max(r => r.Y + r.H));
        if (width < 1 || height < 1 || (long)width * height > 40_000_000 || (long)width * height * n > 400_000_000) throw new InvalidDataException("GIF 预演超出解码预算。");
        var canvas = new byte[checked(width * height * 4)]; var frames = new BitmapSource[n]; var delays = new int[n];
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested(); var r = rects[i]; var saved = r.Disposal == 3 ? (byte[])canvas.Clone() : null;
            var source = new FormatConvertedBitmap(decoder.Frames[i], PixelFormats.Pbgra32, null, 0);
            var pixels = new byte[checked(r.W * r.H * 4)]; source.CopyPixels(pixels, r.W * 4, 0);
            for (var y = 0; y < r.H; y++) for (var x = 0; x < r.W; x++)
            {
                var s = (y * r.W + x) * 4; if (pixels[s + 3] != 0) Buffer.BlockCopy(pixels, s, canvas, ((y + r.Y) * width + x + r.X) * 4, 4);
            }
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, canvas, width * 4);
            var scale = Math.Min(1, 180.0 / Math.Max(width, height));
            if (scale < 1) bitmap = new WriteableBitmap(new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)));
            bitmap.Freeze(); frames[i] = bitmap; delays[i] = r.Delay < 20 ? 100 : r.Delay;
            if (r.Disposal == 2) for (var y = r.Y; y < r.Y + r.H; y++) Array.Clear(canvas, (y * width + r.X) * 4, r.W * 4);
            else if (saved is not null) canvas = saved;
        }
        return new(frames, delays);
    }
    private static int Query(BitmapMetadata? m, string q) => m?.ContainsQuery(q) == true ? Convert.ToInt32(m.GetQuery(q), System.Globalization.CultureInfo.InvariantCulture) : 0;
}
