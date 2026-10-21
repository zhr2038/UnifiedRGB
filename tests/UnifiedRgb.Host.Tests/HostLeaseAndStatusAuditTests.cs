using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class HostLeaseAndStatusAuditTests
{
    [Fact]
    public void Maintenance_lease_refusal_is_a_single_message_and_does_not_acquire_hardware()
    {
        var name = "Local\\urgb-audit-maintenance-" + Guid.NewGuid().ToString("N");
        using var owner = new HardwareHostLease(name);
        HardwareHostLease.TryAcquire(out var other, out var failure, name).Should().BeFalse();
        other.Should().BeNull();
        failure.Should().Contain("Another Host owns the hardware").And.NotContain(Environment.NewLine);
    }

    [Fact]
    public void Unclean_shutdown_keeps_ownership_even_if_the_lease_scope_is_disposed()
    {
        var name = "Local\\urgb-audit-retained-" + Guid.NewGuid().ToString("N");
        var owner = new HardwareHostLease(name);
        owner.RetainUntilProcessExit();
        owner.Dispose();
        HardwareHostLease.TryAcquire(out var other, out _, name).Should().BeFalse();
        other.Should().BeNull();
        // The unique test-only mutex intentionally lives until this test process exits, like an uncertain writer.
    }

    [Fact]
    public async Task Concurrent_status_publications_are_complete_and_leave_no_shared_temp_file()
    {
        await using var host = await TestHost.StartAsync();
        var writer = host.Get<StatusWriter>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(writer.WriteNow)));
        using var document = JsonDocument.Parse(await StatusWriter.ReadSharedAsync(writer.StatusPath));
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        Directory.GetFiles(Path.GetDirectoryName(writer.StatusPath)!, "status.json*.tmp").Should().BeEmpty();
    }
}
