namespace UnifiedRgb.Host.Tests;

public sealed partial class ScenePackageWorkbenchTests
{
    [Fact]
    public async Task An_older_Host_gets_specific_guidance_and_never_an_unchecked_apply_fallback()
    {
        var service = new Packages { MissingCheckedApply = true }; var vm = New(service);
        await SelectAsync(vm); Bind(vm); await vm.SaveBindingsCommand.ExecuteAsync();
        var applied = 0; vm.Applied += (_, _) => applied++;
        await vm.ApplyCommand.ExecuteAsync();
        vm.Message.Should().Contain("当前后台版本不支持绑定版本校验").And.Contain("刷新");
        vm.CanApply.Should().BeFalse(); service.UncheckedCalls.Should().Be(0); applied.Should().Be(0);
        vm.Dirty.Should().BeFalse("the checked binding draft is kept intact");
    }

    [Fact]
    public async Task Refresh_after_Host_update_reenables_checked_apply_without_applying_implicitly()
    {
        var service = new Packages { MissingCheckedApply = true }; var vm = New(service);
        await SelectAsync(vm); Bind(vm); await vm.SaveBindingsCommand.ExecuteAsync();
        await vm.ApplyCommand.ExecuteAsync(); var writes = service.Writes.Count;
        service.MissingCheckedApply = false;
        await vm.RefreshAsync();
        vm.CanApply.Should().BeTrue(); service.Writes.Should().HaveCount(writes);
        await vm.ApplyCommand.ExecuteAsync();
        vm.Message.Should().Contain("已应用"); service.UncheckedCalls.Should().Be(0);
    }
}
