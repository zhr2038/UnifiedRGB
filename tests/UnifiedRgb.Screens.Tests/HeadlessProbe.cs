using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using Color = System.Windows.Media.Color;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// One overlay layer: pixels with any alpha, dark translucent backing-panel pixels, white glyph pixels and (round shape only)
/// painted pixels farther than <see cref="OverlayRenderer.SafeRadius"/> from the centre.
/// </summary>
internal sealed record OverlayProbe(string Overlay, string Shape, int Width, int Height, bool Rendered, int Painted, int Backing, int Text, int OutsideSafe);

/// <summary>One full compose + family encode on a grey background; <see cref="Text"/> counts near-white pixels after decoding.</summary>
internal sealed record ComposeProbe(string Family, string Overlay, string Note, string? Warning, int EncodedBytes, int Text);

internal sealed record HeadlessProbeReport(
    bool SwitchEnabled,
    bool? MediaContextGate,
    bool UserInteractive,
    string CurrentCulture,
    string? InvariantGlobalization,
    string? PredefinedCulturesOnly,
    string? UnsafeBinaryFormatter,
    IReadOnlyList<OverlayProbe> Overlays,
    IReadOnlyList<ComposeProbe> Composes);

/// <summary>
/// The overlay rendering probe: offscreen WPF rendering and encoding only (no device, no USB, no display or OS setting). The tests
/// run it in-process and, through this assembly's entry point (<c>--headless-probe out.json</c>), in a child process started with
/// the Host exe's own runtimeconfig, so the Host's AppContext switches and globalization settings are the ones in effect.
/// </summary>
internal static class HeadlessProbe
{
    public const string Argument = "--headless-probe";

    public static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static readonly PcTelemetryDto Telemetry = new(Now, 25, 12L << 30, 32L << 30, 42, 61, 8L << 30, 32L << 30, "probe", null);

    /// <summary>The three family canvases with their overlay shapes (TL-LCD, H2, TURZX).</summary>
    public static readonly (int W, int H, OverlayShape Shape)[] Sizes =
        [(400, 400, OverlayShape.Round), (480, 480, OverlayShape.Round), (1920, 480, OverlayShape.Rectangle)];

    public static int Main(string[] args)
    {
        if (args is not [Argument or RoundExamples.Argument, var output])
        {
            Console.Error.WriteLine($"usage: {Argument} <out.json> | {RoundExamples.Argument} <directory> (the tests themselves run through the test host)");
            return 2;
        }

        object? report = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                report = args[0] == Argument ? Run() : RoundExamples.Write(output);
            }
#pragma warning disable CA1031 // reported to the parent test through stderr and the exit code
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
            finally
            {
                StaThread.ShutdownDispatcher();
            }
        })
        { Name = "headless probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        File.WriteAllText(args[0] == Argument ? output : Path.Combine(output, "round-examples.json"),
            JsonSerializer.Serialize(report, args[0] == Argument ? JsonSerializerOptions.Default : Indented));
        return 0;
    }

    /// <summary>Runs the probe (STA thread only).</summary>
    public static HeadlessProbeReport Run()
    {
        var gate = typeof(System.Windows.Media.Visual).Assembly.GetType("System.Windows.Media.MediaContext")
            ?.GetProperty("ShouldRenderEvenWhenNoDisplayDevicesAreAvailable", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as bool?;

        var overlays = new List<OverlayProbe>();
        foreach (var (w, h, shape) in Sizes)
        {
            foreach (var overlay in new[] { "compact", "dashboard" })
            {
                var layer = new OverlayRenderer().Render(w, h, shape, overlay, Color.FromRgb(255, 0, 0), Telemetry, Now);
                overlays.Add(layer is null ? new OverlayProbe(overlay, shape.ToString(), w, h, false, 0, 0, 0, 0) : Measure(overlay, shape, layer));
            }
        }

        var composes = new List<ComposeProbe>();
        var root = Path.Combine(Path.GetTempPath(), "urgb-headless-probe", Guid.NewGuid().ToString("N"));
        try
        {
            var compositor = new ScreenCompositor(new AssetStore(root, 1 << 20, 1, NullLogger.Instance), 0);
            foreach (var family in new[] { ScreenFamilyInfo.TlLcd, ScreenFamilyInfo.H2, ScreenFamilyInfo.Turzx })
            {
                foreach (var overlay in new[] { "none", "compact", "dashboard" })
                {
                    var a = new ScreenAssignmentDto("screen:probe:a", BackgroundColor: "#404040", Overlay: overlay, AccentColor: "#FF0000");
                    var composed = compositor.Compose(family, a, 0, Telemetry, Now);
                    var encoded = ScreenFrameEncoder.Encode(family, composed.Canvas, 90);
                    composes.Add(new ComposeProbe(family.Token, overlay, encoded.Note, composed.Warning, encoded.Image.Length,
                        NearWhite(Decode(encoded.Image))));
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        return new HeadlessProbeReport(HeadlessRendering.Enabled, gate, Environment.UserInteractive, CultureInfo.CurrentCulture.Name,
            Data("System.Globalization.Invariant"), Data("System.Globalization.PredefinedCulturesOnly"),
            Data("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"), overlays, composes);
    }

    /// <summary>Opaque near-white pixels: the white value text (the backing, accent and grey background never qualify).</summary>
    public static int NearWhite(PixelFrame f)
    {
        var n = 0;
        var p = f.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            n += p[i + 3] == 255 && p[i] >= 200 && p[i + 1] >= 200 && p[i + 2] >= 200 ? 1 : 0;
        }

        return n;
    }

    private static OverlayProbe Measure(string overlay, OverlayShape shape, PixelFrame layer)
    {
        int painted = 0, backing = 0;
        var p = layer.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            painted += a != 0 ? 1 : 0;

            // premultiplied black at the panel alpha (150 dashboard / 170 compact)
            backing += a is >= 140 and <= 180 && p[i] <= 8 && p[i + 1] <= 8 && p[i + 2] <= 8 ? 1 : 0;
        }

        var outside = shape == OverlayShape.Round ? RoundGeometry.PaintedOutside(layer, OverlayRenderer.SafeRadius) : 0;
        return new OverlayProbe(overlay, shape.ToString(), layer.Width, layer.Height, true, painted, backing, Glyphs(layer), outside);
    }

    /// <summary>
    /// White glyph pixels in a premultiplied layer: at least half white coverage over the panel (alpha above the panel's, neutral
    /// channels of 128 or more). Small text has few fully covered pixels, so fully opaque white alone would undercount it.
    /// </summary>
    public static int Glyphs(PixelFrame layer)
    {
        var n = 0;
        var p = layer.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            n += p[i + 3] > 180 && p[i] >= 128 && p[i + 1] >= 128 && p[i + 2] >= 128 ? 1 : 0;
        }

        return n;
    }

    public static PixelFrame Decode(byte[] image)
    {
        using var ms = new MemoryStream(image, writable: false);
        return WpfCodec.ToPixels(WpfCodec.Decoder(WpfCodec.Sniff(image)!, ms).Frames[0]);
    }

    private static string? Data(string name) => AppContext.GetData(name)?.ToString()?.ToLowerInvariant();
}
