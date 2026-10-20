using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

public sealed partial class ScenePackageHostTests
{
    [Fact]
    public async Task Checked_apply_never_uses_a_later_binding_and_the_legacy_entry_keeps_its_current_binding_semantics()
    {
        await using var rig = await StartAsync(null, new PackageLoopAdapter("pkg:2"));
        var (cid, oldBinding, _) = await ImportBoundAsync(rig, Pkg.Lighting());
        var newer = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip", "pkg:2")));
        newer.Ready.Should().BeTrue(); newer.BindingId.Should().NotBe(oldBinding);
        var before = rig.Engine.Loop.Scene;
        var rejected = await rig.Packages.ApplyScenePackageCheckedAsync(new(cid, oldBinding));
        rejected.Applied.Should().BeFalse(); rejected.BindingId.Should().Be(newer.BindingId); rejected.Detail.Should().Contain("bindings changed");
        rig.Engine.Loop.Scene.Should().BeSameAs(before);
        var fresh = await rig.Packages.ApplyScenePackageCheckedAsync(new(cid, newer.BindingId!));
        fresh.Applied.Should().BeTrue(fresh.Detail); rig.Engine.Loop.Scene.Package!.BindingId.Should().Be(newer.BindingId);
        rig.Engine.Loop.Scene.Assignments.Single().Targets.Should().OnlyContain(t => t.Controller.Value == "pkg:2");
        var latest = await rig.Packages.SaveScenePackageBindingsAsync(Bind(cid, Zone("strip")));
        var legacy = await rig.Packages.ApplyScenePackageAsync(cid);
        legacy.Applied.Should().BeTrue(); legacy.BindingId.Should().Be(latest.BindingId);
    }

    [Theory]
    [InlineData("bad", "bad")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "")]
    public async Task Checked_apply_refuses_malformed_ids_before_publication(string content, string binding)
    {
        await using var rig = await StartAsync(); var before = rig.Engine.Loop.Scene;
        await FluentActions.Awaiting(() => rig.Packages.ApplyScenePackageCheckedAsync(new(content, binding))).Should().ThrowAsync<ArgumentException>();
        rig.Engine.Loop.Scene.Should().BeSameAs(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_backlight_sleep_is_output_ready_only_with_the_existing_control_verification(bool verified)
    {
        await using var rig = await StartAsync();
        var svc = rig.Host.Get<ScenePackageService>();
        var device = new ScreenDeviceDto("screen:tl-lcd:test", "Test", "TlLcd", 400, 400, ScreenStates.Sleeping, null, verified, 1, 0, null, 0);
        var result = svc.ScreenUnavailable(device);
        if (verified) result.Should().BeNull(); else result.Should().Contain("not control-verified");
        svc.ScreenUnavailable(device with { State = ScreenStates.Disabled }).Should().Contain("Disabled");
        svc.ScreenUnavailable(device with { State = ScreenStates.Error }).Should().Contain("Error");
    }
}
