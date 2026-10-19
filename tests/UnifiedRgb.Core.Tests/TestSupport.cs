using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Layout;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Tests;

internal static class TestSupport
{
    public const uint Seed = 12345;
    public const double Fps = 60.0;

    public static readonly (string Name, ZoneLayout Layout)[] Layouts =
    [
        ("Linear20", ZoneLayout.Linear(20)),
        ("Ring16", ZoneLayout.Ring(16)),
        ("Matrix4x4", ZoneLayout.Matrix(4, 4)),
        ("Point", ZoneLayout.Point),
    ];

    public static readonly int[] GoldenFrames = [0, 30, 120];

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public static Rgb[] Render(IEffectGenerator gen, ReadOnlySpan<Sample> samples, double t, uint seed = Seed, float speed = 0.5f)
    {
        var colors = gen.Schema.DefaultColors.ToArray();
        var output = new Rgb[samples.Length];
        var ctx = new RenderContext(t, 1.0 / Fps, colors, speed, 1f, Direction.Forward, samples, seed);
        gen.Render(in ctx, output);
        return output;
    }

    public static byte[] ToBytes(Rgb[] px)
    {
        var b = new byte[px.Length * 3];
        for (var i = 0; i < px.Length; i++)
        {
            b[3 * i] = px[i].R;
            b[(3 * i) + 1] = px[i].G;
            b[(3 * i) + 2] = px[i].B;
        }

        return b;
    }

    public static ControllerDescriptor Controller(string id, params ZoneLayout[] zones) =>
        new(new ControllerId(id), "Test", id, DeviceType.Virtual, DeviceClass.Virtual, id,
            zones.Select((z, i) => new ZoneDescriptor(new ZoneId(i), $"z{i}", z)).ToArray(), Capabilities.DirectOnly(60));
}

internal sealed class RecordingAdapter : IDeviceAdapter
{
    private readonly object _gate = new();

    public string Vendor => "Test";

    public DeviceClass Class => DeviceClass.Virtual;

    public List<(ControllerId Id, ZoneId Zone, Rgb[] Colors)> Writes { get; } = [];

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);

    public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        lock (_gate)
        {
            Writes.Add((id, zone, colors.ToArray()));
        }
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    public Task CloseAsync(ControllerId id) => Task.CompletedTask;
}
