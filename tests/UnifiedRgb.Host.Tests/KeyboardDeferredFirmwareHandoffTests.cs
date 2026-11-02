using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class KeyboardDeferredFirmwareHandoffTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "urgb-rf-firmware-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Deferred_USB_recovery_prevents_firmware_ready_even_if_resource_release_reports_success()
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var resources = new Resources();
        var service = new PeripheralConfigurationService(new() { DataDirectory = _directory }, gate, resources);
        WritePending();
        await ((Func<Task>)(() => service.AcquireAsync(Guid.NewGuid(), default))).Should().ThrowAsync<IOException>();
        var status = await service.GetPeripheralConfigurationStatusAsync();
        status.State.Should().Be("Error");
        status.InterfacesReleased.Should().BeFalse();
        File.Exists(PendingPath).Should().BeTrue();
        resources.Resumes.Should().Be(0);
    }

    [Fact]
    public async Task A_deferred_obligation_appearing_after_release_prevents_finish_and_fresh_resume()
    {
        using var gate = new PeripheralConfigurationGate(new(false, null));
        var resources = new Resources();
        var service = new PeripheralConfigurationService(new() { DataDirectory = _directory }, gate, resources);
        var job = Guid.NewGuid();
        await service.AcquireAsync(job, default);
        (await service.GetPeripheralConfigurationStatusAsync()).InterfacesReleased.Should().BeTrue();
        WritePending();
        await ((Func<Task>)(() => service.FinishAsync(job, true, true, default))).Should().ThrowAsync<InvalidOperationException>();
        resources.Resumes.Should().Be(0);
        File.Exists(PendingPath).Should().BeTrue();
        gate.RogAcquisitionAllowed.Should().BeFalse();
    }

    private string PendingPath => Path.Combine(_directory, "peripherals", "keyboard-display-session.json.deferred-wired");
    private void WritePending()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
        File.WriteAllText(PendingPath, "retained exact transport recovery obligation");
    }
    private sealed class Resources : IPeripheralConfigurationResources
    {
        public int Resumes { get; private set; }
        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct) { Resumes++; return Task.CompletedTask; }
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(_directory);
        if (!resolved.StartsWith(Path.Combine(Path.GetTempPath(), "urgb-rf-firmware-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
