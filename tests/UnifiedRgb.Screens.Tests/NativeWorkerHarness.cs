using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// One TURZX <see cref="ScreenWorker"/> driven directly (no manager) on a frozen <see cref="ManualTime"/>: the test controls the native
/// trial, the telemetry and every clock step, so each 100 ms step admits exactly one picture. Fake channels only; nothing is opened.
/// </summary>
internal sealed class NativeWorkerHarness : IDisposable
{
    public const int Fps = 10;
    public const int PeriodMs = 100;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
    private NativeVideoTrial? _trial;
    private ScreenRateTrial? _rate;
    private PcTelemetryDto? _telemetry;
    private int _aborts;

    private NativeWorkerHarness(Func<byte, uint, int, byte[]?>? reply)
    {
        Reply = reply;
        Opener.Configure = c => c.Reply = packet =>
        {
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
            var stamp = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (Reply?.Invoke(header[0], stamp, c.Writes.Count) is { } custom) return custom;
            return Ack(header[0], stamp);
        };
        Directory.CreateDirectory(_root);
        Assets = new AssetStore(Path.Combine(_root, "assets"), 1L << 30, 64, NullLogger.Instance);
    }

    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    public FakeOpener Opener { get; } = new();
    public AssetStore Assets { get; }
    public ScreenWorker Worker { get; private set; } = null!;
    public ScreenAssignmentDto Assignment { get; private set; } = null!;
    public StoredAsset Asset { get; private set; } = null!;

    /// <summary>(command, stamp, writes so far) → a custom reply, or null for a normal 512-byte ACK with depth 0.</summary>
    public Func<byte, uint, int, byte[]?>? Reply { get; set; }

    public NativeVideoTrial? Trial { get => Volatile.Read(ref _trial); set => Volatile.Write(ref _trial, value); }
    public ScreenRateTrial? RateTrial { get => Volatile.Read(ref _rate); set => Volatile.Write(ref _rate, value); }
    public int Aborts => Volatile.Read(ref _aborts);

    public static NativeVideoTrial NewTrial(string id, Func<NativeVideoTrialDto, NativeVideoTrialDto>? with = null)
    {
        var info = new NativeVideoTrialDto(id, Devices.Turzx.Id, 60000, Fps, null) { Quality = 95 };
        return new(with is null ? info : with(info), long.MaxValue);
    }

    public static async Task<NativeWorkerHarness> StartAsync(NativeVideoTrial? trial, string overlay = "dashboard",
        Func<ScreenWorkerContext, ScreenWorkerContext>? customize = null, Func<byte, uint, int, byte[]?>? reply = null)
    {
        var h = new NativeWorkerHarness(reply);
        try
        {
            var frames = Enumerable.Range(0, 4).Select(k => GifFrame.Fill(0, 0, 2, 2, (byte)(k + 1), delayCs: 10)).ToArray();
            var gif = Images.Gif(2, 2, frames);
            var import = await Sta.RunAsync(() => ImageImporter.Import(gif, ImportLimits.Default));
            h.Asset = h.Assets.Commit(AssetStore.IdOf(gif), "native harness", import);
            h.Trial = trial;
            h.SetTelemetry(10);
            h.Assignment = new ScreenAssignmentDto(Devices.Turzx.Id, h.Asset.Id, Overlay: overlay, Fps: Fps);
            var options = new ScreensOptions { BackoffInitialMs = 20, BackoffMaxMs = 80, DiscoveryIntervalMs = 250, NvmlTelemetry = false };
            Rig.AllOutput(options);
            var ctx = new ScreenWorkerContext(options, h.Opener, new FakeContention(), h.Assets, () => Volatile.Read(ref h._telemetry), h.Time,
                NullLogger.Instance)
            {
                EncodingOf = _ => h.Trial is not null ? new ScreenEncodingSelection(ScreenEncodingPolicies.NativeH264IntraV1, false) : ScreenEncodingSelection.Legacy,
                VideoTrialOf = _ => h.Trial,
                NativeVideoAllowed = _ => h.Trial is not null,
                TrialOf = _ => h.RateTrial,
                AbortTrial = _ => { Interlocked.Increment(ref h._aborts); h.Trial = null; h.RateTrial = null; },
            };
            if (customize is not null) ctx = customize(ctx);
            h.Worker = new ScreenWorker(Devices.Turzx, ctx, new WorkerCommand(h.Assignment, false));
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    public void SetTelemetry(double cpu) => Volatile.Write(ref _telemetry,
        new PcTelemetryDto(Time.GetUtcNow(), cpu, 8L << 30, 32L << 30, 50, null, 2L << 30, 8L << 30, "harness", null));

    public FakeChannel Channel => Opener.Last;
    public byte[][] Writes => Opener.Channels.SelectMany(c => c.Writes).ToArray();
    public int Count(byte command) => Writes.Count(p => Command(p) == command);
    public int DataWrites => Count(TurzxVideoProtocol.DataCommand);

    public static byte Command(byte[] packet) => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512))[0];

    public static uint Stamp(byte[] packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512)).AsSpan(4));

    public static byte[] Ack(byte command, uint stamp, byte depth = 0)
    {
        var reply = new byte[512]; reply[0] = command; reply[1] = 200; reply[8] = depth;
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(2), stamp);
        return reply;
    }

    /// <summary>One wire line: command, packet length, SHA-256 of the decrypted header with its request stamp zeroed, SHA-256 of the payload.</summary>
    public static string Line(byte[] packet)
    {
        var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
        header.AsSpan(4, 4).Clear();
        return $"{header[0]} {packet.Length} {Convert.ToHexString(SHA256.HashData(header))[..16]} {Convert.ToHexString(SHA256.HashData(packet.AsSpan(512)))[..16]}";
    }

    /// <summary>Waits for <paramref name="condition"/>, then 250 ms more: with the clock frozen nothing else can be admitted.</summary>
    public static async Task SettleAsync(Func<bool> condition, string what)
    {
        await Wait.UntilAsync(condition, what);
        await Task.Delay(250);
    }

    /// <summary>Advances one period and waits for exactly one more native access unit.</summary>
    public async Task StepAsync(int ms = PeriodMs)
    {
        var before = DataWrites;
        Time.Advance(TimeSpan.FromMilliseconds(ms));
        await SettleAsync(() => DataWrites > before, "one more access unit");
        DataWrites.Should().Be(before + 1, "a frozen clock admits exactly one picture per period");
    }

    /// <summary>Posts a new assignment: the worker resets pacing and sends one picture at once.</summary>
    public async Task PostAsync(ScreenAssignmentDto assignment)
    {
        var before = DataWrites;
        Assignment = assignment;
        Worker.Post(assignment, false);
        await SettleAsync(() => DataWrites > before, "one access unit after the new assignment");
        DataWrites.Should().Be(before + 1);
    }

    public void Dispose()
    {
        Worker?.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
