using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class ScenePackageAuthorKitTests
{
    [Fact]
    public async Task Published_authoring_example_passes_real_import_and_media_decode_without_applying()
    {
        var zip = File.ReadAllBytes(Pkg.FindUp(Path.Combine("assets", "scenes", "examples", "aurora-starter-v1.zip")));
        var parsed = ScenePackageReader.Read(zip);
        parsed.ContentId.Should().Be("17313811f9c4bdbca3857ac84653f06ce66658877783500a7f07506b0fe09710");
        var screens = new FakeScreens();
        await using var host = await TestHost.StartAsync(s => s.AddSingleton(screens.Platform));
        var rgb = host.Get<IRgbService>(); var packages = host.Get<IScenePackageService>();
        var before = await rgb.GetSceneAsync();
        var details = await packages.ImportScenePackageAsync(new ScenePackageUploadDto("authoring-example", Convert.ToBase64String(zip)));
        details.Info.Name.Should().Be("极光 · 入门示例");
        details.Roles.Should().HaveCount(2);
        details.Roles[0].LedCount.Should().Be(8); details.Roles[0].Frames.Should().Be(200);
        details.Roles[1].Width.Should().Be(64); details.Roles[1].Height.Should().Be(32); details.Roles[1].PreviewPngBase64.Should().NotBeNullOrEmpty();
        (await rgb.GetSceneAsync()).Should().BeEquivalentTo(before);
        screens.Opens.Should().Be(0);
    }
}
