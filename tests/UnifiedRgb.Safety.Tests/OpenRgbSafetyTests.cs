using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.OpenRgb;
using UnifiedRgb.Transports.Process;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Safety.Tests;

public class OpenRgbPacketFirewallTests
{
    private sealed class Sink : ITransport
    {
        public int Sent { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            Sent++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult(0);
    }

    /// <summary>
    /// Upstream ids we must never send: ResizeZone 1000, UpdateSingleLed 1052, SetCustomMode 1100, SaveMode 1102,
    /// profile list/save/load/delete 150–153, the server→client notification 100, and arbitrary junk.
    /// </summary>
    [Theory]
    [InlineData(1000u)]
    [InlineData(1052u)]
    [InlineData(1100u)]
    [InlineData(1102u)]
    [InlineData(150u)]
    [InlineData(151u)]
    [InlineData(152u)]
    [InlineData(153u)]
    [InlineData(100u)]
    [InlineData(0xFFFFFFFFu)]
    public async Task Packets_outside_the_allow_list_never_reach_the_socket(uint id)
    {
        var sink = new Sink();
        var fw = new OpenRgbPacketFirewall(sink);
        var act = async () => await fw.SendAsync(OpenRgbProtocol.Packet(0, id, [1, 2, 3, 4]), CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        sink.Sent.Should().Be(0);
    }

    [Fact]
    public async Task Bad_magic_or_lying_size_is_blocked()
    {
        var sink = new Sink();
        var fw = new OpenRgbPacketFirewall(sink);
        var p = OpenRgbProtocol.UpdateZoneLeds(0, 0, [1, 2, 3]);
        var badMagic = (byte[])p.Clone();
        badMagic[0] = (byte)'X';
        var badSize = (byte[])p.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSize.AsSpan(12), 4);
        foreach (var bad in new[] { badMagic, badSize, p[..10] })
        {
            var act = async () => await fw.SendAsync(bad, CancellationToken.None);
            await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        }

        sink.Sent.Should().Be(0);
        await fw.SendAsync(p, CancellationToken.None);
        sink.Sent.Should().Be(1);
    }

    [Fact]
    public void Every_product_encoder_output_passes()
    {
        var mode = new OpenRgbMode("Direct", 0xFFFF, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, []);
        foreach (var p in new[]
        {
            OpenRgbProtocol.RequestControllerCount(), OpenRgbProtocol.RequestProtocolVersion(), OpenRgbProtocol.RequestControllerData(2, 4),
            OpenRgbProtocol.SetClientName("UnifiedRgb"), OpenRgbProtocol.UpdateLeds(1, [1, 2, 3, 4, 5, 6]),
            OpenRgbProtocol.UpdateZoneLeds(1, 2, [1, 2, 3]), OpenRgbProtocol.UpdateMode(1, 0, mode, 4),
        })
        {
            OpenRgbPacketFirewall.Check(p);
        }
    }

    [Fact]
    public void Sendable_ids_are_exactly_the_documented_seven()
    {
        OpenRgbPacketIds.Sendable.Should().BeEquivalentTo(new uint[] { 0, 1, 40, 50, 1050, 1051, 1101 });
    }

    [Fact]
    public async Task Sdk_transport_refuses_non_loopback_endpoints()
    {
        var act = () => TcpSdkTransport.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 16744), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not loopback*");
    }
}

public sealed class OpenRgbSupervisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-sup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLauncher _launcher = new();

    public OpenRgbSupervisorTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Exe, "MZ pretend OpenRGB binary"u8.ToArray());
        File.WriteAllText(Template, """{ "Detectors": { "detectors": { "ENE SMBus DRAM": true } } }""");
    }

    private string Exe => Path.Combine(_dir, "OpenRGB.exe");

    private string Template => Path.Combine(_dir, "openrgb-headless.json");

    private string HashFile => Path.Combine(_dir, "SHA256SUMS");

    private OpenRgbSupervisorOptions Options => new()
    {
        ExecutablePath = Exe,
        HashFile = HashFile,
        ConfigTemplate = Template,
        ConfigDirectory = Path.Combine(_dir, "cfg"),
    };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Wrong_hash_is_refused_and_nothing_is_launched()
    {
        File.WriteAllText(HashFile, new string('0', 64) + "  OpenRGB.exe\n");
        using var sup = new OpenRgbProcessSupervisor(Options, _launcher);
        var act = sup.EnsureRunning;
        act.Should().Throw<OpenRgbBinaryRejectedException>().WithMessage("*does not match*");
        _launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void Missing_hash_file_or_unlisted_binary_is_refused()
    {
        using var sup = new OpenRgbProcessSupervisor(Options, _launcher);
        var act = sup.EnsureRunning;
        act.Should().Throw<OpenRgbBinaryRejectedException>().WithMessage("*missing*");
        File.WriteAllText(HashFile, "# nothing pinned\n" + Sha(Exe) + "  SomethingElse.exe\n");
        act.Should().Throw<OpenRgbBinaryRejectedException>().WithMessage("*no pinned hash*");
        _launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void Tampered_binary_is_refused_even_after_a_good_launch()
    {
        File.WriteAllText(HashFile, Sha(Exe).ToLowerInvariant() + " *OpenRGB.exe\n");
        using var sup = new OpenRgbProcessSupervisor(Options, _launcher);
        sup.EnsureRunning().Should().BeTrue();
        _launcher.Started.Single().Exited = true;
        File.AppendAllText(Exe, "patched");
        var act = sup.EnsureRunning;
        act.Should().Throw<OpenRgbBinaryRejectedException>();
        _launcher.Started.Should().HaveCount(1);
    }

    [Fact]
    public void Pinned_binary_launches_loopback_only_registers_pid_and_restarts()
    {
        File.WriteAllText(HashFile, Sha(Exe) + "  OpenRGB.exe\n");
        var snapshot = new FakeSnapshot();
        var scanner = new ProcessScanner(snapshot);
        using var sup = new OpenRgbProcessSupervisor(Options, _launcher, scanner);

        sup.EnsureRunning().Should().BeTrue();
        sup.EnsureRunning().Should().BeFalse("already running");
        var p = _launcher.Started.Single();
        p.Args.Should().Equal("--server", "--server-host", "127.0.0.1", "--server-port", "16744", "--config", Options.ConfigDirectory);
        File.ReadAllText(Path.Combine(Options.ConfigDirectory, "OpenRGB.json")).Should().Be(File.ReadAllText(Template));
        snapshot.Procs.Add(new ProcessInfo(p.Id, "OpenRGB"));
        scanner.ScanFor(DeviceClass.OpenRgb).Should().BeEmpty("our own instance is not a foreign writer");
        snapshot.Procs.Add(new ProcessInfo(4242, "OpenRGB"));
        scanner.ScanFor(DeviceClass.OpenRgb).Should().ContainSingle().Which.Pid.Should().Be(4242);
        snapshot.Procs.RemoveAt(1);

        p.Exited = true;
        sup.EnsureRunning().Should().BeTrue();
        sup.Restarts.Should().Be(1);
        scanner.ScanFor(DeviceClass.OpenRgb).Should().ContainSingle("the old pid is no longer ours");

        sup.Stop();
        _launcher.Started[1].Killed.Should().BeTrue();
        sup.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Shipped_hash_file_pins_nothing_until_the_owner_records_a_hash()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        var text = File.ReadAllText(Path.Combine(dir!.FullName, "tools", "openrgb", "SHA256SUMS"));
        OpenRgbProcessSupervisor.PinnedHash(text, "OpenRGB.exe").Should().BeNull();
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Crash_loop_has_monotonic_backoff_and_a_terminal_start_budget()
    {
        File.WriteAllText(HashFile, Sha(Exe) + "  OpenRGB.exe\n");
        var time = new SupervisorClock();
        var options = Options;
        options.MaxConsecutiveStarts = 3;
        using var sup = new OpenRgbProcessSupervisor(options, _launcher, time: time);
        sup.EnsureRunning().Should().BeTrue();
        _launcher.Started[^1].Exited = true;
        sup.EnsureRunning().Should().BeTrue();
        _launcher.Started[^1].Exited = true;
        var start = sup.EnsureRunning;
        start.Should().Throw<IOException>().WithMessage("*backoff*");
        _launcher.Started.Should().HaveCount(2);
        time.Advance(TimeSpan.FromSeconds(1));
        sup.EnsureRunning().Should().BeTrue();
        sup.Restarts.Should().Be(2, "backoff must not lose restart diagnostics");
        _launcher.Started[^1].Exited = true;
        start.Should().Throw<OpenRgbRestartLimitException>();
        time.Advance(TimeSpan.FromHours(1));
        start.Should().Throw<OpenRgbRestartLimitException>();
        _launcher.Started.Should().HaveCount(3);
        sup.Stop();
        sup.EnsureRunning().Should().BeTrue("explicit stop resets the bounded budget");
    }

    [Fact]
    public void Observed_stable_process_resets_the_crash_budget()
    {
        File.WriteAllText(HashFile, Sha(Exe) + "  OpenRGB.exe\n");
        var time = new SupervisorClock();
        var options = Options;
        options.MaxConsecutiveStarts = 1;
        using var sup = new OpenRgbProcessSupervisor(options, _launcher, time: time);
        sup.EnsureRunning().Should().BeTrue();
        time.Advance(options.StablePeriod);
        sup.EnsureRunning().Should().BeFalse();
        _launcher.Started[^1].Exited = true;
        sup.EnsureRunning().Should().BeTrue();
    }

    private sealed class SupervisorClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Only monotonic time is valid.");
        public void Advance(TimeSpan duration) => _timestamp += (long)duration.TotalMilliseconds;
    }

    private sealed class FakeSnapshot : ISystemSnapshot
    {
        public List<ProcessInfo> Procs { get; } = [];

        public IReadOnlyList<ProcessInfo> Processes() => Procs.ToArray();

        public IReadOnlyList<ServiceInfo> Services() => [];
    }

    private sealed class FakeProcess(int id, IReadOnlyList<string> args) : IOwnedProcess
    {
        public int Id { get; } = id;

        public IReadOnlyList<string> Args { get; } = args;

        public bool Exited { get; set; }

        public bool Killed { get; private set; }

        public bool HasExited => Exited || Killed;

        public void Kill() => Killed = true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<FakeProcess> Started { get; } = [];

        public IOwnedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            var p = new FakeProcess(1000 + Started.Count, arguments);
            Started.Add(p);
            return p;
        }
    }
}
