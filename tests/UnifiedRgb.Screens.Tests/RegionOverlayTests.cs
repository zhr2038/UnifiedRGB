using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Runtime;
using Color = System.Windows.Media.Color;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Generic overlay telemetry regions (scene package schemaVersion 2): a dashboard confined to a normalized rectangle of the content
/// canvas on rectangular screens. Strict clipping, optional backing, stable text sizes, no zeros for missing values, a cache keyed on
/// the complete layout and accent, local rotations, and the unchanged historical layout without a region.
/// </summary>
public sealed class RegionOverlayTests
{
    [Theory]
    [InlineData(.00001, .50)]
    [InlineData(.50, .00001)]
    public void An_unreadably_small_region_is_rejected_before_screen_output(double width, double height)
    {
        var layout = new ScreenOverlayLayoutDto(.01, .01, width, height, 2, 0);
        var assignment = new ScreenAssignmentDto("screen:turzx:a", Overlay: "dashboard", OverlayLayout: layout);
        var validate = () => ScreenSceneValidator.ValidateAssignment(assignment, null);
        validate.Should().Throw<ArgumentException>().WithMessage("*too small*");
        OverlayLayouts.TargetProblem("screen:turzx:a", layout).Should().Contain("too small");
    }

    private static readonly DateTimeOffset Now = HeadlessProbe.Now;
    private static readonly Color Gold = Color.FromRgb(0xFA, 0xD6, 0x1D);
    private static readonly ScreenOverlayLayoutDto Right = new(0.50, 0.16, 0.48, 0.74, 2, 0.0);
    private const string Case = "screen:turzx:a";

    public static TheoryData<double, double, double, double, int, double> Layouts() => new()
    {
        { 0.50, 0.16, 0.48, 0.74, 2, 0.0 },
        { 0.00, 0.00, 1.00, 1.00, 4, 0.0 },
        { 0.70, 0.00, 0.30, 1.00, 1, 0.6 },
        { 0.25, 0.50, 0.50, 0.50, 3, 1.0 },
        { 0.60, 0.10, 0.40, 0.30, 4, 0.0 },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Nothing_is_drawn_outside_the_region_and_the_readings_are_inside(double x, double y, double w, double h, int columns, double opacity)
    {
        var layout = new ScreenOverlayLayoutDto(x, y, w, h, columns, opacity);
        foreach (var telemetry in new[] { HeadlessProbe.Telemetry, RoundExamples.Long, null })
        {
            var layer = await RenderAsync(1920, 480, layout, telemetry);
            var r = OverlayLayouts.Pixels(layout, 1920, 480);

            PaintedOutside(layer, r).Should().Be(0, "every pixel outside the region stays fully transparent");
            HeadlessProbe.Glyphs(layer).Should().BeGreaterThan(telemetry is null ? 40 : 400, "white figures are drawn inside the region");
            Accent(layer).Should().BeGreaterThan(30, "accent labels are drawn");
            if (opacity == 0)
            {
                EdgeTouches(layer, r).Should().Be(0, "text and separators fit inside the padded region");
            }
        }
    }

    [Fact]
    public async Task Zero_opacity_has_no_backing_and_a_nonzero_opacity_covers_the_region_with_midnight()
    {
        var bare = await RenderAsync(1920, 480, Right, HeadlessProbe.Telemetry);
        var backed = await RenderAsync(1920, 480, Right with { BackgroundOpacity = 0.5 }, HeadlessProbe.Telemetry);
        var r = OverlayLayouts.Pixels(Right, 1920, 480);

        (Painted(bare) / (double)(r.Width * r.Height)).Should().BeLessThan(0.2, "no black block: only text, shadows, labels and hairlines");
        CoveredAtLeast(backed, r, 127).Should().Be(r.Width * r.Height, "the backing fills the whole region at the requested opacity");
        PaintedOutside(backed, r).Should().Be(0);
    }

    [Fact]
    public async Task Text_sizes_hold_for_longest_missing_and_stale_values()
    {
        var typical = await RenderAsync(1920, 480, Right, HeadlessProbe.Telemetry);
        var longest = await RenderAsync(1920, 480, Right, RoundExamples.Long);
        var missing = await RenderAsync(1920, 480, Right, null);
        var stale = await RenderAsync(1920, 480, Right, HeadlessProbe.Telemetry with { Timestamp = Now.AddSeconds(-6) });

        var (t, l) = (RoundGeometry.GlyphBands(typical), RoundGeometry.GlyphBands(longest));
        l.Should().HaveSameCount(t, "the same rows of figures");
        l.Zip(t).Should().OnlyContain(p => Math.Abs(p.First - p.Second) <= 1, "one fitted size for typical and longest values");
        RoundGeometry.GlyphBands(typical).Max().Should().BeGreaterThanOrEqualTo(40, "large clear figures in a 922x355 region");
        stale.Pixels.Should().Equal(missing.Pixels, "a stale sample shows exactly like a missing one (em dashes)");
        HeadlessProbe.Glyphs(missing).Should().BeLessThan(HeadlessProbe.Glyphs(typical) / 3, "missing values are dashes, never drawn as zeros");
        OverlayRenderer.Values(null, Now).Should().OnlyContain(v => v.Value == OverlayRenderer.Missing);
    }

    [Fact]
    public async Task Without_a_region_the_historical_layers_are_unchanged_and_the_cache_keys_on_layout_and_accent()
    {
        var (legacy, explicitNull, same, region, moved, recoloured, columns, legacyAgain) = await Sta.RunAsync(() =>
        {
            var fresh = new OverlayRenderer().Render(1920, 480, OverlayShape.Rectangle, "dashboard", Gold, HeadlessProbe.Telemetry, Now)!;
            var r = new OverlayRenderer();
            var n = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Gold, HeadlessProbe.Telemetry, Now, null)!;
            var a = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Gold, HeadlessProbe.Telemetry, Now, Right)!;
            var a2 = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Gold, HeadlessProbe.Telemetry, Now, Right with { })!;
            var b = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Gold, HeadlessProbe.Telemetry, Now, Right with { X = 0.02 })!;
            var c = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Color.FromRgb(0x5D, 0xDA, 0xC8), HeadlessProbe.Telemetry, Now, Right with { X = 0.02 })!;
            var d = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Color.FromRgb(0x5D, 0xDA, 0xC8), HeadlessProbe.Telemetry, Now, Right with { X = 0.02, Columns = 4 })!;
            var back = r.Render(1920, 480, OverlayShape.Rectangle, "dashboard", Gold, HeadlessProbe.Telemetry, Now)!;
            return (fresh, n, ReferenceEquals(a, a2), a, b, c, d, back);
        });

        explicitNull.Pixels.Should().Equal(legacy.Pixels, "a null layout is the historical full-screen dashboard, byte for byte");
        legacyAgain.Pixels.Should().Equal(legacy.Pixels);
        same.Should().BeTrue("an equal layout hits the cache");
        moved.Pixels.Should().NotEqual(region.Pixels, "moving the region re-renders");
        recoloured.Pixels.Should().NotEqual(moved.Pixels, "a new accent re-renders");
        columns.Pixels.Should().NotEqual(recoloured.Pixels, "a new column count re-renders");
        region.Pixels.Should().NotEqual(legacy.Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task A_region_turns_with_the_content_under_the_local_rotation(int rotation)
    {
        var a = new ScreenAssignmentDto(Case, BackgroundColor: "#203040", Overlay: "dashboard", Rotation: rotation, AccentColor: "#FAD61D", OverlayLayout: Right);
        using var store = new TempAssets();

        var (with, without) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store.Assets, 0);
            return (c.Compose(ScreenFamilyInfo.Turzx, a, 0, HeadlessProbe.Telemetry, Now),
                c.Compose(ScreenFamilyInfo.Turzx, a with { Overlay = "none", OverlayLayout = null }, 0, HeadlessProbe.Telemetry, Now).Canvas);
        });

        with.Warning.Should().BeNull();
        var swap = rotation is 90 or 270;
        var (cw, ch) = swap ? (480, 1920) : (1920, 480);
        var mask = PixelFrame.Solid(cw, ch, 0, 0, 0);
        var r = OverlayLayouts.Pixels(Right, cw, ch);
        for (var y = r.Y; y < r.Y + r.Height; y++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                mask.Pixels[(((y * cw) + x) * 4) + 1] = 255;
            }
        }

        var rotated = rotation == 0 ? mask : PixelOps.Rotate(mask, rotation);
        var (inside, outside) = (0, 0);
        for (var i = 0; i < with.Canvas.Pixels.Length; i += 4)
        {
            if (with.Canvas.Pixels[i] != without.Pixels[i] || with.Canvas.Pixels[i + 1] != without.Pixels[i + 1] || with.Canvas.Pixels[i + 2] != without.Pixels[i + 2])
            {
                _ = rotated.Pixels[i + 1] == 255 ? inside++ : outside++;
            }
        }

        outside.Should().Be(0, "only the rotated region changes");
        inside.Should().BeGreaterThan(2000, "the readings are drawn");
    }

    [Fact]
    public async Task A_blocked_timeline_stays_black_with_no_overlay_and_round_panels_never_take_a_region()
    {
        var region = new ScreenAssignmentDto(Case, BackgroundAssetId: "0123456789abcdef0123456789abcdef", Overlay: "dashboard", OverlayLayout: Right,
            TimelineId: "package:test");
        var round = new ScreenAssignmentDto("screen:h2:a", BackgroundColor: "#203040", Overlay: "dashboard", OverlayLayout: Right);
        using var store = new TempAssets();

        var (blocked, refused) = await Sta.RunAsync(() =>
        {
            var c = new ScreenCompositor(store.Assets, 0);
            return (c.Compose(ScreenFamilyInfo.Turzx, region, 0, HeadlessProbe.Telemetry, Now), c.Compose(ScreenFamilyInfo.H2, round, 0, HeadlessProbe.Telemetry, Now));
        });

        blocked.Warning.Should().StartWith(ScreenCompositor.BlockedPrefix);
        blocked.Canvas.Pixels.Where((_, i) => i % 4 != 3).Should().OnlyContain(b => b == 0, "strict content that cannot play is black, no overlay");
        refused.Warning.Should().StartWith(ScreenCompositor.BlockedPrefix, "a region that reached a round panel is never dropped or widened");
        refused.Canvas.Pixels.Where((_, i) => i % 4 != 3).Should().OnlyContain(b => b == 0);
    }

    [Theory]
    [InlineData("screen:h2:a", "dashboard", 0.5, 0.1, 0.4, 0.4, 2, 0.0, "*rectangular*")]
    [InlineData("screen:tl-lcd:a", "dashboard", 0.5, 0.1, 0.4, 0.4, 2, 0.0, "*rectangular*")]
    [InlineData(Case, "none", 0.5, 0.1, 0.4, 0.4, 2, 0.0, "*dashboard*")]
    [InlineData(Case, "compact", 0.5, 0.1, 0.4, 0.4, 2, 0.0, "*dashboard*")]
    [InlineData(Case, "dashboard", 0.7, 0.1, 0.4, 0.4, 2, 0.0, "*inside the canvas*")]
    [InlineData(Case, "dashboard", 0.5, 0.7, 0.4, 0.4, 2, 0.0, "*inside the canvas*")]
    [InlineData(Case, "dashboard", -0.1, 0.1, 0.4, 0.4, 2, 0.0, "*x and y*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, 0, 0.4, 2, 0.0, "*width and height*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, 0.4, 1.5, 2, 0.0, "*width and height*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, 0.4, 0.4, 0, 0.0, "*columns*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, 0.4, 0.4, 5, 0.0, "*columns*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, 0.4, 0.4, 2, 1.01, "*backgroundOpacity*")]
    [InlineData(Case, "dashboard", double.NaN, 0.1, 0.4, 0.4, 2, 0.0, "*finite*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, double.PositiveInfinity, 0.4, 2, 0.0, "*finite*")]
    [InlineData(Case, "dashboard", 0.5, 0.1, 0.4, 0.4, 2, double.NaN, "*finite*")]
    public void The_generic_screen_scene_validates_regions_like_the_package_format(
        string screen, string overlay, double x, double y, double w, double h, int columns, double opacity, string message)
    {
        var a = new ScreenAssignmentDto(screen, Overlay: overlay, OverlayLayout: new ScreenOverlayLayoutDto(x, y, w, h, columns, opacity));
        FluentActions.Invoking(() => ScreenSceneValidator.ValidateAssignment(a, null)).Should().Throw<ArgumentException>().WithMessage(message);
        ScreenSceneValidator.ValidateAssignment(new ScreenAssignmentDto(Case, Overlay: "dashboard", OverlayLayout: Right), null);
        ScreenSceneValidator.ValidateAssignment(new ScreenAssignmentDto(Case, Overlay: "dashboard", OverlayLayout: new(0, 0, 1, 1, 4, 1)), null);
    }

    [Fact]
    public async Task Scene_json_without_a_region_is_unchanged_and_a_region_round_trips()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx, Devices.H2);
        var plain = new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#203040", Overlay: "compact");
        var region = new ScreenAssignmentDto(Devices.Turzx.Id, BackgroundColor: "#203040", Overlay: "dashboard", AccentColor: "#FAD61D", OverlayLayout: Right);
        var json = System.Text.Json.JsonSerializer.Serialize(plain, UnifiedRgb.Contracts.ContractsJsonContext.Default.ScreenAssignmentDto);
        json.Should().NotContain("overlayLayout", "a null region is never written, so existing RPC/profile/scene JSON stays byte-identical");

        await rig.Manager.SetScreenSceneAsync(Images.Scene(plain, region));
        var path = Path.Combine(rig.DataDirectory, "screens", "scene.json");
        var text = await File.ReadAllTextAsync(path);
        text.Should().Contain("\"overlayLayout\"").And.Contain("\"backgroundOpacity\"");
        await using var second = new Rig(Rig.AllOutput, Devices.Turzx, Devices.H2);
        File.Copy(path, Path.Combine(Directory.CreateDirectory(Path.Combine(second.DataDirectory, "screens")).FullName, "scene.json"));
        (await second.Manager.GetScreenSceneAsync()).Assignments.Should().Equal(plain, region);
    }

    [Fact]
    public async Task Setting_a_region_on_a_round_screen_is_refused_and_keeps_the_previous_scene()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx, Devices.H2);
        var good = Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id, Overlay: "dashboard", OverlayLayout: Right));
        await rig.Manager.SetScreenSceneAsync(good);

        var bad = () => rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, Overlay: "dashboard", OverlayLayout: Right)));
        await bad.Should().ThrowAsync<ArgumentException>().WithMessage("*rectangular*");
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Should().Equal(good.Assignments);
        var preview = () => rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(Devices.H2.Id, Overlay: "dashboard", OverlayLayout: Right));
        await preview.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_encoded_preview_shows_the_readings_only_inside_the_region()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        var a = new ScreenAssignmentDto(Devices.Turzx.Id, BackgroundColor: "#203040", Overlay: "dashboard", AccentColor: "#FAD61D", OverlayLayout: Right);

        var p = await rig.Manager.PreviewScreenAsync(a);
        var preview = await Sta.RunAsync(() => WpfCodec.DecodePng(Convert.FromBase64String(p.PngBase64), 1920 * 1920));

        (preview.Width, preview.Height).Should().Be((1920, 480));
        var r = OverlayLayouts.Pixels(Right, 1920, 480);
        var (inside, outside) = (0, 0);
        for (var y = 0; y < preview.Height; y++)
        {
            for (var x = 0; x < preview.Width; x++)
            {
                var (b, g, rr, _) = preview.At(x, y);
                var differs = Math.Abs(rr - 0x20) > 12 || Math.Abs(g - 0x30) > 12 || Math.Abs(b - 0x40) > 12;
                var within = x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
                _ = differs ? (within ? inside++ : outside++) : 0;
            }
        }

        outside.Should().Be(0, "the actual encoded frame has only background outside the region");
        inside.Should().BeGreaterThan(2000, "the encoded frame carries the readings");
    }

    private static Task<PixelFrame> RenderAsync(int w, int h, ScreenOverlayLayoutDto layout, PcTelemetryDto? telemetry) =>
        Sta.RunAsync(() => new OverlayRenderer().Render(w, h, OverlayShape.Rectangle, "dashboard", Gold, telemetry, Now, layout)
            ?? throw new InvalidOperationException("the overlay rendered nothing"));

    private static int Painted(PixelFrame f) => f.Pixels.Where((_, i) => i % 4 == 3).Count(a => a != 0);

    private static int PaintedOutside(PixelFrame f, (int X, int Y, int Width, int Height) r)
    {
        var n = 0;
        for (var y = 0; y < f.Height; y++)
        {
            for (var x = 0; x < f.Width; x++)
            {
                var within = x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
                n += !within && f.Pixels[(((y * f.Width) + x) * 4) + 3] != 0 ? 1 : 0;
            }
        }

        return n;
    }

    /// <summary>Painted pixels on the region's outermost row/column (text or lines touching the clip edge).</summary>
    private static int EdgeTouches(PixelFrame f, (int X, int Y, int Width, int Height) r)
    {
        var n = 0;
        for (var y = r.Y; y < r.Y + r.Height; y++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                var edge = x == r.X || y == r.Y || x == r.X + r.Width - 1 || y == r.Y + r.Height - 1;
                n += edge && f.Pixels[(((y * f.Width) + x) * 4) + 3] != 0 ? 1 : 0;
            }
        }

        return n;
    }

    private static int CoveredAtLeast(PixelFrame f, (int X, int Y, int Width, int Height) r, byte alpha)
    {
        var n = 0;
        for (var y = r.Y; y < r.Y + r.Height; y++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                n += f.Pixels[(((y * f.Width) + x) * 4) + 3] >= alpha ? 1 : 0;
            }
        }

        return n;
    }

    /// <summary>Gold accent pixels (premultiplied BGRA: strong red/green, weak blue).</summary>
    private static int Accent(PixelFrame f)
    {
        var n = 0;
        var p = f.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            n += p[i + 3] > 150 && p[i + 2] > 150 && p[i + 1] > 120 && p[i] < 80 ? 1 : 0;
        }

        return n;
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
