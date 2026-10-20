using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

public sealed partial class ScenePackageWorkbenchTests
{
    private sealed class LibraryFake : ISceneLibraryService
    {
        public IReadOnlyList<string> References { get; set; } = [];
        public bool MissingMaintenance { get; set; }
        public List<ScenePackageRemovalRequestDto> Removed { get; } = [];
        public Task<SceneLibraryUsageDto> GetSceneLibraryUsageAsync(CancellationToken ct = default) => MissingMaintenance
            ? Task.FromException<SceneLibraryUsageDto>((StreamJsonRpc.RemoteMethodNotFoundException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(StreamJsonRpc.RemoteMethodNotFoundException)))
            : Task.FromResult(new SceneLibraryUsageDto(2, 32, 1048576, 1073741824, 0));
        public Task<ScenePackageRemovalPlanDto> InspectScenePackageRemovalAsync(string contentId, CancellationToken ct = default) => Task.FromResult(new ScenePackageRemovalPlanDto(contentId, "Generic scene", 100, References, []));
        public Task<ScenePackageRemovalDto> RemoveScenePackageAsync(ScenePackageRemovalRequestDto request, CancellationToken ct = default)
        { Removed.Add(request); return Task.FromResult(new ScenePackageRemovalDto(true, request.ContentId, 100, false, [], [], [], "removed")); }
        public Task<SceneProfileRemovalDto> RemoveSavedSceneAsync(string name, CancellationToken ct = default) => throw new InvalidOperationException("not expected");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Package_removal_checks_references_and_confirmation_before_any_write(bool confirm, bool referenced)
    {
        var package = new Packages(); var library = new LibraryFake { References = referenced ? ["已保存场景：“Keep”"] : [] };
        var confirmations = 0;
        var vm = new ScenePackagesViewModel(() => package, () => Task.FromResult<IReadOnlyList<ControllerDto>>([]),
            () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([]), () => null, ex => throw ex,
            _ => { confirmations++; return confirm; }, () => library);
        await SelectAsync(vm);
        vm.CleanRelatedMedia.Should().BeFalse(); vm.Usage.Should().Contain("2 / 32");
        await vm.RemoveCommand.ExecuteAsync();
        library.Removed.Should().HaveCount(confirm && !referenced ? 1 : 0);
        confirmations.Should().Be(referenced ? 0 : 1);
        package.Writes.Should().BeEmpty("library maintenance must never apply a scene");
        if (referenced) vm.Message.Should().Contain("Keep");
    }

    [Fact]
    public async Task A_dirty_binding_disables_removal_and_cleanup_is_an_explicit_choice()
    {
        var package = new Packages(); var library = new LibraryFake();
        var vm = new ScenePackagesViewModel(() => package, () => Task.FromResult<IReadOnlyList<ControllerDto>>([]),
            () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([]), () => null, ex => throw ex, _ => true, () => library);
        await SelectAsync(vm);
        vm.CleanRelatedMedia = true;
        await vm.RemoveCommand.ExecuteAsync();
        library.Removed.Single().CleanUnreferencedMedia.Should().BeTrue();
        // A user binding draft is protected; setting any role selection is enough to dirty it.
        vm.Roles[0].Selected = new("draft", "draft");
        vm.Dirty.Should().BeTrue(); vm.RemoveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Older_Hosts_keep_the_library_readable_but_do_not_offer_unavailable_maintenance()
    {
        var package = new Packages(); var library = new LibraryFake { MissingMaintenance = true };
        var vm = new ScenePackagesViewModel(() => package, () => Task.FromResult<IReadOnlyList<ControllerDto>>([]),
            () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([]), () => null, ex => throw ex, _ => true, () => library);
        await SelectAsync(vm);
        vm.Packages.Should().NotBeEmpty(); vm.MaintenanceAvailable.Should().BeFalse(); vm.Usage.Should().Contain("更新后台");
        vm.RemoveCommand.CanExecute(null).Should().BeFalse();
        await vm.RemoveCommand.ExecuteAsync(); library.Removed.Should().BeEmpty();
        library.MissingMaintenance = false; await vm.RefreshAsync();
        vm.MaintenanceAvailable.Should().BeTrue(); vm.RemoveCommand.CanExecute(null).Should().BeTrue();
    }
}
