using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Telemetry overlays must render in the Host when no display device is available (monitor asleep or off). The Host exe's
/// runtimeconfig carries the WPF switch; these tests read the emitted file and run the overlay probe under exactly that file
/// (child process, invariant globalization), checking real text and backing-panel pixels rather than encoded sizes.
/// </summary>
public sealed class HeadlessRenderingTests
{
    /// <summary>The Host's runtimeconfig as emitted by its build (copied beside this assembly by the project reference).</summary>
    private static string HostRuntimeConfig => Path.Combine(AppContext.BaseDirectory, "UnifiedRgb.Host.runtimeconfig.json");

    [Fact]
    public void The_host_runtimeconfig_turns_the_switch_on_with_invariant_globalization()
    {
        var options = JsonNode.Parse(File.ReadAllText(HostRuntimeConfig))!["runtimeOptions"]!;
        var props = options["configProperties"]!;

        props[HeadlessRendering.SwitchName]!.GetValue<bool>().Should().BeTrue();
        props["System.Globalization.Invariant"]!.GetValue<bool>().Should().BeTrue();
        props["System.Globalization.PredefinedCulturesOnly"]!.GetValue<bool>().Should().BeTrue();
        options["frameworks"]!.AsArray().Select(f => (string)f!["name"]!).Should().Contain("Microsoft.WindowsDesktop.App");
    }

    [Fact]
    public async Task Overlays_render_text_under_the_host_runtimeconfig()
    {
        var report = await ProbeAsync(HostRuntimeConfig);

        report.SwitchEnabled.Should().BeTrue();
        report.MediaContextGate.Should().BeTrue("WPF's MediaContext took the switch from the Host runtimeconfig");
        report.UnsafeBinaryFormatter.Should().Be("false", "the Host's runtimeconfig is in effect, not the test host's");
        report.InvariantGlobalization.Should().Be("true");
        report.PredefinedCulturesOnly.Should().Be("true");
        report.CurrentCulture.Should().BeEmpty("the Host runs with the invariant culture");
        AssertRendered(report);
    }

    [Fact]
    public async Task Without_the_switch_the_wpf_gate_falls_back_to_its_default()
    {
        // negative control: the same Host runtimeconfig minus the switch (a temp copy; the emitted file is never modified)
        var node = JsonNode.Parse(File.ReadAllText(HostRuntimeConfig))!;
        node["runtimeOptions"]!["configProperties"]!.AsObject().Remove(HeadlessRendering.SwitchName).Should().BeTrue();
        var temp = TempFile("runtimeconfig.json");
        try
        {
            File.WriteAllText(temp, node.ToJsonString());

            var report = await ProbeAsync(temp);

            report.SwitchEnabled.Should().BeFalse();
            report.MediaContextGate.Should().Be(!report.UserInteractive,
                "an interactive process stops rendering when no display device is available unless the switch is set");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public async Task Overlays_render_text_in_the_test_host()
    {
        var report = await Sta.RunAsync(HeadlessProbe.Run);

        report.SwitchEnabled.Should().BeTrue("the test project sets the same switch");
        AssertRendered(report);
    }

    [Fact]
    public async Task An_empty_overlay_layer_is_reported_and_never_cached()
    {
        var calls = 0;
        PixelFrame FirstEmpty(DrawingVisual v, int w, int h) => ++calls == 1 ? new PixelFrame(w, h) : PixelFrame.Solid(w, h, 255, 255, 255);
        var accent = Color.FromRgb(255, 0, 0);
        var t = HeadlessProbe.Telemetry;

        var (first, second, third) = await Sta.RunAsync(() =>
        {
            var r = new OverlayRenderer(FirstEmpty);
            return (r.Render(400, 400, OverlayShape.Round, "compact", accent, t, HeadlessProbe.Now),
                r.Render(400, 400, OverlayShape.Round, "compact", accent, t, HeadlessProbe.Now),
                r.Render(400, 400, OverlayShape.Round, "compact", accent, t, HeadlessProbe.Now));
        });

        first.Should().BeNull("a fully transparent layer means offscreen rendering did not run");
        second.Should().NotBeNull("the empty result was not cached");
        third.Should().BeSameAs(second, "a real layer is cached until its text changes");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_compose_without_overlay_pixels_says_so()
    {
        using var store = new AssetStoreDir();
        var a = new ScreenAssignmentDto("screen:h2:a", BackgroundColor: "#404040", Overlay: "dashboard");

        var result = await Sta.RunAsync(() => new ScreenCompositor(store.Assets, 0, new OverlayRenderer((_, w, h) => new PixelFrame(w, h)))
            .Compose(ScreenFamilyInfo.H2, a, 0, HeadlessProbe.Telemetry, HeadlessProbe.Now));

        result.Warning.Should().Be("telemetry overlay rendered nothing (WPF offscreen rendering unavailable)");
        result.Canvas.Pixels.Chunk(4).Should().OnlyContain(p => p[0] == 0x40 && p[1] == 0x40 && p[2] == 0x40 && p[3] == 255);
    }

    private static void AssertRendered(HeadlessProbeReport report)
    {
        report.Overlays.Should().HaveCount(HeadlessProbe.Sizes.Length * 2);
        foreach (var o in report.Overlays)
        {
            var area = o.Width * o.Height;
            var what = $"{o.Overlay} {o.Shape} {o.Width}x{o.Height}";
            o.Rendered.Should().BeTrue(what);
            o.Backing.Should().BeGreaterThan(area / (o.Overlay == "compact" ? 20 : o.Shape == "Round" ? 4 : 3), $"{what} backing panels");
            o.Text.Should().BeGreaterThan(MinGlyphs(o.Overlay), $"{what} white text");
            o.OutsideSafe.Should().Be(0, $"{what}: nothing outside the safe circle");
        }

        report.Composes.Should().HaveCount(9);
        foreach (var c in report.Composes)
        {
            c.Warning.Should().BeNull($"{c.Family} {c.Overlay}");
            if (c.Overlay == "none")
            {
                c.Text.Should().Be(0, $"{c.Family}: the grey background alone has no white");
            }
            else
            {
                c.Text.Should().BeGreaterThan(MinGlyphs(c.Overlay), $"{c.Family} {c.Overlay} text survives encoding ({c.Note})");
            }
        }
    }

    /// <summary>About a third of the smallest count measured (400x400; 2026-10-18): font hinting may vary, an empty render is 0.</summary>
    private static int MinGlyphs(string overlay) => overlay == "compact" ? 300 : 1500;

    /// <summary>Runs this assembly's probe entry point in a child process under <paramref name="runtimeConfig"/>.</summary>
    private static async Task<HeadlessProbeReport> ProbeAsync(string runtimeConfig)
    {
        var output = TempFile("probe.json");
        var psi = new ProcessStartInfo(Muxer())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "exec", "--runtimeconfig", runtimeConfig, typeof(HeadlessProbe).Assembly.Location, HeadlessProbe.Argument, output })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var child = Process.Start(psi)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("the headless probe did not finish within 60 s");
            }

            var errors = await stderr;
            await stdout;
            child.ExitCode.Should().Be(0, errors);
            return JsonSerializer.Deserialize<HeadlessProbeReport>(File.ReadAllText(output))!;
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>The dotnet muxer running this test host.</summary>
    private static string Muxer()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { } host && File.Exists(host))
        {
            return host;
        }

        // <root>\shared\Microsoft.NETCore.App\<version>\ → <root>\dotnet.exe
        var runtime = new DirectoryInfo(Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory()));
        return Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet.exe");
    }

    private static string TempFile(string suffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-screens-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + "-" + suffix);
    }

    private sealed class AssetStoreDir : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));

        public AssetStoreDir() => Assets = new AssetStore(_root, 1 << 20, 1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

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
