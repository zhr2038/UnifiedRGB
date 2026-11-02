using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class PeripheralDeferredRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "urgb-deferred-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restarted_handoff_counts_deferred_recovery_and_blocks_exit_without_resource_calls()
    {
        var options = new UnifiedRgbOptions { DataDirectory = _directory };
        var gate = new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load());
        var resources = new NoopResources();
        var first = new PeripheralConfigurationService(options, gate, resources);
        (await first.EnterPeripheralConfigurationAsync()).State.Should().Be("Ready");
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        File.WriteAllText(Path.Combine(_directory, "peripherals", "keyboard-display-session.json.deferred-wired"), "pending");
        var restarted = new PeripheralConfigurationService(options, new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load()), resources);
        (await restarted.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Error");
        (await restarted.ExitPeripheralConfigurationAsync()).State.Should().Be("Error");
        resources.Resumes.Should().Be(0);
    }

    [Fact]
    public async Task Release_success_without_clearing_deferred_obligation_cannot_publish_Ready()
    {
        var options = new UnifiedRgbOptions { DataDirectory = _directory };
        var service = new PeripheralConfigurationService(options, new PeripheralConfigurationGate(PeripheralConfigurationMarker.For(options).Load()), new NoopResources());
        Directory.CreateDirectory(Path.Combine(_directory, "peripherals"));
        File.WriteAllText(Path.Combine(_directory, "peripherals", "keyboard-display-session.json.deferred-wireless"), "pending");
        var status = await service.EnterPeripheralConfigurationAsync();
        status.State.Should().Be("Error"); status.InterfacesReleased.Should().BeFalse();
    }

    private sealed class NoopResources : IPeripheralConfigurationResources
    {
        public int Resumes { get; private set; }
        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct) { Resumes++; return Task.CompletedTask; }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
