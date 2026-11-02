using System.Reflection;
using UnifiedRgb.App.Diagnostics;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Tests deliberately complete held fake RPCs out of order.

/// <summary>Headless command and stale-response checks; no production pipe, file dialog or hardware service.</summary>
public sealed class UiFunctionalAuditTests
{
    [Fact]
    public void Failed_autostart_persistence_keeps_the_last_confirmed_setting_and_allows_a_retry()
    {
        var registry = new FailingAutostartRegistry();
        var vm = new SettingsViewModel(registry, "unused-app.exe");
        vm.Invoking(v => v.StartWithWindows = true).Should().Throw<IOException>();
        vm.StartWithWindows.Should().BeFalse();
        registry.Fail = false;
        vm.StartWithWindows = true;
        vm.StartWithWindows.Should().BeTrue();
        registry.Writes.Should().Be(2, "a failed save must not leave the local flag preventing another attempt");
    }

    [Fact]
    public void Diagnostic_export_ignores_old_temp_files_and_cleans_its_own_failed_attempt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "urgb-ui-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "diagnostics.zip");
        var stale = path + ".tmp";
        File.WriteAllText(stale, "old attempt");
        try
        {
            DiagnosticsExporter.Export(directory, directory, path).Should().BeEmpty();
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(stale).Should().Be("old attempt");
            var blocked = Path.Combine(directory, "blocked.zip");
            Directory.CreateDirectory(blocked);
            ((Action)(() => DiagnosticsExporter.Export(directory, directory, blocked))).Should().Throw<Exception>()
                .Where(ex => ex is IOException || ex is UnauthorizedAccessException);
            Directory.EnumerateFiles(directory, "blocked.zip.*.tmp").Should().BeEmpty();
            Directory.Delete(blocked);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var subdirectory in Directory.EnumerateDirectories(directory)) Directory.Delete(subdirectory);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Disabled_async_ICommand_execution_rechecks_the_parameter()
    {
        var calls = 0;
        var enabled = false;
        var command = new AsyncCommand(_ => { calls++; return Task.CompletedTask; }, ex => throw ex,
            parameter => enabled && Equals(parameter, "accepted"));

        command.Execute("accepted");
        enabled = true;
        command.Execute("wrong");
        calls.Should().Be(0);
        await command.ExecuteAsync("accepted");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Firmware_confirmation_cannot_authorize_an_observation_from_a_lost_connection()
    {
        var service = new FakeFirmwareService();
        FirmwareUpdateViewModel? vm = null;
        vm = new(() => service, InlineDispatcher.Instance, () => null, _ => { vm!.SetConnected(false); return true; }, _ => { });
        await vm.RefreshAsync();
        vm.CanPrepare.Should().BeTrue();
        await vm.PrepareCommand.ExecuteAsync();
        service.Writes.Should().Be(0);
        vm.CanPrepare.Should().BeFalse();
    }

    [Fact]
    public void Disabled_relay_commands_also_refuse_programmatic_execution()
    {
        var calls = 0;
        var command = new RelayCommand(_ => calls++, parameter => Equals(parameter, "accepted"));
        command.Execute("wrong");
        calls.Should().Be(0);
        command.Execute("accepted");
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_old_firmware_read_failure_cannot_invalidate_a_newer_success(bool unsupported)
    {
        var old = new TaskCompletionSource<FirmwareJobStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeFirmwareService { Read = call => call == 1 ? old.Task : Task.FromResult<FirmwareJobStatus?>(Loaded()) };
        var vm = Firmware(service);
        var pending = vm.RefreshAsync();
        await vm.RefreshAsync();
        vm.CanPrepare.Should().BeTrue();
        var current = vm.Status;
        var message = vm.Message;

        old.SetException(unsupported ? new NotSupportedException("old host") : new IOException("old connection"));
        await pending;

        vm.Status.Should().BeSameAs(current);
        vm.Message.Should().Be(message);
        vm.CanPrepare.Should().BeTrue("a superseded failure must not revoke the newer observation");
    }

    [Fact]
    public async Task Firmware_reads_do_not_supersede_a_pending_mutation_and_disabled_install_sends_nothing()
    {
        var preparation = new TaskCompletionSource<FirmwareJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeFirmwareService { Prepare = () => preparation.Task };
        var vm = Firmware(service);
        await vm.RefreshAsync();
        await vm.InstallCommand.ExecuteAsync();
        service.Writes.Should().Be(0, "the Loaded phase does not authorize installation");

        var pending = vm.PrepareCommand.ExecuteAsync();
        service.Writes.Should().Be(1);
        await vm.RefreshAsync();
        service.Reads.Should().Be(1, "a read cannot clear the admission of an unresolved mutation");
        preparation.SetException(new IOException("unknown request result"));
        await pending;
        vm.Message.Should().Contain("请求结果未确认");
        vm.CanPrepare.Should().BeFalse();

        await vm.RefreshAsync();
        vm.CanPrepare.Should().BeTrue("an explicit fresh read resolves uncertainty without resending");
        service.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Direct_firmware_load_is_refused_while_a_persistent_job_is_running()
    {
        var service = new FakeFirmwareService { Read = _ => Task.FromResult<FirmwareJobStatus?>(Loaded() with { Running = true, Phase = FirmwarePhase.Installing }) };
        var picks = 0;
        var vm = new FirmwareUpdateViewModel(() => service, InlineDispatcher.Instance,
            () => { picks++; return "unused-package"; }, _ => true, _ => { });
        await vm.RefreshAsync();
        vm.CanLoad.Should().BeFalse();
        await vm.LoadAsync();
        picks.Should().Be(0);
        service.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_firmware_file_picker_cannot_continue_loading_after_its_connection_changes()
    {
        var service = new FakeFirmwareService();
        FirmwareUpdateViewModel? vm = null;
        vm = new(() => service, InlineDispatcher.Instance,
            () => { vm!.SetConnected(false); return "unused-package"; }, _ => true, _ => { });
        await vm.LoadAsync();
        service.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Saving_a_profile_uses_the_name_captured_when_the_action_started()
    {
        await using var client = new PipeClient("unused-ui-save-" + Guid.NewGuid().ToString("N"));
        var scene = new TaskCompletionSource<SceneDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = DispatchProxy.Create<IRgbService, ProfileProxy>();
        var fake = (ProfileProxy)proxy;
        fake.ReadScene = () => scene.Task;
        typeof(PipeClient).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, proxy);
        typeof(PipeClient).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, ConnectionState.Connected);
        var vm = new ProfilesViewModel(client, InlineDispatcher.Instance, ex => throw ex) { NewName = "requested name" };
        var save = vm.SaveAsync();
        vm.NewName = "later draft";
        scene.SetResult(new([]));
        await save;
        fake.Saved.Should().ContainSingle().Which.Name.Should().Be("requested name");
        vm.NewName.Should().Be("later draft");
    }

    [Fact]
    public async Task Old_profile_preview_failures_do_not_overwrite_the_new_selection()
    {
        await using var client = new PipeClient("unused-ui-audit-" + Guid.NewGuid().ToString("N"));
        var old = new TaskCompletionSource<ProfileDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = DispatchProxy.Create<IRgbService, ProfileProxy>();
        ((ProfileProxy)proxy).Load = name => name == "old" ? old.Task : Task.FromResult(new ProfileDto(name, new([])));
        typeof(PipeClient).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, proxy);
        typeof(PipeClient).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, ConnectionState.Connected);
        var dispatcher = new ObservedDispatcher();
        var vm = new ProfilesViewModel(client, dispatcher, ex => throw ex);
        vm.Selected = new("old", DateTimeOffset.UtcNow);
        vm.Selected = new("current", DateTimeOffset.UtcNow);
        var current = vm.Summary;

        old.SetException(new IOException("stale preview error"));
        await dispatcher.SecondPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Selected!.Name.Should().Be("current");
        vm.Summary.Should().Be(current).And.NotContain("stale preview error");
    }

    private static FirmwareUpdateViewModel Firmware(FakeFirmwareService service) =>
        new(() => service, InlineDispatcher.Instance, () => null, _ => true, _ => { });

    private static FirmwareJobStatus Loaded() => new(Guid.NewGuid(), 1,
        new("fake-release", "Fake firmware", "1", "", "bundle", "candidate", "rollback", true, "fixture", true),
        FirmwarePhase.Loaded, "fixture", false, false, true, 0, "fixture", "unused", null, null, DateTimeOffset.UtcNow);

    private sealed class FakeFirmwareService : IFirmwareUpdateService
    {
        public Func<int, Task<FirmwareJobStatus?>> Read { get; init; } = _ => Task.FromResult<FirmwareJobStatus?>(Loaded());
        public Func<Task<FirmwareJobStatus>> Prepare { get; init; } = () => Task.FromResult(Loaded());
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Task<FirmwareJobStatus?> GetFirmwareJobAsync(Guid? jobId = null, CancellationToken ct = default) => Read(++Reads);
        public Task<FirmwareJobStatus> PrepareFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) { Writes++; return Prepare(); }
        private Task<FirmwareJobStatus> Write() { Writes++; return Task.FromResult(Loaded()); }
        public Task<FirmwareJobStatus> LoadFirmwarePackageAsync(string path, CancellationToken ct = default) => Write();
        public Task<FirmwareJobStatus> UseRetainedFirmwareBaselineAsync(FirmwareRequest request, CancellationToken ct = default) => Write();
        public Task<FirmwareJobStatus> BackupFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Write();
        public Task<FirmwareJobStatus> InstallFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Write();
        public Task<FirmwareJobStatus> RestoreFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Write();
        public Task<FirmwareJobStatus> CancelFirmwareIfSafeAsync(FirmwareRequest request, CancellationToken ct = default) => Write();
        public Task<FirmwareJobStatus> FinishFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Write();
    }

    public class ProfileProxy : DispatchProxy
    {
        public Func<string, Task<ProfileDto>> Load { get; set; } = _ => throw new InvalidOperationException("No fixture profile.");
        public Func<Task<SceneDto>> ReadScene { get; set; } = () => throw new InvalidOperationException("No fixture scene.");
        public List<ProfileDto> Saved { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IRgbService.LoadProfileAsync): return Load((string)args![0]!);
                case nameof(IRgbService.GetSceneAsync): return ReadScene();
                case nameof(IRgbService.SaveProfileAsync): Saved.Add((ProfileDto)args![0]!); return Task.CompletedTask;
                case nameof(IRgbService.ListProfilesAsync): return Task.FromResult<IReadOnlyList<ProfileInfoDto>>([]);
                default: throw new InvalidOperationException("Unexpected RPC in profile-only test.");
            }
        }
    }

    private sealed class ObservedDispatcher : IUiDispatcher
    {
        private int _posts;
        public TaskCompletionSource SecondPost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Post(Action action)
        {
            action();
            if (Interlocked.Increment(ref _posts) == 2) SecondPost.TrySetResult();
        }
    }

    private sealed class FailingAutostartRegistry : IAutostartRegistry
    {
        public bool Fail { get; set; } = true;
        public int Writes { get; private set; }
        public string? Get() => null;
        public void Set(string? command)
        {
            Writes++;
            if (Fail) throw new IOException("fake registry write failure");
        }
    }
}
