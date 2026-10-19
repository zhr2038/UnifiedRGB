using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Layout;

namespace UnifiedRgb.Core.Tests;

/// <summary>
/// Golden fixtures: tests/Golden/&lt;effect&gt;/&lt;layout&gt;-&lt;frame&gt;.bin, raw R,G,B bytes per LED, tolerance 0.
/// Fixtures were generated once from the ported generators (set URGB_UPDATE_GOLDEN=1 to regenerate after an
/// intentional, reviewed math change). A missing fixture fails the test.
/// </summary>
public class GoldenFrameTests
{
    public static TheoryData<string, string, int> Cases()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var g in EffectRegistry.Default.All)
        {
            foreach (var (name, _) in TestSupport.Layouts)
            {
                foreach (var f in TestSupport.GoldenFrames)
                {
                    data.Add(g.Id, name, f);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Frame_matches_golden_fixture(string effectId, string layoutName, int frame)
    {
        var gen = EffectRegistry.Default[effectId];
        var layout = TestSupport.Layouts.Single(l => l.Name == layoutName).Layout;
        var bytes = TestSupport.ToBytes(TestSupport.Render(gen, Projector.Project(layout), frame / TestSupport.Fps));

        var path = Path.Combine(TestSupport.RepoRoot(), "tests", "Golden", effectId, $"{layoutName}-{frame}.bin");
        if (Environment.GetEnvironmentVariable("URGB_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        File.Exists(path).Should().BeTrue($"golden fixture {path} must exist");
        bytes.Should().Equal(File.ReadAllBytes(path));
    }

    [Fact]
    public void Fixture_set_is_complete()
    {
        var root = Path.Combine(TestSupport.RepoRoot(), "tests", "Golden");
        Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Should().HaveCount(EffectRegistry.Default.All.Count * 4 * 3);
    }
}
