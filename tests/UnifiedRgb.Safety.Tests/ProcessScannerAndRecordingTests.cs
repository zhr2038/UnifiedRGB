using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Process;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Safety.Tests;

internal sealed class FakeSnapshot : ISystemSnapshot
{
    public List<ProcessInfo> Procs { get; } = [];

    public List<ServiceInfo> Svcs { get; } = [];

    public IReadOnlyList<ProcessInfo> Processes() => Procs;

    public IReadOnlyList<ServiceInfo> Services() => Svcs;
}

public class ProcessScannerAndRecordingTests
{
    [Fact]
    public void Clean_system_reports_no_writers()
    {
        var snap = new FakeSnapshot();
        snap.Procs.Add(new ProcessInfo(1, "explorer"));
        new ProcessScanner(snap).Scan().Should().BeEmpty();
    }

    [Fact]
    public void Detects_vendor_processes_and_running_services_per_class()
    {
        var snap = new FakeSnapshot();
        snap.Procs.Add(new ProcessInfo(10, "LEDKeeper2"));
        snap.Svcs.Add(new ServiceInfo("LConnectService", Running: true, "Automatic")); // SCM name differs from the process name
        snap.Svcs.Add(new ServiceInfo("Mystic_Light_Service", Running: false, "Manual"));
        var scanner = new ProcessScanner(snap);

        scanner.ScanFor(DeviceClass.MsiGpu).Should().ContainSingle(w => w.Writer.Name == "LEDKeeper2");
        scanner.ScanFor(DeviceClass.LianLi).Should().ContainSingle(w => w.Writer.Name == "L-Connect-Service" && w.FoundAs == WriterKind.Service);
        scanner.ScanFor(DeviceClass.Virtual).Should().BeEmpty();
    }

    [Fact]
    public void Our_own_openrgb_is_not_a_writer_but_a_foreign_one_is()
    {
        var snap = new FakeSnapshot();
        snap.Procs.Add(new ProcessInfo(100, "OpenRGB"));
        snap.Procs.Add(new ProcessInfo(200, "OpenRGB"));
        var scanner = new ProcessScanner(snap);
        scanner.RegisterOwnedProcess(100);
        scanner.ScanFor(DeviceClass.OpenRgb).Should().ContainSingle(w => w.Pid == 200);
    }

    [Fact]
    public void Known_writer_list_matches_architecture()
    {
        KnownWriters.All.Select(w => w.Name).Should().BeEquivalentTo(
            "L-Connect-Service", "L-Connect-Service-Watcher", "L-Connect 3", "Mystic_Light_Service", "LightKeeperService",
            "LEDKeeper2", "OpenRGB", "LConnectMSI5090Mirror", "LianliToMSIGPU-OpenRGB", "Aac3572MbHal_x86", "GHelper");

        // NATIVE design §8: the MSI rows cover the GPU and the DRAM path; the ASUS HAL is a Process row (never a service we stop,
        // so its Windows DCOM host is never touched); candidates are informational only and never rows.
        foreach (var msi in new[] { "Mystic_Light_Service", "LightKeeperService", "LEDKeeper2", "LConnectMSI5090Mirror", "LianliToMSIGPU-OpenRGB" })
        {
            KnownWriters.All.Single(w => w.Name == msi).Affects.Should().Contain([DeviceClass.MsiGpu, DeviceClass.EneDram]);
        }

        var hal = KnownWriters.All.Single(w => w.Name == KnownWriters.AsusHalProcessName);
        hal.Kind.Should().Be(WriterKind.Process);
        hal.Affects.Should().BeEquivalentTo([DeviceClass.AsusAura, DeviceClass.EneDram]);
        KnownWriters.All.Select(w => w.ScmName).Should().NotContain(n => n.Contains("DcomLaunch", StringComparison.OrdinalIgnoreCase)
            || n.Equals("svchost", StringComparison.OrdinalIgnoreCase) || n.Equals("RpcSs", StringComparison.OrdinalIgnoreCase));
        KnownWriters.Candidates.Select(c => c.Name).Should().NotIntersectWith(KnownWriters.All.Select(w => w.Name));
        KnownWriters.All.Where(w => w.Affects.Contains(DeviceClass.OpenRgb)).Should().NotBeEmpty();
        foreach (var cls in new[] { DeviceClass.LianLi, DeviceClass.MsiGpu, DeviceClass.OpenRgb, DeviceClass.AsusAura, DeviceClass.EneDram, DeviceClass.AsusRog })
        {
            KnownWriters.All.Should().Contain(w => w.Affects.Contains(cls), $"{cls} has a confirmed writer row");
        }
    }

    [Fact]
    public async Task Recording_round_trips_through_file_format()
    {
        var rec = new RecordingTransport();
        await rec.SendAsync(new byte[] { 16, 32, 1, 2, 3 }, CancellationToken.None);
        rec.EnqueueRead([9, 9]);
        var buf = new byte[8];
        (await rec.ReadAsync(buf, CancellationToken.None)).Should().Be(2);

        using var ms = new MemoryStream();
        rec.Save(ms);
        ms.Position = 0;
        var loaded = RecordingTransport.Load(ms);
        loaded.Should().HaveCount(2);
        loaded[0].Direction.Should().Be(RecordDirection.Out);
        loaded[0].Data.Should().Equal(16, 32, 1, 2, 3);
        loaded[1].Direction.Should().Be(RecordDirection.In);
        loaded[1].Data.Should().Equal(9, 9);
    }
}
