using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Deferred fake RPCs deliberately exercise selection and draft races.
public sealed class ScenePackageWorkbenchTests
{
    [Fact]
    public async Task Reading_and_binding_drafts_do_not_apply_or_automap_hardware()
    {
        var service = new Packages(); var vm = New(service);
        service.Calls.Should().BeEmpty(); await SelectAsync(vm);
        vm.Roles.Should().HaveCount(2); vm.Roles.Should().OnlyContain(r => r.Selected!.Zone == null && r.Selected.ScreenId == null);
        vm.Roles[0].Selected = vm.Roles[0].Targets[1]; vm.Dirty.Should().BeTrue();
        service.Writes.Should().BeEmpty(); vm.CanApply.Should().BeFalse();
        vm.Roles[0].Targets.Should().HaveCount(2, "wrong-size lighting zones must not be offered");
    }

    [Fact]
    public async Task Saving_complete_bindings_is_separate_from_explicit_apply()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm); Bind(vm);
        await vm.SaveBindingsCommand.ExecuteAsync();
        service.Writes.Should().Equal("Save"); vm.CanApply.Should().BeTrue(); vm.Dirty.Should().BeFalse();
        await vm.ApplyCommand.ExecuteAsync(); service.Writes.Should().Equal("Save", "Apply"); vm.Message.Should().Contain("已应用");
    }

    [Fact]
    public async Task Failed_bindings_disable_apply_without_discarding_the_candidate()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm);
        vm.Roles[0].Selected = vm.Roles[0].Targets[1];
        await vm.SaveBindingsCommand.ExecuteAsync();
        vm.CanApply.Should().BeFalse(); vm.Dirty.Should().BeTrue(); vm.Message.Should().Contain("unbound");
        vm.Roles[0].Selected!.Zone.Should().NotBeNull(); service.Writes.Should().Equal("Save");
    }

    [Fact]
    public async Task Refresh_preserves_binding_draft_and_selected_package()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm); Bind(vm);
        var row = vm.Roles[0]; await vm.RefreshAsync();
        vm.Roles[0].Should().BeSameAs(row); vm.Dirty.Should().BeTrue(); vm.Selected!.Id.Should().Be("a");
        service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_with_a_dirty_binding_requires_the_existing_discard_choice()
    {
        var service = new Packages(); var vm = New(service, _ => false); await SelectAsync(vm); Bind(vm);
        vm.Selected = vm.Packages[1]; vm.Selected!.Id.Should().Be("a"); vm.Dirty.Should().BeTrue();
    }

    [Fact]
    public async Task Late_details_from_previous_selection_cannot_replace_new_package()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm);
        var gate = new TaskCompletionSource<ScenePackageDetailsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Read = id => id == "a" ? gate.Task : Task.FromResult(Packages.Detail(id));
        var old = vm.ReadSelectionAsync(); vm.Selected = vm.Packages[1]; await vm.ReadSelectionAsync();
        gate.SetResult(Packages.Detail("a")); await old;
        vm.Title.Should().Be("Package b"); vm.Selected!.Id.Should().Be("b");
    }

    [Fact]
    public async Task Selecting_new_package_disables_apply_while_details_are_loading()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm); Bind(vm); await vm.SaveBindingsCommand.ExecuteAsync();
        var gate = new TaskCompletionSource<ScenePackageDetailsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Read = _ => gate.Task; vm.Selected = vm.Packages[1];
        vm.CanApply.Should().BeFalse(); vm.Roles.Should().BeEmpty();
        gate.SetResult(Packages.Detail("b")); await vm.ReadSelectionAsync();
    }

    [Fact]
    public async Task New_edits_during_save_are_not_marked_saved_or_overwritten()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm); Bind(vm);
        var gate = new TaskCompletionSource<ScenePackageBindingStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Save = _ => gate.Task; var save = vm.SaveBindingsCommand.ExecuteAsync();
        vm.Roles[0].Selected = vm.Roles[0].Targets[0];
        gate.SetResult(new("a", "old", [], true, [])); await save;
        vm.Dirty.Should().BeTrue(); vm.Roles[0].Selected!.Zone.Should().BeNull(); vm.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Externally_changed_binding_prevents_stale_apply()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm); Bind(vm); await vm.SaveBindingsCommand.ExecuteAsync();
        service.State = service.State with { BindingId = "changed" };
        await vm.ApplyCommand.ExecuteAsync(); service.Writes.Should().Equal("Save"); vm.Dirty.Should().BeTrue(); vm.Message.Should().Contain("已变化");
    }

    [Fact]
    public async Task Apply_failure_is_visible_and_never_claims_success()
    {
        var service = new Packages { ApplyResult = new(false, "a", "saved", "wire budget exceeded") };
        var vm = New(service); await SelectAsync(vm); Bind(vm); await vm.SaveBindingsCommand.ExecuteAsync();
        await vm.ApplyCommand.ExecuteAsync(); vm.Message.Should().Contain("原场景保留").And.Contain("wire budget exceeded").And.NotContain("已应用");
    }

    [Fact]
    public async Task Disconnect_blocks_apply_and_preserves_binding_draft()
    {
        var service = new Packages(); var vm = New(service); await SelectAsync(vm); Bind(vm); vm.MarkDisconnected();
        vm.Available.Should().BeFalse(); vm.Dirty.Should().BeTrue(); vm.CanApply.Should().BeFalse();
        await vm.ApplyCommand.ExecuteAsync(); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_failure_reports_error_without_reusing_previous_details()
    {
        var service = new Packages(); var errors = new List<Exception>(); var vm = New(service, error: errors.Add); await SelectAsync(vm);
        service.Read = _ => Task.FromException<ScenePackageDetailsDto>(new ArgumentException("hash mismatch"));
        vm.Selected = vm.Packages[1]; await vm.ReadSelectionAsync();
        vm.HasDetails.Should().BeFalse(); vm.CanApply.Should().BeFalse(); vm.Message.Should().Contain("hash mismatch"); errors.Should().NotBeEmpty();
    }

    private static ScenePackagesViewModel New(Packages service, Func<string, bool>? confirm = null, Action<Exception>? error = null) => new(
        () => service,
        () => Task.FromResult<IReadOnlyList<ControllerDto>>([new("controller", "Generic", "Test", "Virtual", "test", "", [new(0, "two", "Linear", 2, 2, 1), new(1, "three", "Linear", 3, 3, 1)], 30, ControllerStates.Active, null)]),
        () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([new("screen:test", "Test screen", "H2", 480, 480, "Running", null, true, 0, 0, null, 5)]),
        () => null, error ?? (ex => throw ex), confirm);
    private static async Task SelectAsync(ScenePackagesViewModel vm) { await vm.RefreshAsync(); vm.Selected = vm.Packages[0]; await vm.ReadSelectionAsync(); }
    private static void Bind(ScenePackagesViewModel vm) { foreach (var row in vm.Roles) row.Selected = row.Targets[1]; }

    private sealed class Packages : IScenePackageService
    {
        public List<string> Calls { get; } = [];
        public List<string> Writes { get; } = [];
        public ScenePackageBindingStateDto State { get; set; } = new("a", null, [], false, ["unbound roles"]);
        public Func<string, Task<ScenePackageDetailsDto>>? Read { get; set; }
        public Func<ScenePackageBindingsDto, Task<ScenePackageBindingStateDto>>? Save { get; set; }
        public ScenePackageApplyDto ApplyResult { get; set; } = new(true, "a", "saved", "applied");
        private static ScenePackageInfoDto Info(string id) => new(id, id, "Package " + id, 1, "rgb24-hold-physical-ms-v1", 12000, 1, 1);
        public static ScenePackageDetailsDto Detail(string id) => new(Info(id), [new("light", "Lighting", "lighting", 2, 0, 0, 200, 12000, ["#FF0000", "#00FF00"]), new("display", "Screen", "screen", 0, 20, 20, 1, 12000, [])]);
        public Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default) { Calls.Add("List"); return Task.FromResult<IReadOnlyList<ScenePackageInfoDto>>([Info("a"), Info("b")]); }
        public Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default) { Calls.Add("Get:" + contentId); return Read?.Invoke(contentId) ?? Task.FromResult(Detail(contentId)); }
        public Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default) => Task.FromResult(State with { ContentId = contentId });
        public Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default) => throw new InvalidOperationException("Import not expected");
        public Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default)
        {
            Writes.Add("Save"); if (Save is not null) return Save(bindings);
            if (bindings.Bindings.Any(b => b.Zone is null && b.ScreenId is null)) return Task.FromResult(new ScenePackageBindingStateDto(bindings.ContentId, null, bindings.Bindings, false, ["unbound role"]));
            State = new(bindings.ContentId, "saved", bindings.Bindings, true, []); return Task.FromResult(State);
        }
        public Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default) { Writes.Add("Apply"); return Task.FromResult(ApplyResult); }
    }
}
