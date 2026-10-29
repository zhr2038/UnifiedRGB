using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class PeripheralConfigurationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "urgb-peripheral-config-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Enter_is_sticky_and_exit_resumes_fresh_resources_without_preference_mutation()
    {
        var (service, resources, _) = Make();
        var settings = WritePeripheralFile("keyboard-display.json", "{\"Schema\":1,\"Enabled\":true}"u8.ToArray());
        var journal = WritePeripheralFile("keyboard-display-session.json", "recovery"u8.ToArray());
        var settingsBefore = File.ReadAllBytes(settings);
        resources.Release = _ => { File.Delete(journal); resources.DisposedKeyboardLightingMouseReceiverLeases = 4; return Task.CompletedTask; };

        var entered = await service.EnterPeripheralConfigurationAsync();
        entered.State.Should().Be("Ready");
        entered.ConfigurationMode.Should().BeTrue();
        entered.InterfacesReleased.Should().BeTrue();
        resources.DisposedKeyboardLightingMouseReceiverLeases.Should().Be(4);
        File.Exists(MarkerPath()).Should().BeTrue();
        File.ReadAllBytes(settings).Should().Equal(settingsBefore);
        File.Exists(journal).Should().BeFalse();

        var exited = await service.ExitPeripheralConfigurationAsync();
        exited.State.Should().Be("Active");
        exited.ConfigurationMode.Should().BeFalse();
        resources.FreshSessionSnapshots.Should().Be(1);
        File.Exists(MarkerPath()).Should().BeFalse();
        File.ReadAllBytes(settings).Should().Equal(settingsBefore);
    }

    [Fact]
    public async Task Valid_marker_is_loaded_before_rog_open_and_corrupt_marker_fails_closed_for_rog_only()
    {
        var (first, resources, _) = Make();
        (await first.EnterPeripheralConfigurationAsync()).State.Should().Be("Ready");
        var (restarted, restartedResources, restartedGate) = Make();
        restartedGate.RogAcquisitionAllowed.Should().BeFalse();
        (await restarted.GetPeripheralConfigurationStatusAsync()).InterfacesReleased.Should().BeTrue();
        restartedResources.RogOpens.Should().Be(0);

        File.WriteAllText(MarkerPath(), "not-json");
        var (corrupt, corruptResources, corruptGate) = Make();
        corruptGate.RogAcquisitionAllowed.Should().BeFalse();
        var status = await corrupt.GetPeripheralConfigurationStatusAsync();
        status.State.Should().Be("Error");
        status.ConfigurationMode.Should().BeTrue();
        status.InterfacesReleased.Should().BeFalse();
        status.Detail.Should().Contain("fail-closed");
        corruptResources.RogOpens.Should().Be(0);
        (await corrupt.ExitPeripheralConfigurationAsync()).State.Should().Be("Active");
        corruptResources.FreshSessionSnapshots.Should().Be(1);
    }

    [Fact]
    public async Task Restart_with_sticky_marker_and_recovery_journal_is_error_until_explicit_wired_recovery()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath())!);
        File.WriteAllText(MarkerPath(), "{\"Schema\":1,\"Requested\":true}");
        WritePeripheralFile("keyboard-display-session.json", "schema2-recovery"u8.ToArray());
        var (service, resources, gate) = Make();
        var status = await service.GetPeripheralConfigurationStatusAsync();
        status.State.Should().Be("Error");
        status.InterfacesReleased.Should().BeFalse();
        status.Detail.Should().Contain("explicit Enter");
        gate.RogAcquisitionAllowed.Should().BeFalse();
        resources.RogOpens.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_and_cancelled_requests_are_serialized_without_abandoning_cleanup()
    {
        var (service, resources, _) = Make();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resources.Release = async _ =>
        {
            entered.SetResult();
            await finish.Task;
            resources.DisposedKeyboardLightingMouseReceiverLeases = 4;
        };
        var entering = service.EnterPeripheralConfigurationAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var waitingExit = service.ExitPeripheralConfigurationAsync(cancelled.Token);
        cancelled.Cancel();
        await waitingExit.Invoking(async t => await t).Should().ThrowAsync<OperationCanceledException>();
        (await service.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Entering");
        finish.SetResult();
        (await entering).State.Should().Be("Ready");
        resources.ReleaseCalls.Should().Be(1);
        resources.ResumeCalls.Should().Be(0);

        var duplicate = await Task.WhenAll(service.EnterPeripheralConfigurationAsync(), service.EnterPeripheralConfigurationAsync());
        duplicate.Should().OnlyContain(s => s.State == "Ready");
        resources.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task Sticky_timeout_keeps_marker_and_recovery_and_never_claims_release()
    {
        var (service, resources, gate) = Make(TimeSpan.FromMilliseconds(50));
        resources.Release = async ct =>
        {
            resources.ReleaseCalls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var journal = WritePeripheralFile("keyboard-display-session.json", "schema2-recovery"u8.ToArray());
        var before = File.ReadAllBytes(journal);
        var status = await service.EnterPeripheralConfigurationAsync();
        status.State.Should().Be("Error");
        status.ConfigurationMode.Should().BeTrue();
        status.InterfacesReleased.Should().BeFalse();
        status.Detail.Should().Contain("timed out");
        gate.RogAcquisitionAllowed.Should().BeFalse();
        File.Exists(MarkerPath()).Should().BeTrue();
        File.ReadAllBytes(journal).Should().Equal(before);
        resources.ResumeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Offline_or_asleep_restoration_failure_is_truthful_and_retains_recovery()
    {
        var (service, resources, _) = Make();
        resources.Release = _ => throw new IOException("mouse asleep; partial restore");
        var journal = WritePeripheralFile("keyboard-display-session.json", "recovery"u8.ToArray());
        var before = File.ReadAllBytes(journal);
        var status = await service.EnterPeripheralConfigurationAsync();
        status.State.Should().Be("Error");
        status.InterfacesReleased.Should().BeFalse();
        status.Detail.Should().Contain("offline, asleep");
        File.Exists(MarkerPath()).Should().BeTrue();
        File.ReadAllBytes(journal).Should().Equal(before);
    }

    [Fact]
    public void Frozen_contract_has_source_generated_pipe_metadata()
    {
        var dto = new PeripheralConfigurationStatusDto(true, "Ready", "released", true, DateTimeOffset.UtcNow, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, ContractsJsonContext.Default.PeripheralConfigurationStatusDto);
        JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.PeripheralConfigurationStatusDto).Should().Be(dto);
        typeof(IPeripheralConfigurationService).GetMethods().Should().HaveCount(3);
    }

    private (PeripheralConfigurationService Service, FakeResources Resources, PeripheralConfigurationGate Gate) Make(TimeSpan? timeout = null)
    {
        var options = new UnifiedRgbOptions { DataDirectory = _directory };
        var gate = new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load());
        var resources = new FakeResources();
        return (new PeripheralConfigurationService(options, gate, resources, timeout), resources, gate);
    }

    private string MarkerPath() => Path.Combine(_directory, "peripherals", "peripheral-configuration.json");

    private string WritePeripheralFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, "peripherals", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class FakeResources : IPeripheralConfigurationResources
    {
        public int ReleaseCalls;
        public int ResumeCalls;
        public int DisposedKeyboardLightingMouseReceiverLeases;
        public int FreshSessionSnapshots;
        public int RogOpens;
        public Func<CancellationToken, Task>? Release;

        public async Task ReleaseAsync(CancellationToken ct)
        {
            ReleaseCalls++;
            if (Release is not null) await Release(ct);
            else DisposedKeyboardLightingMouseReceiverLeases = 4;
        }

        public Task ResumeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ResumeCalls++;
            FreshSessionSnapshots++;
            RogOpens++;
            return Task.CompletedTask;
        }
    }
}
