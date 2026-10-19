using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class ScenePackageTransactionReviewTests
{
    [Fact]
    public async Task A_refused_wire_budget_cannot_unblock_the_previous_shared_timeline()
    {
        var adapter = new PackageLoopAdapter();
        await using var host = await TestHost.StartAsync(s => s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(adapter)));
        var packages = host.Get<IScenePackageService>();
        var imported = await packages.ImportScenePackageAsync(new("review", Convert.ToBase64String(Pkg.Lighting())));
        var id = imported.Info.ContentId;
        var binding = await packages.SaveScenePackageBindingsAsync(new(id, [new("strip", new("pkg:1", 0))]));
        (await packages.ApplyScenePackageAsync(id)).Applied.Should().BeTrue();
        var timeline = host.Get<ScenePackageTimeline>(); var key = ScenePackageIds.TimelineIdOf(id, binding.BindingId!);
        timeline.Block(key, "existing media hold");
        adapter.Fits = false;
        var before = await host.Get<IRgbService>().GetSceneAsync();
        (await packages.ApplyScenePackageAsync(id)).Applied.Should().BeFalse();
        (await host.Get<IRgbService>().GetSceneAsync()).Should().BeEquivalentTo(before);
        timeline.Position(key)!.Blocked.Should().Be("existing media hold", "a refused candidate must not publish its timeline or release the previous screen hold");
        adapter.Fits = true;
        (await packages.ApplyScenePackageAsync(id)).Applied.Should().BeTrue();
        timeline.Position(key)!.Blocked.Should().BeNull("only an accepted candidate may release the old hold");
    }
}
