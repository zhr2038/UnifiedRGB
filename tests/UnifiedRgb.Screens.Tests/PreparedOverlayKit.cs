using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>Shared helpers of the S1b worker tests (frozen-clock <see cref="NativeWorkerHarness"/>, fake channels only).</summary>
internal static class PreparedOverlayKit
{
    public static NativeVideoTrial Prepared(string id, bool measure = false, bool simple = false) =>
        NativeWorkerHarness.NewTrial(id, t => t with { PreparedOverlay = true, MeasureQueueAfterOverlay = measure, SimpleOverlayPng = simple });

    public static Task<NativeWorkerHarness> StartAsync(NativeVideoTrial trial, OverlayEncoder? encoder, string overlay = "dashboard",
        Func<ScreenWorkerContext, ScreenWorkerContext>? customize = null, Func<byte, uint, int, byte[]?>? reply = null) =>
        NativeWorkerHarness.StartAsync(trial, overlay, c =>
        {
            if (encoder is not null) c = c with { PrepareOverlayForTest = encoder.Encode };
            return customize is null ? c : customize(c);
        }, reply);

    public static int[] Commands(IEnumerable<byte[]> writes) => [.. writes.Select(p => (int)NativeWorkerHarness.Command(p))];

    /// <summary>Worker-issued overlay PNG payloads: every 102 inside a native session except Begin's own transparent one.</summary>
    public static List<byte[]> WorkerPngs(IEnumerable<byte[]> writes)
    {
        var pngs = new List<byte[]>();
        bool inSession = false, beginPng = false;
        foreach (var packet in writes)
        {
            switch (NativeWorkerHarness.Command(packet))
            {
                case TurzxVideoProtocol.DataCommand when !inSession:
                    throw new InvalidOperationException("native data outside a session");
                case 41: beginPng = true; break;
                case 15: inSession = true; break;
                case 123: inSession = false; break;
                case ScreenProtocol.PushPngCommand when beginPng: beginPng = false; break;
                case ScreenProtocol.PushPngCommand when inSession: pngs.Add(packet[512..]); break;
            }
        }

        return pngs;
    }

    /// <summary>Begin's own transparent PNG payload (the first 102 after a 41).</summary>
    public static byte[] BeginPng(byte[][] writes)
    {
        var start = Array.FindIndex(writes, p => NativeWorkerHarness.Command(p) == 41);
        return writes.Skip(start).First(p => NativeWorkerHarness.Command(p) == ScreenProtocol.PushPngCommand)[512..];
    }

    public static NativeVideoPerformanceDto Facts(NativeWorkerHarness h) => h.Worker.Performance.NativeVideo
        ?? throw new InvalidOperationException("no native session");

    public static NativeVideoPreparedOverlayDto PreparedFacts(NativeWorkerHarness h) => Facts(h).PreparedOverlay
        ?? throw new InvalidOperationException("no prepared-overlay facts");

    public static TurzxOverlayPreparer Preparer(NativeWorkerHarness h) => h.Worker.OverlayPreparerForTest
        ?? throw new InvalidOperationException("no preparer");

    /// <summary>Advances one period and waits until that one picture (its access unit AND its overlay step) has completed.</summary>
    public static async Task PictureAsync(NativeWorkerHarness h)
    {
        var data = h.DataWrites;
        var units = h.Worker.Performance.NativeVideo?.AccessUnits ?? 0;
        h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
        await Wait.UntilAsync(() => h.Worker.Performance.NativeVideo is { } v && v.AccessUnits > units, "one more native picture");
        h.DataWrites.Should().Be(data + 1, "a frozen clock admits exactly one picture per period");
    }

    /// <summary>Waits until the preparer has neither an in-flight nor a pending request (its result, if any, is held).</summary>
    public static Task IdleAsync(NativeWorkerHarness h) =>
        Wait.UntilAsync(() => h.Worker.OverlayPreparerForTest?.Snapshot() is { InFlight: false, Pending: false }, "the preparer finished");

    /// <summary>A native trial replaces the current one (or starts): the worker's pacing reset admits one picture at once.</summary>
    public static async Task SwitchTrialAsync(NativeWorkerHarness h, NativeVideoTrial trial)
    {
        var data = h.DataWrites;
        h.Trial = trial;
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data && h.Worker.Performance.NativeVideo is not null, "the new trial's first picture");
        h.DataWrites.Should().Be(data + 1);
    }

    /// <summary>The native trial disappears without a command (expiry); the stop controls and then the normal image path follow.</summary>
    public static async Task<int[]> EndTrialAsync(NativeWorkerHarness h, bool post = false)
    {
        var before = h.Writes.Length;
        h.Trial = null;
        if (post) h.Worker.Post(h.Assignment, false); // the manager's cancel path also posts the assignment
        await NativeWorkerHarness.SettleAsync(() => h.Writes is { } all && all.Length > before + 3
            && NativeWorkerHarness.Command(all[before + 3]) == ScreenProtocol.PushPngCommand, "stop controls, then the normal image path");
        return Commands(h.Writes.Skip(before).Take(4));
    }

    /// <summary>The worker's exact inline bytes for one request's layer (OverlayCanvas + EncodeOverlayPng), on an STA.</summary>
    public static Task<byte[]> ExactAsync(OverlayPreparationRequest r) =>
        Sta.RunAsync(() => ScreenWorker.PrepareOverlayPng(r.Layer, r.Assignment, r.InternalRotation, r.SimplePng).Bytes);

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

/// <summary>
/// The injected preparer encoder: the worker's exact operations unless told to block (until released) or to fail. Records every
/// request and the layer's pixel hash at encode time.
/// </summary>
internal sealed class OverlayEncoder : IDisposable
{
    private readonly SemaphoreSlim _release = new(0);
    private int _calls, _gated, _fail;

    public bool Gated { get => Volatile.Read(ref _gated) != 0; set => Volatile.Write(ref _gated, value ? 1 : 0); }

    public bool Fail { get => Volatile.Read(ref _fail) != 0; set => Volatile.Write(ref _fail, value ? 1 : 0); }

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<(OverlayPreparationRequest Request, string LayerSha)> Seen { get; } = new();

    public OverlayPreparationRequest Last => Seen.Last().Request;

    public (byte[] Bytes, string Note) Encode(OverlayPreparationRequest request)
    {
        Seen.Enqueue((request, PreparedOverlayKit.Sha(request.Layer.Pixels)));
        Interlocked.Increment(ref _calls);
        if (Gated && !_release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("the test never released the encoder");
        if (Fail) throw new InvalidOperationException("fake WIC failure");
        return ScreenWorker.PrepareOverlayPng(request.Layer, request.Assignment, request.InternalRotation, request.SimplePng);
    }

    public Task WaitCallsAsync(int count) => Wait.UntilAsync(() => Calls >= count, $"{count} encoder call(s)");

    public void Release(int count = 1) => _release.Release(count);

    public void Dispose()
    {
        Gated = false;
        _release.Release(64); // never disposed: an abandoned encode may still be waiting on it
    }
}

internal sealed class RecordingLogger : ILogger
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(logLevel + " " + formatter(state, exception));
}

/// <summary>The theory form of <see cref="MediaFoundationFactAttribute"/> (same component check).</summary>
internal sealed class MediaFoundationTheoryAttribute : TheoryAttribute
{
    public MediaFoundationTheoryAttribute()
    {
        if (new MediaFoundationFactAttribute().Skip is { } skip) Skip = skip;
    }
}
