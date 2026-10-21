using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using Color = System.Windows.Media.Color;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// TL-LCD (400x400) and H2 (480x480) panels show only their inscribed circle. Their telemetry overlays must stay inside the safe
/// circle (0.47 D), keep their size as values change and be larger than the old bottom line; TURZX keeps the wide layout.
/// </summary>
public sealed class RoundOverlayTests
{
    private static readonly DateTimeOffset Now = HeadlessProbe.Now;
    private static readonly Color Red = Color.FromRgb(255, 0, 0);

    public static TheoryData<int, string, string> Cases()
    {
        var data = new TheoryData<int, string, string>();
        foreach (var d in new[] { 400, 480 })
        {
            foreach (var overlay in new[] { "compact", "dashboard" })
            {
                foreach (var telemetry in new[] { "typical", "longest", "missing", "stale", "no-gpu" })
                {
                    data.Add(d, overlay, telemetry);
                }
            }
        }

        return data;
    }

    [Fact]
    public void Tl_lcd_and_h2_are_round_and_turzx_keeps_the_wide_rectangle()
    {
        OverlayRenderer.ShapeOf(ScreenFamily.TlLcd).Should().Be(OverlayShape.Round);
        OverlayRenderer.ShapeOf(ScreenFamily.H2).Should().Be(OverlayShape.Round);
        OverlayRenderer.ShapeOf(ScreenFamily.Turzx).Should().Be(OverlayShape.Rectangle);
        OverlayRenderer.SafeRadius.Should().Be(0.47);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Round_overlays_stay_inside_the_safe_circle(int d, string overlay, string telemetry)
    {
        var layer = await RenderAsync(d, overlay, Telemetry(telemetry));

        RoundGeometry.PaintedOutside(layer, OverlayRenderer.SafeRadius).Should().Be(0, "every label, value, accent and panel pixel is inside 0.47 D");
        RoundGeometry.MaxPaintedRadius(layer).Should().BeLessThan(0.45, "with a margin of more than 0.02 D to the safe circle");
        var dashes = telemetry is "missing" or "stale";
        HeadlessProbe.Glyphs(layer).Should().BeGreaterThan(overlay == "compact" ? (dashes ? 60 : 300) : (dashes ? 200 : 1500), "white value text is drawn");
        RoundGeometry.Accent(layer).Should().BeGreaterThan(300, "accent labels and lines are drawn");
    }

    [Theory]
    [InlineData(400, "compact")]
    [InlineData(480, "compact")]
    [InlineData(400, "dashboard")]
    [InlineData(480, "dashboard")]
    public async Task Panels_and_text_sizes_do_not_change_with_the_values(int d, string overlay)
    {
        var typical = await RenderAsync(d, overlay, Telemetry("typical"));
        var longest = await RenderAsync(d, overlay, Telemetry("longest"));
        var missing = await RenderAsync(d, overlay, Telemetry("missing"));

        Painted(longest).Should().Equal(Painted(typical), "the panels do not move or resize, and no text leaves them");
        Painted(missing).Should().Equal(Painted(typical));
        var (t, l) = (RoundGeometry.GlyphBands(typical), RoundGeometry.GlyphBands(longest));
        l.Should().HaveSameCount(t, "the same lines of white text");
        l.Zip(t).Should().OnlyContain(p => Math.Abs(p.First - p.Second) <= 1, $"same text size for typical {Join(t)} and longest {Join(l)} values");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(480)]
    public async Task Round_text_is_larger_than_the_old_bottom_line(int d)
    {
        var old = await Sta.RunAsync(() => new OverlayRenderer().Render(d, d, OverlayShape.Rectangle, "compact", Red, HeadlessProbe.Telemetry, Now)!);
        var compact = await RenderAsync(d, "compact", Telemetry("typical"));
        var dashboard = await RenderAsync(d, "dashboard", Telemetry("typical"));

        var line = RoundGeometry.GlyphBands(old).Should().ContainSingle().Subject;
        var rows = RoundGeometry.GlyphBands(compact);
        rows.Should().HaveCount(2, "two short rows");
        rows.Should().OnlyContain(h => h >= line * 1.3 && h >= 0.035 * d, $"the old bottom line was {line} px high; rows {string.Join("/", rows)}");
        RoundGeometry.GlyphBands(dashboard).Max().Should().BeGreaterThanOrEqualTo((int)(0.07 * d), "dashboard values are large");
    }

    [Theory]
    [InlineData("tl-lcd", "compact", 0)]
    [InlineData("tl-lcd", "dashboard", 90)]
    [InlineData("h2", "compact", 270)]
    [InlineData("h2", "dashboard", 180)]
    public async Task Round_compositions_change_nothing_outside_the_safe_circle(string token, string overlay, int rotation)
    {
        var family = ScreenFamilyInfo.All.Single(f => f.Token == token);
        var a = new ScreenAssignmentDto($"screen:{token}:a", BackgroundColor: "#3366CC", Overlay: overlay, Rotation: rotation, Brightness: 0.8, AccentColor: "#FF0000");
        using var store = new TempAssets();

        var (with, without) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store.Assets, 0);
            return (c.Compose(family, a, 0, RoundExamples.Long, Now).Canvas, c.Compose(family, a with { Overlay = "none" }, 0, RoundExamples.Long, Now).Canvas);
        });

        (with.Width, with.Height).Should().Be((family.Width, family.Height));
        RoundGeometry.ChangedOutside(with, without, OverlayRenderer.SafeRadius).Should().Be(0, "the background fills the raster; the overlay stays inside");
        RoundGeometry.ChangedOutside(with, without, 0).Should().BeGreaterThan(1000, "the overlay is there");
        without.At(0, 0).Should().Be(without.At(family.Width / 2, family.Height / 2), "the background colour still fills the square corners");
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("dashboard")]
    public async Task Turzx_keeps_the_wide_layout(string overlay)
    {
        var a = new ScreenAssignmentDto("screen:turzx:a", BackgroundColor: "#3366CC", Overlay: overlay, AccentColor: "#FF0000");
        using var store = new TempAssets();

        var (with, without, layer) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store.Assets, 0);
            return (c.Compose(ScreenFamilyInfo.Turzx, a, 0, HeadlessProbe.Telemetry, Now).Canvas,
                c.Compose(ScreenFamilyInfo.Turzx, a with { Overlay = "none" }, 0, HeadlessProbe.Telemetry, Now).Canvas,
                new OverlayRenderer().Render(1920, 480, OverlayShape.Rectangle, overlay, Red, HeadlessProbe.Telemetry, Now)!);
        });

        var expected = without.Clone();
        PixelOps.BlendOver(expected, layer, 0, 0);
        PixelOps.Flatten(expected);
        with.Pixels.Should().Equal(expected.Pixels, "TURZX composes the unchanged rectangle layout");
        foreach (var (x, y) in overlay == "compact" ? new[] { (2, 477), (1917, 477) } : [(60, 60), (1860, 420)])
        {
            with.At(x, y).Should().NotBe(without.At(x, y), $"the wide {overlay} layout spans the canvas ({x}, {y})");
        }
    }

    [Theory]
    [InlineData("tl", "compact")]
    [InlineData("h2", "dashboard")]
    public async Task The_preview_is_what_the_round_screen_is_sent(string which, string overlay)
    {
        var device = which == "tl" ? Devices.Tl1 : Devices.H2;
        var family = which == "tl" ? ScreenFamilyInfo.TlLcd : ScreenFamilyInfo.H2;
        await using var rig = new Rig(null, Rig.AllOutput, new ManualTime(Now), device);
        await rig.Manager.GetPcTelemetryAsync(); // establish CPU deltas before either image; clock and fake metrics then stay fixed
        var a = new ScreenAssignmentDto(device.Id, BackgroundColor: "#203040", Overlay: overlay, AccentColor: "#FF0000", Fps: 10);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(a));
        rig.Manager.Start();

        PixelFrame? preview = null, sent = null;
        await Wait.UntilAsync(async () =>
        {
            if (rig.Opener.Opens == 0 || rig.Opener.Last.Writes.IsEmpty)
            {
                return false;
            }

            var p = await rig.Manager.PreviewScreenAsync(a);
            var packet = rig.Opener.Last.Writes.Last();
            var length = BinaryPrimitives.ReadInt32BigEndian(ScreenProtocol.DecryptHeader(family.Family, packet.AsSpan(0, 512)).AsSpan(8));
            (preview, sent) = await Sta.RunAsync(() =>
                (WpfCodec.DecodePng(Convert.FromBase64String(p.PngBase64), 480 * 480), HeadlessProbe.Decode(packet.AsSpan(512, length).ToArray())));
            return MeanAbsDiff(preview, sent) < 1; // the same telemetry values once the sampler has two CPU samples
        }, "a device frame that matches the preview", 20_000);

        var background = PixelFrame.Solid(family.Width, family.Height, 0x20, 0x30, 0x40);
        Differing(sent!, preview!, 64).Should().BeLessThan(family.Width * family.Height / 200, "only JPEG noise differs");
        Differing(sent!, background, 24).Should().BeGreaterThan(family.Width * family.Height / 20, "the overlay panels and text are in the device frame");
        // the preview is the decoded device JPEG (truthful preview), so it carries the same JPEG noise as the device frame; the exact
        // round layout of the composed canvas is asserted by Round_compositions_change_nothing_outside_the_safe_circle and the examples
        MaxDiffOutside(preview!, background, OverlayRenderer.SafeRadius).Should().BeLessThanOrEqualTo(12, "the preview shows the round layout (JPEG noise)");
        MaxDiffOutside(sent!, background, OverlayRenderer.SafeRadius).Should().BeLessThanOrEqualTo(12, "the device frame has only background there (JPEG noise)");
    }

    [Fact]
    public async Task The_four_inspectable_examples_keep_everything_inside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var examples = await Sta.RunAsync(() => RoundExamples.Write(dir));

            examples.Select(e => e.File).Should().Equal("tl-lcd-400-compact.png", "tl-lcd-400-dashboard.png", "h2-480-compact.png", "h2-480-dashboard.png");
            examples.Should().OnlyContain(e => e.ChangedOutsideSafe == 0 && e.MaxOverlayRadius < 0.45 && e.Note.StartsWith("JPEG q90", StringComparison.Ordinal));
            examples.Should().OnlyContain(e => File.Exists(Path.Combine(dir, e.File)));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private static Task<PixelFrame> RenderAsync(int d, string overlay, PcTelemetryDto? telemetry) =>
        Sta.RunAsync(() => new OverlayRenderer().Render(d, d, OverlayShape.Round, overlay, Red, telemetry, Now)
            ?? throw new InvalidOperationException("the overlay rendered nothing"));

    private static PcTelemetryDto? Telemetry(string name) => name switch
    {
        "typical" => HeadlessProbe.Telemetry,
        "longest" => RoundExamples.Long,
        "missing" => null,
        "stale" => HeadlessProbe.Telemetry with { Timestamp = Now.AddSeconds(-6) },
        "no-gpu" => RoundExamples.NoGpu,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static bool[] Painted(PixelFrame layer) => [.. layer.Pixels.Where((_, i) => i % 4 == 3).Select(a => a != 0)];

    private static string Join(IEnumerable<int> values) => "[" + string.Join(", ", values) + "]";

    private static double MeanAbsDiff(PixelFrame a, PixelFrame b)
    {
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        }

        return sum / (double)a.Pixels.Length;
    }

    /// <summary>Pixels where any colour channel differs by more than <paramref name="min"/>.</summary>
    private static int Differing(PixelFrame a, PixelFrame b, int min)
    {
        var n = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            n += Math.Abs(a.Pixels[i] - b.Pixels[i]) > min || Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) > min || Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > min ? 1 : 0;
        }

        return n;
    }

    private static int MaxDiffOutside(PixelFrame a, PixelFrame b, double radius)
    {
        var max = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (RoundGeometry.Radius(a, x, y) > radius)
                {
                    var i = ((y * a.Width) + x) * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        max = Math.Max(max, Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]));
                    }
                }
            }
        }

        return max;
    }

    private sealed class TempAssets : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));

        public TempAssets() => Assets = new AssetStore(_root, 1 << 20, 1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        public AssetStore Assets { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}
