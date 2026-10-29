using System.Diagnostics;
using System.Globalization;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Cross-build capture: compiled UNCHANGED against the pre-change and the changed encoder (it only uses ToNv12(frame, range) and
/// Encode). Every representative raster's NV12 bytes must equal the verbatim scalar oracle, and the Q95 access units of the actual
/// native rasters must be deterministic; their SHA-256 lines are compared across builds. With NV12_CAPTURE_DIR set it also writes host
/// timings of ToNv12 (product and unchanged scalar oracle) and of the whole Encode call at 1920x480 and 480x1920. Desktop timings, not
/// device facts.
/// </summary>
public sealed class Nv12EncodeCaptureTests
{
    [MediaFoundationFact]
    public async Task Representative_rasters_equal_the_scalar_oracle_and_encode_deterministically()
    {
        var rasters = await Nv12Rasters.RepresentativeAsync();
        var lines = new List<string>();
        foreach (var (name, frame) in rasters)
        foreach (var full in new[] { true, false })
        {
            var nv12 = MfH264IntraEncoder.ToNv12(frame, full);
            var expected = Nv12Oracle.ToNv12(frame, full);
            Nv12Rasters.FirstDifference(nv12, expected).Should().Be(-1, $"{name} full={full} must equal the scalar oracle byte for byte");
            lines.Add($"nv12 {name} full={full} {nv12.Length} {Nv12Rasters.Sha(nv12)}");
        }

        foreach (var name in new[] { "scene-telemetry-480x1920", "scene-telemetry-1920x480", "noise-480x480" })
        {
            var frame = rasters.Single(r => r.Name == name).Frame;
            foreach (var full in new[] { true, false })
            {
                // Consecutive IDR pictures of one encoder legitimately differ in their slice headers (retained first-run failure):
                // determinism is "another fresh encoder emits the same SEQUENCE", picture by picture.
                var (a1, a2, b1, b2) = await Sta.RunAsync(() =>
                {
                    using var a = new MfH264IntraEncoder(frame.Width, frame.Height, full);
                    using var b = new MfH264IntraEncoder(frame.Width, frame.Height, full);
                    return (a.Encode(frame), a.Encode(frame), b.Encode(frame), b.Encode(frame));
                });
                Nv12Rasters.Sha(b1).Should().Be(Nv12Rasters.Sha(a1), "a fresh encoder repeats the first access unit");
                Nv12Rasters.Sha(b2).Should().Be(Nv12Rasters.Sha(a2), "and the second");
                lines.Add($"au {name} full={full} q95 #1 {a1.Length} {Nv12Rasters.Sha(a1)}");
                lines.Add($"au {name} full={full} q95 #2 {a2.Length} {Nv12Rasters.Sha(a2)}");
            }
        }

        if (Environment.GetEnvironmentVariable("NV12_CAPTURE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(Path.Combine(dir, "nv12-au-sha.txt"), lines);
        var timing = new List<string> { $"host {Environment.MachineName} cpu-count {Environment.ProcessorCount} {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
            $"vector256-accelerated {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} avx2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}" };
        foreach (var name in new[] { "scene-telemetry-1920x480", "scene-telemetry-480x1920", "noise-1920x480", "noise-480x1920" })
        {
            var frame = rasters.Single(r => r.Name == name).Frame;
            foreach (var full in new[] { true, false })
            {
                timing.Add(Time($"ToNv12-product {name} full={full}", 200, () => MfH264IntraEncoder.ToNv12(frame, full)));
                timing.Add(Time($"ToNv12-oracle  {name} full={full}", 200, () => Nv12Oracle.ToNv12(frame, full)));
            }
        }

        foreach (var name in new[] { "scene-telemetry-480x1920", "scene-telemetry-1920x480" })
        {
            var frame = rasters.Single(r => r.Name == name).Frame;
            timing.Add(await Sta.RunAsync(() =>
            {
                using var encoder = new MfH264IntraEncoder(frame.Width, frame.Height, fullRange: true);
                return Time($"Encode-q95-full {name}", 60, () => encoder.Encode(frame));
            }));
        }

        await File.WriteAllLinesAsync(Path.Combine(dir, "nv12-timing.txt"), timing);
    }

    /// <summary>Warm-up then <paramref name="runs"/> timed calls on THIS thread; managed bytes allocated per call on this thread.</summary>
    private static string Time(string label, int runs, Func<byte[]> work)
    {
        for (var i = 0; i < 10; i++) work();
        var ms = new double[runs];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < runs; i++)
        {
            var start = Stopwatch.GetTimestamp();
            work();
            ms[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - allocated) / runs;
        Array.Sort(ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"{label}: runs {runs} min {ms[0]:F3} median {ms[runs / 2]:F3} p95 {ms[(int)(runs * 0.95)]:F3} max {ms[^1]:F3} mean {ms.Average():F3} ms; allocated/call {perCall} B");
    }
}
