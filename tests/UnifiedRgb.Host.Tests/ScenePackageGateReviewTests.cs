using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Tests;

/// <summary>Codex independent whole-candidate admission checks; fake transports only.</summary>
[Collection("Host")]
public sealed class ScenePackageGateReviewTests
{
    [Fact]
    public async Task Disabled_screen_output_refuses_the_whole_combined_package_before_RGB_changes()
    {
        var adapter = new PackageLoopAdapter(); var screens = new FakeScreens();
        await using var host = await TestHost.StartAsync(s => { s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(adapter)); s.AddSingleton(screens.Platform); });
        var packages = host.Get<IScenePackageService>(); var rgb = host.Get<IRgbService>();
        var imported = await packages.ImportScenePackageAsync(new("generic", Convert.ToBase64String(Pkg.GenericFixture())));
        var id = imported.Info.ContentId;
        var state = await packages.SaveScenePackageBindingsAsync(new(id, [new("strip", new("pkg:1", 0)), new("panel", ScreenId: FakeScreens.Tl.Id)]));
        var before = await rgb.GetSceneAsync();
        var applied = await packages.ApplyScenePackageAsync(id);
        applied.Applied.Should().BeFalse("a discovered screen with disabled output cannot silently produce a partial RGB-only show");
        state.Ready.Should().BeFalse(); (await rgb.GetSceneAsync()).Should().BeEquivalentTo(before);
        screens.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Paused_native_writer_refuses_package_instead_of_reporting_a_partial_show_as_applied()
    {
        var adapter = new PackageLoopAdapter();
        await using var host = await TestHost.StartAsync(s => s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(adapter)));
        var packages = host.Get<IScenePackageService>(); var rgb = host.Get<IRgbService>();
        var imported = await packages.ImportScenePackageAsync(new("generic-rgb", Convert.ToBase64String(Pkg.Lighting())));
        var id = imported.Info.ContentId;
        await packages.SaveScenePackageBindingsAsync(new(id, [new("strip", new("pkg:1", 0))]));
        var writer = host.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id.Value == "pkg:1").Writer!;
        writer.Paused = true;
        try
        {
            var before = await rgb.GetSceneAsync(); var applied = await packages.ApplyScenePackageAsync(id);
            applied.Applied.Should().BeFalse("an open but paused writer is contended, not ready for an all-target package");
            (await rgb.GetSceneAsync()).Should().BeEquivalentTo(before);
        }
        finally { writer.Paused = false; }
    }

    [Fact]
    public async Task Raw_scene_or_profile_application_cannot_bypass_package_writer_readiness()
    {
        var adapter = new PackageLoopAdapter();
        await using var host = await TestHost.StartAsync(s => s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(adapter)));
        var packages = host.Get<IScenePackageService>(); var rgb = host.Get<IRgbService>();
        var imported = await packages.ImportScenePackageAsync(new("generic-rgb", Convert.ToBase64String(Pkg.Lighting())));
        var id = imported.Info.ContentId;
        var binding = await packages.SaveScenePackageBindingsAsync(new(id, [new("strip", new("pkg:1", 0))]));
        var scene = new SceneDto([new AssignmentDto([new("pkg:1", 0)], "StaticColor", [], Strategy: "Frames", FrameSetId: ScenePackageIds.TimelineIdOf(id, binding.BindingId!))], Package: new(id, binding.BindingId!, Pkg.Contract));
        await rgb.SaveProfileAsync(new("readiness review", scene));
        var writer = host.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id.Value == "pkg:1").Writer!;
        writer.Paused = true;
        try
        {
            var before = await rgb.GetSceneAsync();
            await ((Func<Task>)(() => rgb.SetSceneAsync(scene))).Should().ThrowAsync<ArgumentException>();
            await ((Func<Task>)(() => rgb.ApplyProfileAsync("readiness review"))).Should().ThrowAsync<ArgumentException>();
            (await rgb.GetSceneAsync()).Should().BeEquivalentTo(before);
        }
        finally { writer.Paused = false; }
    }
}
