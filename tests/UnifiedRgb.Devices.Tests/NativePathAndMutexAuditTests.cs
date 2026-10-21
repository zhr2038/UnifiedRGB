using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.Tests;

public sealed class NativePathAndMutexAuditTests
{
    [Fact]
    public void Explicit_relative_vendor_codec_path_is_resolved_from_the_application_directory()
    {
        var relative = Path.Combine("codec", "yuz.dll");
        YuzNative.ResolveConfiguredPath(relative).Should().Be(Path.GetFullPath(relative, AppContext.BaseDirectory));
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "user-provided", "yuz.dll"));
        YuzNative.ResolveConfiguredPath(absolute).Should().Be(absolute);
        YuzNative.ResolveConfiguredPath(null).Should().BeNull();
        YuzNative.ResolveConfiguredPath("").Should().BeEmpty();
    }

    [Fact]
    public void Abandoned_bus_ownership_is_observable_without_rejecting_a_valid_windows_acquisition()
    {
        var name = "Local\\urgb-audit-bus-" + Guid.NewGuid().ToString("N");
        using var observed = new GlobalSmbusMutex(name);
        var previous = new Thread(() => { using var mutex = new Mutex(false, name); mutex.WaitOne(); });
        previous.Start(); previous.Join(); // deliberate test-only abandoned owner; never the production SMBus mutex
        observed.Wait(0).Should().BeTrue();
        try { observed.AbandonedAcquisitions.Should().Be(1); }
        finally { observed.Release(); }
        observed.Wait(0).Should().BeTrue();
        try { observed.AbandonedAcquisitions.Should().Be(1); }
        finally { observed.Release(); }
    }
}
