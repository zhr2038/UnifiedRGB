using System.Collections.Concurrent;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Telemetry;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

/// <summary>The five screens as found in the registry on 2026-10-18 (paths built from the read-only discovery record).</summary>
internal static class Devices
{
    public const string Guid = "{88bae032-5a81-49f0-bc3d-a4ff138216d6}";

    public static string PathOf(string pid, string instance) => $@"\\?\usb#vid_1cbe&pid_{pid}#{instance}#{Guid}";

    public static ScreenDeviceInfo Tl1 { get; } = ScreenDeviceInfo.FromPath(PathOf("0006", "a4396dc0631e0a66"))!;

    public static ScreenDeviceInfo Tl2 { get; } = ScreenDeviceInfo.FromPath(PathOf("0006", "da712ac3931e0b66"))!;

    public static ScreenDeviceInfo Tl3 { get; } = ScreenDeviceInfo.FromPath(PathOf("0006", "e1f26a43041e1366"))!;

    public static ScreenDeviceInfo H2 { get; } = ScreenDeviceInfo.FromPath(PathOf("a021", "1d3343b614a38505"))!;

    public static ScreenDeviceInfo Turzx { get; } = ScreenDeviceInfo.FromPath(PathOf("0088", "6f2c527df4cac503"))!;
}

internal sealed class FakeEnumerator : IScreenDeviceEnumerator
{
    private readonly object _gate = new();
    private List<ScreenDeviceInfo> _devices;

    public FakeEnumerator(params ScreenDeviceInfo[] devices) => _devices = [.. devices];

    public int Calls { get; private set; }

    public void Set(params ScreenDeviceInfo[] devices)
    {
        lock (_gate)
        {
            _devices = [.. devices];
        }
    }

    public IReadOnlyList<ScreenDeviceInfo> Enumerate()
    {
        lock (_gate)
        {
            Calls++;
            return [.. _devices];
        }
    }
}

internal sealed class FakeChannel : IScreenUsbChannel
{
    private readonly ManualResetEventSlim _aborted = new();

    public ConcurrentQueue<byte[]> Writes { get; } = new();

    /// <summary>Write number (1-based) → exception to throw instead of accepting it.</summary>
    public Func<int, Exception?>? FailWrite { get; set; }

    /// <summary>When set, a write blocks until <see cref="Abort"/> and then fails as aborted (a pending bulk transfer).</summary>
    public bool BlockWrites { get; set; }

    public int WriteAttempts;
    public int Reads;
    public int Aborts;
    public volatile bool Disposed;

    public byte OutEndpoint => 0x01;

    public byte InEndpoint => 0x81;

    public void Write(ReadOnlySpan<byte> data, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        var n = Interlocked.Increment(ref WriteAttempts);
        if (BlockWrites)
        {
            _aborted.Wait(TimeSpan.FromSeconds(30));
            throw new ScreenTransferException(ScreenTransferError.Aborted, "aborted", 995);
        }

        if (FailWrite?.Invoke(n) is { } ex)
        {
            throw ex;
        }

        Writes.Enqueue(data.ToArray());
    }

    /// <summary>When set, each response read throws this instead of returning 0 bytes.</summary>
    public Func<Exception?>? FailRead { get; set; }
    public Func<byte[], byte[]>? Reply { get; set; }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        Interlocked.Increment(ref Reads);
        if (FailRead?.Invoke() is { } ex)
        {
            throw ex;
        }

        if (Reply is not null && Writes.TryPeek(out _))
        {
            var reply = Reply(Writes.Last()); reply.CopyTo(buffer); return reply.Length;
        }

        return 0;
    }

    public void Abort()
    {
        Interlocked.Increment(ref Aborts);
        _aborted.Set();
    }

    public void Dispose()
    {
        Disposed = true;
        _aborted.Set();
    }
}

internal sealed class FakeOpener : IScreenUsbOpener
{
    public ConcurrentQueue<FakeChannel> Channels { get; } = new();

    /// <summary>Open number (1-based) → exception instead of a channel.</summary>
    public Func<int, Exception?>? FailOpen { get; set; }

    public Action<FakeChannel>? Configure { get; set; }

    private int _attempts;
    private int _opens;

    /// <summary>Completed open attempts (failed ones included); a successful channel is already in <see cref="Channels"/>.</summary>
    public int Opens => Volatile.Read(ref _opens);

    public IScreenUsbChannel Open(ScreenDeviceInfo device)
    {
        var n = Interlocked.Increment(ref _attempts);
        if (FailOpen?.Invoke(n) is { } ex)
        {
            Interlocked.Increment(ref _opens);
            throw ex;
        }

        var c = new FakeChannel();
        Configure?.Invoke(c);
        Channels.Enqueue(c);
        Opened.Enqueue((device.Id, c));
        Interlocked.Increment(ref _opens); // published after the enqueue, so "Opens == n" implies Last is the n-th channel
        return c;
    }

    public FakeChannel Last => Channels.Last();

    /// <summary>Every successful open with its screen id, in order.</summary>
    public ConcurrentQueue<(string Id, FakeChannel Channel)> Opened { get; } = new();

    /// <summary>Channels opened for <paramref name="id"/>, oldest first.</summary>
    public FakeChannel[] For(string id) => [.. Opened.Where(o => o.Id == id).Select(o => o.Channel)];
}

internal sealed class FakeContention : IScreenContentionProbe
{
    public volatile string? TurzxOwner;
    public volatile string? LianLiOwner;

    public string? BlockedBy(ScreenFamily family) => family == ScreenFamily.Turzx ? TurzxOwner : LianLiOwner;
}

internal sealed class FakeOs : ICpuMemoryReader
{
    private ulong _tick;

    public bool Fail { get; set; }

    /// <summary>Each read advances 1000 units of total time of which 750 are idle: 25 % busy.</summary>
    public CpuTimes? ReadCpuTimes()
    {
        if (Fail)
        {
            return null;
        }

        var t = Interlocked.Increment(ref _tick);
        return new CpuTimes(t * 750, t * 800, t * 200);
    }

    public MemoryStatus? ReadMemory() => Fail ? null : new MemoryStatus(32L << 30, 20L << 30);
}

internal sealed class FakeGpu : IGpuReader
{
    public GpuSample Next { get; set; } = new(42, 61, 8L << 30, 32L << 30, null);

    public bool Disposed { get; private set; }

    public string Name => "FakeGPU";

    public GpuSample Read() => Next;

    public void Dispose() => Disposed = true;
}

/// <summary>A controllable clock (timers still run on real time).</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private long _ticks;

    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        Now += by;
        Interlocked.Add(ref _ticks, by.Ticks);
    }
}

internal sealed class Rig : IAsyncDisposable
{
    public Rig(Action<ScreensOptions>? configure = null, params ScreenDeviceInfo[] devices)
        : this(null, configure, devices)
    {
    }

    public Rig(IFanLcdGroupRecovery? lcdRecovery, Action<ScreensOptions>? configure, params ScreenDeviceInfo[] devices)
    {
        Options = new ScreensOptions { BackoffInitialMs = 20, BackoffMaxMs = 80, DiscoveryIntervalMs = 250, NvmlTelemetry = false };
        configure?.Invoke(Options);
        Enumerator = new FakeEnumerator(devices);
        DataDirectory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", System.Guid.NewGuid().ToString("N"));
        Platform = new ScreenPlatform(Enumerator, Opener, Contention, Os, null, TimeProvider.System);
        Manager = new ScreenManager(Options, DataDirectory, Platform, lcdRecovery: lcdRecovery);
    }

    public ScreensOptions Options { get; }

    public FakeEnumerator Enumerator { get; }

    public FakeOpener Opener { get; } = new();

    public FakeContention Contention { get; } = new();

    public FakeOs Os { get; } = new();

    public ScreenPlatform Platform { get; }

    public string DataDirectory { get; }

    public ScreenManager Manager { get; }

    public static Action<ScreensOptions> AllOutput => o =>
    {
        o.Enabled = true;
        o.TlLcd.Output = true;
        o.H2.Output = true;
        o.Turzx.Output = true;
    };

    public async Task<ScreenDeviceDto> ScreenAsync(string id) => (await Manager.ListScreensAsync()).Single(s => s.Id == id);

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

internal static class Wait
{
    /// <summary>Observes the outcome of a task the calling test already started (for FluentActions.Awaiting), within 30 s.</summary>
#pragma warning disable VSTHRD003 // the test started this task; awaiting it here only observes its outcome
    public static Func<Task> Outcome(Task started) => () => started.WaitAsync(TimeSpan.FromSeconds(30));
#pragma warning restore VSTHRD003

    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }
}

internal static class Sta
{
    public static async Task<T> RunAsync<T>(Func<T> work)
    {
        using var t = new StaThread("test sta");
        return await t.InvokeAsync(work, CancellationToken.None);
    }
}

/// <summary>Tiny image builders for tests (STA where WIC is involved).</summary>
internal static class Images
{
    public static PixelFrame Solid(int w, int h, byte r, byte g, byte b) => PixelFrame.Solid(w, h, r, g, b);

    public static PixelFrame Split(int w, int h)
    {
        // left half red, right half blue
        var f = new PixelFrame(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = ((y * w) + x) * 4;
                f.Pixels[i] = (byte)(x < w / 2 ? 0 : 255);
                f.Pixels[i + 2] = (byte)(x < w / 2 ? 255 : 0);
                f.Pixels[i + 3] = 255;
            }
        }

        return f;
    }

    public static Task<byte[]> PngAsync(PixelFrame f) => Sta.RunAsync(() => WpfCodec.EncodePng(f));

    public static Task<byte[]> JpegAsync(PixelFrame f) => Sta.RunAsync(() => WpfCodec.EncodeJpeg(f, 90));

    public static ScreenSceneDto Scene(params ScreenAssignmentDto[] a) => new(a);

    /// <summary>
    /// A GIF89a with a 4-colour global table (0 black, 1 red, 2 green, 3 blue). Pixel data uses LZW with a clear code after
    /// every two codes, so the code width never grows (the "uncompressed GIF" technique) — easy to write, valid to decode.
    /// </summary>
    public static byte[] Gif(int width, int height, params GifFrame[] frames)
    {
        var o = new List<byte>();
        o.AddRange("GIF89a"u8.ToArray());
        o.AddRange(U16(width));
        o.AddRange(U16(height));
        o.Add(0x91); // global table, 2-bit colour resolution, 4 entries
        o.Add(0);
        o.Add(0);
        o.AddRange(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });
        foreach (var f in frames)
        {
            o.AddRange(new byte[] { 0x21, 0xF9, 0x04, (byte)((f.Disposal << 2) | (f.Transparent is null ? 0 : 1)) });
            o.AddRange(U16(f.DelayCs));
            o.Add((byte)(f.Transparent ?? 0));
            o.Add(0);
            o.Add(0x2C);
            o.AddRange(U16(f.X));
            o.AddRange(U16(f.Y));
            o.AddRange(U16(f.W));
            o.AddRange(U16(f.H));
            o.Add(0);
            o.Add(2); // LZW minimum code size
            var data = Lzw(f.Indices);
            for (var i = 0; i < data.Count; i += 255)
            {
                var n = Math.Min(255, data.Count - i);
                o.Add((byte)n);
                o.AddRange(data.GetRange(i, n));
            }

            o.Add(0);
        }

        o.Add(0x3B);
        return [.. o];
    }

    private static List<byte> Lzw(byte[] indices)
    {
        const int Clear = 4;
        const int End = 5;
        var codes = new List<int> { Clear };
        for (var i = 0; i < indices.Length; i++)
        {
            if (i > 0 && i % 2 == 0)
            {
                codes.Add(Clear);
            }

            codes.Add(indices[i]);
        }

        codes.Add(End);
        var bytes = new List<byte>();
        int acc = 0, bits = 0;
        foreach (var c in codes)
        {
            acc |= c << bits;
            bits += 3;
            while (bits >= 8)
            {
                bytes.Add((byte)acc);
                acc >>= 8;
                bits -= 8;
            }
        }

        if (bits > 0)
        {
            bytes.Add((byte)acc);
        }

        return bytes;
    }

    private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];
}

internal sealed record GifFrame(int X, int Y, int W, int H, byte[] Indices, int DelayCs = 10, int Disposal = 1, int? Transparent = null)
{
    public static GifFrame Fill(int x, int y, int w, int h, byte index, int delayCs = 10, int disposal = 1) =>
        new(x, y, w, h, Enumerable.Repeat(index, w * h).ToArray(), delayCs, disposal);
}
