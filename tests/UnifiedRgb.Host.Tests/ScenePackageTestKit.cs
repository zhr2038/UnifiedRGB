using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UnifiedRgb.Devices;
using UnifiedRgb.Screens;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Telemetry;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Host.Tests;

/// <summary>Builds scene-package ZIPs (safe and deliberately unsafe) for the OPUS-GENERIC-SCENE-PACKAGES tests. No theme content.</summary>
internal static class Pkg
{
    public const string Format = "UnifiedRgb.ScenePackage";
    public const string Contract = "rgb24-hold-physical-ms-v1";

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Frame f lights LED f % leds white over a frame-dependent dim blue base (index alignment is checkable).</summary>
    public static byte[] Rgb(int frames, int leds)
    {
        var rgb = new byte[frames * leds * 3];
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < leds; i++)
            {
                var o = ((f * leds) + i) * 3;
                if (f % leds == i)
                {
                    rgb[o] = rgb[o + 1] = rgb[o + 2] = 255;
                }
                else
                {
                    rgb[o + 2] = (byte)(f * 7 % 64);
                }
            }
        }

        return rgb;
    }

    /// <summary>A valid manifest for the given lighting/screen files (files{} is computed from <paramref name="files"/>).</summary>
    public static JsonObject Manifest(IReadOnlyDictionary<string, byte[]> files, JsonArray lighting, JsonArray? screens = null, double durationMs = 12000)
    {
        var hashes = new JsonObject();
        foreach (var (path, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            hashes[path] = Sha(data);
        }

        return new JsonObject
        {
            ["format"] = Format,
            ["schemaVersion"] = 1,
            ["id"] = "test-scene",
            ["name"] = "Test scene",
            ["revision"] = 1,
            ["durationMs"] = durationMs,
            ["loop"] = true,
            ["playbackContract"] = Contract,
            ["lighting"] = lighting,
            ["screens"] = screens ?? [],
            ["files"] = hashes,
        };
    }

    public static JsonObject LightingTrack(string role, string file, int leds, int frames, double frameMs) => new()
    {
        ["role"] = role,
        ["label"] = role,
        ["file"] = file,
        ["ledCount"] = leds,
        ["frames"] = frames,
        ["frameDurationMs"] = frameMs,
        ["pixelFormat"] = "RGB24",
        ["pixelOrder"] = "logical-layout",
    };

    public static JsonObject ScreenTrack(string role, string file, double durationMs = 12000) => new()
    {
        ["role"] = role,
        ["file"] = file,
        ["durationMs"] = durationMs,
        ["overlay"] = "compact",
        ["fit"] = "cover",
        ["fps"] = 5,
    };

    /// <summary>A valid one-role lighting package (<paramref name="edit"/> may change the manifest after the hashes are computed).</summary>
    public static byte[] Lighting(int leds = 8, int frames = 200, double frameMs = 60, Action<JsonObject>? edit = null, string role = "strip")
    {
        var files = new Dictionary<string, byte[]> { ["data/strip.rgb24"] = Rgb(frames, leds) };
        var m = Manifest(files, [LightingTrack(role, "data/strip.rgb24", leds, frames, frameMs)], durationMs: frames * frameMs);
        edit?.Invoke(m);
        return Zip([("scene.json", Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
    }

    /// <summary>A lighting + one screen package with the given screen media bytes.</summary>
    public static byte[] Mixed(byte[] media, string mediaPath = "media/panel.png", int leds = 8)
    {
        var files = new Dictionary<string, byte[]> { ["data/strip.rgb24"] = Rgb(200, leds), [mediaPath] = media };
        var m = Manifest(files, [LightingTrack("strip", "data/strip.rgb24", leds, 200, 60)], [ScreenTrack("panel", mediaPath)]);
        return Zip([("scene.json", Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
    }

    /// <summary>A screen-only package.</summary>
    public static byte[] ScreenOnly(byte[] media, string mediaPath = "media/panel.png")
    {
        var files = new Dictionary<string, byte[]> { [mediaPath] = media };
        var m = Manifest(files, [], [ScreenTrack("panel", mediaPath)]);
        return Zip([("scene.json", Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
    }

    /// <summary>A schemaVersion 2 overlay region (generic default: right half, two columns, no backing).</summary>
    public static JsonObject Region(double x = 0.50, double y = 0.16, double width = 0.48, double height = 0.74, int columns = 2, double backgroundOpacity = 0) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height,
        ["columns"] = columns,
        ["backgroundOpacity"] = backgroundOpacity,
    };

    /// <summary>
    /// A schemaVersion 2 screen-only package: dashboard overlay, accent #FAD61D and a region (<paramref name="screen"/> may change
    /// the screen track, <paramref name="edit"/> the manifest, both after the hashes are computed).
    /// </summary>
    public static byte[] RegionScreenOnly(byte[] media, Action<JsonObject>? screen = null, Action<JsonObject>? edit = null, string mediaPath = "media/panel.png")
    {
        var files = new Dictionary<string, byte[]> { [mediaPath] = media };
        var track = ScreenTrack("panel", mediaPath);
        track["overlay"] = "dashboard";
        track["accentColor"] = "#FAD61D";
        track["overlayLayout"] = Region();
        screen?.Invoke(track);
        var m = Manifest(files, [], [track]);
        m["schemaVersion"] = 2;
        edit?.Invoke(m);
        return Zip([("scene.json", Utf8(m)), .. files.Select(f => (f.Key, f.Value))]);
    }

    public static byte[] Utf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    /// <summary>Writes the entries in order; <paramref name="tweak"/> may change each created entry (e.g. symlink attributes).</summary>
    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, Action<ZipArchiveEntry>? tweak = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
                tweak?.Invoke(e);
                using var s = e.Open();
                s.Write(data);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Entries of an existing ZIP (to re-pack them with changes).</summary>
    public static List<(string Name, byte[] Data)> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e =>
        {
            using var s = e.Open();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return (e.FullName, copy.ToArray());
        }).ToList();
    }

    /// <summary>A file of the work tree found by walking up from the test output (fixtures are not copied to the output).</summary>
    public static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }

    public static byte[] GenericFixture() => File.ReadAllBytes(FindUp(Path.Combine("tests", "UnifiedRgb.Host.Tests", "Fixtures", "ScenePackages", "generic-two-role-v1.zip")));

    public static byte[] FullCaseFixture() => File.ReadAllBytes(FindUp(Path.Combine("tests", "UnifiedRgb.Host.Tests", "Fixtures", "ScenePackages", "generic-nine-five-v1.zip")));

    /// <summary>The generic fixture's 64×32 PNG (a real, importer-accepted still).</summary>
    public static byte[] Png() => Entries(GenericFixture()).Single(e => e.Name == "media/panel.png").Data;
}

/// <summary>
/// A Virtual-class (guard always clear) uploaded-loop adapter with a wire-budget probe: zone 0 = 8 LEDs, zone 1 = 16 LEDs.
/// Counts every upload/measure; never touches hardware.
/// </summary>
internal sealed class PackageLoopAdapter(string id = "pkg:1", Exception? openError = null) : ILoopUploadAdapter, ILoopBudgetProbe
{
    private readonly ControllerDescriptor _d = new(new ControllerId(id), "T", "Chain " + id, DeviceType.Cooler, DeviceClass.Virtual, id,
        [new ZoneDescriptor(new ZoneId(0), "a", ZoneLayout.Linear(8)), new ZoneDescriptor(new ZoneId(1), "b", ZoneLayout.Linear(16))],
        Capabilities.UploadedLoopOnly(4096, 1, 12288));

    private readonly ConcurrentDictionary<ZoneId, LoopUploadStats> _last = new();

    public ConcurrentQueue<(ZoneId Zone, LoopFrames Loop)> Uploaded { get; } = new();

    public int Measures => Volatile.Read(ref _measures);

    private int _measures;

    public volatile bool Fits = true;

    public ControllerId Id => _d.Id;

    public string Vendor => "T";

    public DeviceClass Class => DeviceClass.Virtual;

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public IReadOnlyList<LoopFrames> For(int zone) => [.. Uploaded.Where(u => u.Zone.Value == zone).Select(u => u.Loop)];

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_d]);

    public Task OpenAsync(ControllerId id, CancellationToken ct) => openError is { } e ? Task.FromException(e) : Task.CompletedTask;

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new InvalidOperationException("uploaded-loop only");

    public Task UploadLoopAsync(ControllerId id, ZoneId zone, LoopFrames loop, CancellationToken ct)
    {
        Uploaded.Enqueue((zone, loop));
        _last[zone] = new LoopUploadStats(DateTimeOffset.UtcNow, 1, loop.EffectIndex, loop.Frames, loop.IntervalMs, loop.Rgb.Length, loop.Rgb.Length, 1, TimeSpan.Zero, 0, "test");
        return Task.CompletedTask;
    }

    public LoopUploadStats? LastUpload(ControllerId id) => _last.Values.OrderBy(s => s.At).LastOrDefault();

    public LoopBudget MeasureLoop(ControllerId id, ZoneId zone, LoopFrames loop)
    {
        Interlocked.Increment(ref _measures);
        return Fits ? new LoopBudget(loop.Rgb.Length / 10, 12288, null) : new LoopBudget(20000, 12288, "compressed 20000 B exceeds the 12288 B budget");
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    public Task CloseAsync(ControllerId id) => Task.CompletedTask;
}

internal sealed class PackageAdapterFactory(IDeviceAdapter adapter) : IDeviceAdapterFactory
{
    public DeviceClass Class => adapter.Class;

    public IDeviceAdapter Create(IServiceProvider sp) => adapter;
}

/// <summary>A screen platform with present fake screens whose USB open is counted and refused (output stays disabled anyway).</summary>
internal sealed class FakeScreens : IScreenDeviceEnumerator, IScreenUsbOpener, IScreenContentionProbe, ICpuMemoryReader
{
    private const string Guid = "{88bae032-5a81-49f0-bc3d-a4ff138216d6}";
    private int _opens;

    public static ScreenDeviceInfo Tl { get; } = ScreenDeviceInfo.FromPath($@"\\?\usb#vid_1cbe&pid_0006#a4396dc0631e0a66#{Guid}")!;

    public static ScreenDeviceInfo H2 { get; } = ScreenDeviceInfo.FromPath($@"\\?\usb#vid_1cbe&pid_a021#1d3343b614a38505#{Guid}")!;

    /// <summary>A synthetic rectangular case screen (enumerated only when <see cref="CasePresent"/>).</summary>
    public static ScreenDeviceInfo Case { get; } = ScreenDeviceInfo.FromPath($@"\\?\usb#vid_1cbe&pid_0088#00000000000c0001#{Guid}")!;

    /// <summary>True: the rectangular case screen is present too (default false keeps the historical TL+H2 platform).</summary>
    public bool CasePresent { get; init; }

    public int Opens => Volatile.Read(ref _opens);

    public ScreenPlatform Platform => new(this, this, this, this, null, TimeProvider.System);

    /// <summary>False: the TL screen is unplugged (not enumerated); a screen scene still naming it reports it Disconnected.</summary>
    public volatile bool TlPresent = true;

    public IReadOnlyList<ScreenDeviceInfo> Enumerate() => (TlPresent ? [Tl, H2] : new[] { H2 }).Concat(CasePresent ? [Case] : []).ToArray();

    /// <summary>False (default): every open is refused. True: an in-memory sink accepts writes (never a real device).</summary>
    public bool AcceptOpens { get; init; }

    private long _writes;

    /// <summary>Bytes the in-memory sink accepted.</summary>
    public long SinkWrites => Interlocked.Read(ref _writes);

    public IScreenUsbChannel Open(ScreenDeviceInfo device)
    {
        Interlocked.Increment(ref _opens);
        return AcceptOpens ? new Sink(this) : throw new ScreenTransferException("tests never open USB");
    }

    private sealed class Sink(FakeScreens owner) : IScreenUsbChannel
    {
        public byte OutEndpoint => 0x01;

        public byte InEndpoint => 0x81;

        public void Write(ReadOnlySpan<byte> data, int timeoutMs) => Interlocked.Add(ref owner._writes, data.Length);

        public int Read(Span<byte> buffer, int timeoutMs) => 0;

        public void Abort()
        {
        }

        public void Dispose()
        {
        }
    }

    public string? BlockedBy(ScreenFamily family) => null;

    public CpuTimes? ReadCpuTimes() => null;

    public MemoryStatus? ReadMemory() => null;
}
