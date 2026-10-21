using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnifiedRgb.Screens.Media;

/// <summary>
/// WIC through WPF, with the built-in Microsoft codecs named explicitly (never <c>BitmapDecoder.Create</c>, which would consult
/// any installed third-party codec). Every member must run on an STA thread (<see cref="StaThread"/>, display workers).
/// </summary>
internal static class WpfCodec
{
    private const BitmapCreateOptions Create = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;

    public static BitmapDecoder Decoder(string format, Stream stream) => format switch
    {
        "jpeg" => new JpegBitmapDecoder(stream, Create, BitmapCacheOption.None),
        "png" => new PngBitmapDecoder(stream, Create, BitmapCacheOption.None),
        "gif" => new GifBitmapDecoder(stream, Create, BitmapCacheOption.None),
        _ => throw new ArgumentException($"unsupported format '{format}'", nameof(format)),
    };

    /// <summary>"jpeg", "png" or "gif" by magic bytes; null otherwise.</summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (Protocol.ScreenProtocol.IsJpeg(data))
        {
            return "jpeg";
        }

        if (Protocol.ScreenProtocol.IsPng(data))
        {
            return "png";
        }

        return data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8) ? "gif" : null;
    }

    /// <summary>Converts any decoded frame to premultiplied BGRA bytes.</summary>
    public static PixelFrame ToPixels(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var frame = new PixelFrame(converted.PixelWidth, converted.PixelHeight);
        converted.CopyPixels(frame.Pixels, frame.Stride, 0);
        return frame;
    }

    /// <summary>JPEG of an opaque frame (alpha ignored), baseline, at <paramref name="quality"/> 1..100.</summary>
    public static byte[] EncodeJpeg(PixelFrame frame, int quality)
    {
        var src = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>32-bit RGBA PNG (colour type 6, as the TURZX reference sends); premultiplied input is un-premultiplied by WIC.</summary>
    public static byte[] EncodePng(PixelFrame frame)
    {
        BitmapSource src = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null, frame.Pixels, frame.Stride);
        src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Decodes one stored PNG frame, refusing anything larger than <paramref name="maxPixels"/>.</summary>
    public static PixelFrame DecodePng(byte[] png, long maxPixels)
    {
        using var ms = new MemoryStream(png, writable: false);
        var dec = Decoder("png", ms);
        var f = dec.Frames[0];
        if ((long)f.PixelWidth * f.PixelHeight > maxPixels)
        {
            throw new InvalidDataException($"stored frame {f.PixelWidth}x{f.PixelHeight} exceeds {maxPixels} pixels");
        }

        return ToPixels(f);
    }

    /// <summary>Reads an integer metadata query, or <paramref name="fallback"/> when it is absent or unreadable.</summary>
    public static int Query(BitmapMetadata? metadata, string query, int fallback)
    {
        if (metadata is null)
        {
            return fallback;
        }

        try
        {
            var v = metadata.GetQuery(query);
            return v is null ? fallback : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException
            or InvalidCastException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    /// <summary>True for the exception types WIC/WPF raise on malformed or unsupported image data.</summary>
    public static bool IsDecodeFailure(Exception ex) =>
        ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or COMException
            or OverflowException or IOException or InvalidDataException or ExternalException;
}

/// <summary>A dedicated STA thread with a work queue (previews, uploads). Display workers have their own STA threads.</summary>
internal sealed class StaThread : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private int _disposed;
    private int _queueDisposed;

    public StaThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    tcs.TrySetResult(work());
                }
#pragma warning disable CA1031 // the caller observes the exception through the task
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    tcs.TrySetException(ex);
                }
            }, CancellationToken.None); // unbounded queue: Add never blocks; a cancelled item is skipped when it runs
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(StaThread));
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(10))) DisposeQueue();
    }

    /// <summary>Releases the WPF dispatcher a thread acquired by touching imaging/visual objects.</summary>
    public static void ShutdownDispatcher() =>
        System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();

    private void Run()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }
        finally
        {
            ShutdownDispatcher();
            if (Volatile.Read(ref _disposed) != 0) DisposeQueue(); // also after a delayed decoder finally exits
        }
    }

    private void DisposeQueue() { if (Interlocked.Exchange(ref _queueDisposed, 1) == 0) _queue.Dispose(); }
}
