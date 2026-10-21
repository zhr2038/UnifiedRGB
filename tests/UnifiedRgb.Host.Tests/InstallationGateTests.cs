using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Tests;

public sealed class InstallationGateTests
{
    [Fact]
    public async Task Maintenance_blocks_another_startup_until_publication_is_complete()
    {
        var name = @"Local\UnifiedRgb.Install.Test." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var installer = Task.Run(() =>
        {
            using var held = InstallationGate.TryEnter(name);
            held.Should().NotBeNull();
            ready.Set();
            release.Wait();
        });
        ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        try { InstallationGate.TryEnter(name).Should().BeNull(); }
        finally { release.Set(); }
        await installer;
        using var startup = InstallationGate.TryEnter(name);
        startup.Should().NotBeNull();
    }

    [Fact]
    public void Failed_bounded_start_does_not_poison_the_gate()
    {
        var name = @"Local\UnifiedRgb.Install.Test." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() => { using var gate = InstallationGate.TryEnter(name); ready.Set(); release.Wait(); });
        thread.Start();
        try
        {
            ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            InstallationGate.EnterWhenAvailable(TimeSpan.FromMilliseconds(100), name).Should().BeNull();
        }
        finally { release.Set(); thread.Join(); }
        using var after = InstallationGate.TryEnter(name);
        after.Should().NotBeNull();
    }
}
