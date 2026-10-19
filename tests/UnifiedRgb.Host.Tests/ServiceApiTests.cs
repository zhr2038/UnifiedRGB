using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Autostart;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

/// <summary>Mutable stand-in for both the process/service snapshot and the SCM; records every SCM call.</summary>
internal sealed class FakeSystem : ISystemSnapshot, IServiceControl
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (bool Running, string StartType)> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _processes = [];

    public ConcurrentQueue<string> Calls { get; } = new();

    public HashSet<string> FailStop { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeSystem Service(string name, bool running = true, string startType = "Automatic")
    {
        lock (_gate)
        {
            _services[name] = (running, startType);
        }

        return this;
    }

    public FakeSystem Process(string name)
    {
        lock (_gate)
        {
            _processes.Add(name);
        }

        return this;
    }

    public void Kill(string name)
    {
        lock (_gate)
        {
            _processes.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public (bool Running, string StartType) this[string name]
    {
        get
        {
            lock (_gate)
            {
                return _services[name];
            }
        }
    }

    public IReadOnlyList<ProcessInfo> Processes()
    {
        lock (_gate)
        {
            // a running service also shows up as its process
            return _processes.Concat(_services.Where(s => s.Value.Running).Select(s => s.Key))
                .Select((p, i) => new ProcessInfo(5000 + i, p)).ToArray();
        }
    }

    public IReadOnlyList<ServiceInfo> Services()
    {
        lock (_gate)
        {
            return _services.Select(s => new ServiceInfo(s.Key, s.Value.Running, s.Value.StartType)).ToArray();
        }
    }

    public ServiceInfo? Query(string name)
    {
        Calls.Enqueue($"query {name}");
        lock (_gate)
        {
            return _services.TryGetValue(name, out var s) ? new ServiceInfo(name, s.Running, s.StartType) : null;
        }
    }

    public void Stop(string name, TimeSpan timeout)
    {
        Calls.Enqueue($"stop {name}");
        if (FailStop.Contains(name))
        {
            throw new InvalidOperationException("access denied");
        }

        lock (_gate)
        {
            _services[name] = (false, _services[name].StartType);
        }
    }

    public void Start(string name, TimeSpan timeout)
    {
        Calls.Enqueue($"start {name}");
        lock (_gate)
        {
            _services[name] = (true, _services[name].StartType);
        }
    }

    public void SetStartType(string name, string startType)
    {
        Calls.Enqueue($"starttype {name} {startType}");
        lock (_gate)
        {
            _services[name] = (_services[name].Running, startType);
        }
    }
}

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-profiles", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    internal static ProfileDto Sample(string name = "Evening") => new(name, new SceneDto(
    [
        new AssignmentDto([new ZoneRefDto("virtual:fan1", 0), new ZoneRefDto("virtual:gpu", 0)], "Runway", ["#FF0000", "#0000FF"], 0.7f, 0.9f, true, 7, "Front"),
        new AssignmentDto([new ZoneRefDto("virtual:mb", 2)], "StaticColor", ["#00FF00"]),
    ]));

    [Fact]
    public void Round_trip_is_byte_identical()
    {
        var store = new ProfileStore(_dir);
        store.Save(Sample());
        var first = File.ReadAllBytes(Path.Combine(_dir, "Evening.json"));
        var loaded = store.Load("Evening");
        store.Save(loaded);
        File.ReadAllBytes(Path.Combine(_dir, "Evening.json")).Should().Equal(first);
        ProfileStore.Serialize(loaded).Should().Equal(first);
        loaded.Scene.Assignments.Should().HaveCount(2);
        loaded.Scene.Assignments[0].Targets.Should().Equal(Sample().Scene.Assignments[0].Targets);
    }

    [Fact]
    public void Crash_during_save_leaves_previous_profile_intact_and_no_temp_files()
    {
        var store = new ProfileStore(_dir);
        store.Save(Sample());
        var before = File.ReadAllBytes(Path.Combine(_dir, "Evening.json"));

        store.BeforeCommit = _ => throw new IOException("power loss");
        var changed = Sample() with { Scene = new SceneDto([]) };
        var act = () => store.Save(changed);
        act.Should().Throw<IOException>();

        File.ReadAllBytes(Path.Combine(_dir, "Evening.json")).Should().Equal(before);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        store.BeforeCommit = null;
        store.Load("Evening").Scene.Assignments.Should().HaveCount(2);
    }

    [Fact]
    public void Leftover_temp_files_from_a_real_crash_are_ignored()
    {
        var store = new ProfileStore(_dir);
        store.Save(Sample());
        File.WriteAllText(Path.Combine(_dir, "Evening.json.0123.tmp"), "{ torn");
        store.List().Select(p => p.Name).Should().Equal("Evening");
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("C:x")]
    [InlineData("active")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    [InlineData(".hidden")]
    [InlineData("this-name-is-far-too-long-for-a-profile-name-and-should-be-rejected-x")]
    public void Unsafe_names_are_rejected(string name)
    {
        var store = new ProfileStore(_dir);
        ProfileStore.IsValidName(name).Should().BeFalse();
        var save = () => store.Save(Sample(name));
        save.Should().Throw<ArgumentException>();
        var load = () => store.Load(name);
        load.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Active_is_separate_and_not_listed()
    {
        var store = new ProfileStore(_dir);
        store.LoadActive().Should().BeNull();
        store.SaveActive(Sample("Custom"));
        store.LoadActive()!.Name.Should().Be("Custom");
        store.List().Should().BeEmpty();
    }

    [Fact]
    public void Corrupt_or_missing_files_are_reported()
    {
        var store = new ProfileStore(_dir);
        var missing = () => store.Load("Nope");
        missing.Should().Throw<KeyNotFoundException>();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "active.json"), "{ not json");
        var corrupt = () => store.LoadActive();
        corrupt.Should().Throw<InvalidDataException>();
    }
}

public sealed class TakeoverTests
{
    private static WriterGuard Guard(FakeSystem sys, TakeoverStore? store = null) =>
        new(new ProcessScanner(sys), sys, store ?? new TakeoverStore((string?)null));

    [Fact]
    public async Task Default_policy_blocks_and_never_calls_the_scm()
    {
        var sys = new FakeSystem().Service("LConnectService").Service("LConnectServiceWatcher");
        using var guard = Guard(sys);
        guard.IsTakeoverEnabled(DeviceClass.LianLi).Should().BeFalse();
        guard.Check(DeviceClass.LianLi).State.Should().Be(GuardState.Blocked);
        var r = await guard.TakeoverAsync(DeviceClass.LianLi, CancellationToken.None);
        r.Enabled.Should().BeFalse();
        await guard.RestoreAsync(CancellationToken.None);
        sys.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Takeover_is_a_no_op_without_vendor_services()
    {
        var sys = new FakeSystem();
        using var guard = Guard(sys);
        var r = await guard.SetTakeoverAsync(DeviceClass.LianLi, true, CancellationToken.None);
        r.Enabled.Should().BeTrue();
        r.StoppedServices.Should().BeEmpty();
        r.Error.Should().BeNull();
        await guard.RestoreAsync(CancellationToken.None);
        sys.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Takeover_stops_watcher_first_records_and_restores_on_exit()
    {
        // Real machine: SCM names are LConnectService(Watcher), the processes are "L-Connect-Service(-Watcher)".
        var sys = new FakeSystem()
            .Service("LConnectService").Service("LConnectServiceWatcher", startType: "Manual")
            .Process("L-Connect-Service").Process("L-Connect-Service-Watcher").Process("L-Connect 3");
        using var guard = Guard(sys);

        var r = await guard.SetTakeoverAsync(DeviceClass.LianLi, true, CancellationToken.None);

        r.StoppedServices.Should().Equal("LConnectServiceWatcher", "LConnectService");
        r.ProcessesToClose.Should().Equal(["L-Connect 3"], "service processes exit with their service even when the names differ");
        sys["LConnectService"].Should().Be((false, "Disabled"));
        sys["LConnectServiceWatcher"].Should().Be((false, "Disabled"));
        guard.Check(DeviceClass.LianLi).Should().Be(GuardVerdict.Blocked("L-Connect-Service"), "stopped services still show as processes until they exit");

        sys.Kill("L-Connect-Service");
        sys.Kill("L-Connect-Service-Watcher");
        guard.Check(DeviceClass.LianLi).Should().Be(GuardVerdict.Blocked("L-Connect 3"), "GUI processes are never killed");

        sys.Kill("L-Connect 3");
        guard.Check(DeviceClass.LianLi).State.Should().Be(GuardState.Clear);

        await guard.RestoreAsync(CancellationToken.None);
        sys["LConnectService"].Should().Be((true, "Automatic"));
        sys["LConnectServiceWatcher"].Should().Be((true, "Manual"));
        var calls = sys.Calls.ToArray();
        Array.IndexOf(calls, "start LConnectService").Should().BeLessThan(Array.IndexOf(calls, "start LConnectServiceWatcher"));
        sys.Calls.Should().OnlyContain(c => c.Contains("LConnectService", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bookkeeping_survives_a_crash_and_is_restored_by_the_next_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), "urgb-takeover", Guid.NewGuid().ToString("N"), "takeover.json");
        try
        {
            var sys = new FakeSystem().Service("Mystic_Light_Service", startType: "Manual");
            using (var first = Guard(sys, new TakeoverStore(path)))
            {
                await first.SetTakeoverAsync(DeviceClass.MsiGpu, true, CancellationToken.None);
            }

            // "crash": no RestoreAsync. A fresh guard (e.g. --restore-services) reads takeover.json.
            File.ReadAllText(path).Should().Contain("Mystic_Light_Service");
            using var second = Guard(sys, new TakeoverStore(path));
            second.IsTakeoverEnabled(DeviceClass.MsiGpu).Should().BeTrue("the toggle is persisted");
            await second.RestoreAsync(CancellationToken.None);
            sys["Mystic_Light_Service"].Should().Be((true, "Manual"));
            new TakeoverStore(path).Load().Stopped.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task Disabling_one_class_restores_only_services_no_other_enabled_class_needs()
    {
        var sys = new FakeSystem().Service("LConnectService").Service("Mystic_Light_Service");
        using var guard = Guard(sys);
        await guard.SetTakeoverAsync(DeviceClass.LianLi, true, CancellationToken.None);
        await guard.SetTakeoverAsync(DeviceClass.OpenRgb, true, CancellationToken.None); // L-Connect also affects OpenRgb
        await guard.SetTakeoverAsync(DeviceClass.MsiGpu, true, CancellationToken.None);

        await guard.SetTakeoverAsync(DeviceClass.LianLi, false, CancellationToken.None);
        sys["LConnectService"].Running.Should().BeFalse("OpenRgb takeover still needs it stopped");

        await guard.SetTakeoverAsync(DeviceClass.OpenRgb, false, CancellationToken.None);
        sys["LConnectService"].Should().Be((true, "Automatic"));
        sys["Mystic_Light_Service"].Running.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_stop_is_reported_and_record_kept_for_restore()
    {
        var sys = new FakeSystem().Service("LightKeeperService");
        sys.FailStop.Add("LightKeeperService");
        using var guard = Guard(sys);
        var r = await guard.SetTakeoverAsync(DeviceClass.MsiGpu, true, CancellationToken.None);
        r.Error.Should().Contain("LightKeeperService");
        r.StoppedServices.Should().BeEmpty();
        guard.Check(DeviceClass.MsiGpu).State.Should().Be(GuardState.Blocked);
        await guard.RestoreAsync(CancellationToken.None);
        sys["LightKeeperService"].StartType.Should().Be("Automatic");
    }

    [Fact]
    public async Task Virtual_and_unknown_classes_cannot_be_taken_over()
    {
        using var guard = Guard(new FakeSystem());
        var v = () => guard.SetTakeoverAsync(DeviceClass.Virtual, true, CancellationToken.None);
        await v.Should().ThrowAsync<ArgumentOutOfRangeException>();
        var u = () => guard.SetTakeoverAsync((DeviceClass)42, true, CancellationToken.None);
        await u.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Spooler")]
    [InlineData("WinDefend")]
    [InlineData("L-Connect 3")] // a process, not a service
    [InlineData("")]
    public void Real_service_control_refuses_anything_but_known_writer_services(string name)
    {
        var scm = new WindowsServiceControl();
        ((Action)(() => scm.SetStartType(name, "Disabled"))).Should().Throw<ArgumentException>();
        ((Action)(() => scm.Stop(name, TimeSpan.Zero))).Should().Throw<ArgumentException>();
        ((Action)(() => scm.Start(name, TimeSpan.Zero))).Should().Throw<ArgumentException>();
        ((Action)(() => scm.Query(name))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Device_class_parsing_rejects_numbers_virtual_and_garbage()
    {
        RgbService.ParseHardwareClass("lianli").Should().Be(DeviceClass.LianLi);
        foreach (var bad in new[] { "Virtual", "1", "42", "", "LianLi,MsiGpu", "Nope" })
        {
            ((Action)(() => RgbService.ParseHardwareClass(bad))).Should().Throw<ArgumentException>(bad);
        }
    }
}

public sealed class RecheckTests
{
    [Fact]
    public async Task Reappearing_vendor_writer_pauses_then_resumes_and_blocked_controller_opens_when_cleared()
    {
        var sys = new FakeSystem();
        var guard = new WriterGuard(new ProcessScanner(sys), sys, new TakeoverStore((string?)null));
        var gpu = new StubAdapter(DeviceClass.MsiGpu, "gpu:1");
        var lian = new StubAdapter(DeviceClass.LianLi, "lianli:1");
        sys.Process("L-Connect 3");
        using var bus = new FrameBus();
        var host = new AdapterHost([new StubFactory(gpu), new StubFactory(lian)], null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await host.StartAsync(CancellationToken.None);

        Entry(host, "gpu:1").IsActive.Should().BeTrue();
        Entry(host, "lianli:1").Verdict.State.Should().Be(GuardState.Blocked);

        sys.Process("LEDKeeper2");
        (await host.RecheckAsync(CancellationToken.None)).Should().BeTrue();
        Entry(host, "gpu:1").Writer!.Paused.Should().BeTrue();
        DtoMapper.ToDto(Entry(host, "gpu:1")).State.Should().Be(ControllerStates.Contended);
        DtoMapper.ToDto(Entry(host, "gpu:1")).BlockedBy.Should().Be("LEDKeeper2");

        sys.Kill("LEDKeeper2");
        sys.Kill("L-Connect 3");
        (await host.RecheckAsync(CancellationToken.None)).Should().BeTrue();
        Entry(host, "gpu:1").Writer!.Paused.Should().BeFalse();
        DtoMapper.ToDto(Entry(host, "gpu:1")).State.Should().Be(ControllerStates.Active);
        Entry(host, "lianli:1").IsActive.Should().BeTrue();
        lian.Opens.Should().Be(1);

        (await host.RecheckAsync(CancellationToken.None)).Should().BeFalse("nothing changed");
        await host.StopAsync();
    }

    [Fact]
    public async Task Hotplug_requests_a_rescan_that_adds_new_controllers()
    {
        var sys = new FakeSystem();
        var guard = new WriterGuard(new ProcessScanner(sys));
        var a = new StubAdapter(DeviceClass.LianLi, "lianli:1");
        using var bus = new FrameBus();
        var host = new AdapterHost([new StubFactory(a)], null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        host.RescanRequested.Should().BeFalse();

        a.Ids.Add("lianli:2");
        a.RaiseHotplug();
        host.RescanRequested.Should().BeTrue();
        (await host.RescanAsync(CancellationToken.None)).Should().BeTrue();
        host.RescanRequested.Should().BeFalse();
        host.Controllers.Should().HaveCount(2).And.OnlyContain(c => c.IsActive);
        a.Opens.Should().Be(2, "the existing controller is not reopened");
        await host.StopAsync();
    }

    [Fact]
    public void Set_assignment_takes_zones_out_of_other_assignments()
    {
        static ZoneRef Z(string c, int z) => new(new ControllerId(c), new ZoneId(z));
        var scene = new Scene(
        [
            Assignment.ForGroup("All", [Z("a", 0), Z("b", 0), Z("c", 0)], "Rainbow"),
            Assignment.ForZone(Z("d", 0), "Meteor"),
        ]);
        var merged = LightingEngine.Merge(scene, Assignment.ForZone(Z("b", 0), "StaticColor"));
        merged.Assignments.Should().HaveCount(3);
        merged.Assignments[0].Targets.Should().Equal(Z("a", 0), Z("c", 0));
        merged.Assignments[2].EffectId.Should().Be("StaticColor");

        var all = LightingEngine.Merge(merged, Assignment.ForZone(Z("d", 0), "Breathing"));
        all.Assignments.Select(x => x.EffectId).Should().Equal("Rainbow", "StaticColor", "Breathing");
    }

    private static ControllerEntry Entry(AdapterHost h, string id) => h.Controllers.Single(c => c.Descriptor.Id.Value == id);

    private sealed class StubFactory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => adapter.Class;

        public IDeviceAdapter Create(IServiceProvider sp) => adapter;
    }

    private sealed class StubAdapter(DeviceClass cls, string id) : IDeviceAdapter
    {
        public List<string> Ids { get; } = [id];

        public int Opens { get; private set; }

        public string Vendor => cls.ToString();

        public DeviceClass Class => cls;

        public event Action<HotplugEvent>? Hotplug;

        public void RaiseHotplug() => Hotplug?.Invoke(new HotplugEvent(new ControllerId(Ids[^1]), HotplugKind.Arrived));

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ControllerDescriptor>>(Ids.Select(i => new ControllerDescriptor(new ControllerId(i), "X", i, DeviceType.Gpu, cls, i,
                [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Point)], Capabilities.DirectOnly(30))).ToArray());

        public Task OpenAsync(ControllerId id, CancellationToken ct)
        {
            Opens++;
            return Task.CompletedTask;
        }

        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
        }

        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId id) => new(DateTimeOffset.UnixEpoch, [1200], null);

        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }
}

public sealed class ScheduledTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Definition_matches_architecture_9()
    {
        var doc = ScheduledTaskInstaller.BuildDefinition(@"C:\Program Files\UnifiedRgb\UnifiedRgb.Host.exe", "S-1-5-21-1-2-3-1001");
        var t = doc.Root!;
        t.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "Delay")!.Value.Should().Be("PT20S");
        t.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value.Should().Be("S-1-5-21-1-2-3-1001");
        var p = t.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        p.Element(Ns + "RunLevel")!.Value.Should().Be("HighestAvailable");
        p.Element(Ns + "LogonType")!.Value.Should().Be("InteractiveToken");
        var s = t.Element(Ns + "Settings")!;
        s.Element(Ns + "RestartOnFailure")!.Element(Ns + "Interval")!.Value.Should().Be("PT1M");
        s.Element(Ns + "RestartOnFailure")!.Element(Ns + "Count")!.Value.Should().Be("3");
        s.Element(Ns + "ExecutionTimeLimit")!.Value.Should().Be("PT0S");
        s.Element(Ns + "MultipleInstancesPolicy")!.Value.Should().Be("IgnoreNew");
        var exec = t.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        exec.Element(Ns + "Command")!.Value.Should().Be(@"C:\Program Files\UnifiedRgb\UnifiedRgb.Host.exe");
        exec.Element(Ns + "Arguments").Should().BeNull();
    }

    [Fact]
    public void Written_file_is_utf16_and_bad_inputs_are_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"urgb-task-{Guid.NewGuid():N}.xml");
        try
        {
            ScheduledTaskInstaller.WriteDefinition(ScheduledTaskInstaller.BuildDefinition(@"C:\x\UnifiedRgb.Host.exe", "S-1-5-21-1-2-3-1001"), path);
            var bytes = File.ReadAllBytes(path);
            bytes.Take(2).Should().Equal(0xFF, 0xFE);
            XDocument.Load(path).Root!.Name.Should().Be(Ns + "Task");
        }
        finally
        {
            File.Delete(path);
        }

        ((Action)(() => ScheduledTaskInstaller.BuildDefinition("UnifiedRgb.Host.exe", "S-1-5-21-1-2-3-1001"))).Should().Throw<ArgumentException>();
        ((Action)(() => ScheduledTaskInstaller.BuildDefinition(@"C:\x\h.exe", "not-a-sid"))).Should().Throw<ArgumentException>();
        ScheduledTaskInstaller.CreateCommand(@"C:\t.xml").ArgumentList.Should().Equal("/Create", "/TN", "UnifiedRgb Host", "/XML", @"C:\t.xml", "/F");
        ScheduledTaskInstaller.DeleteCommand().UseShellExecute.Should().BeFalse();
    }
}

[Collection("Host")]
public sealed class ServiceApiTests
{
    private static async Task<(JsonRpc Rpc, IRgbService Svc, NamedPipeClientStream Pipe)> ConnectAsync(TestHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var svc = rpc.Attach<IRgbService>();
        rpc.StartListening();
        return (rpc, svc, pipe);
    }

    [Fact]
    public async Task Every_rpc_works_against_virtual()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            var controllers = await svc.ListControllersAsync();
            controllers.Should().HaveCount(9).And.OnlyContain(c => c.State == ControllerStates.Active);
            (await svc.ListEffectsAsync()).Should().Contain(e => e.Id == "Runway");

            await svc.SetSceneAsync(new SceneDto([new AssignmentDto([new ZoneRefDto("virtual:fan1", 0), new ZoneRefDto("virtual:fan2", 0)], "Meteor", [])]));
            await svc.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto("virtual:fan2", 0)], "StaticColor", ["#123456"]));
            var scene = await svc.GetSceneAsync();
            scene.Assignments.Should().HaveCount(2);
            scene.Assignments[0].Targets.Should().Equal(new ZoneRefDto("virtual:fan1", 0));
            scene.Assignments[1].Colors.Should().Equal("#123456");

            var bad = () => svc.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto("virtual:nope", 0)], "StaticColor", []));
            await bad.Should().ThrowAsync<RemoteInvocationException>();

            await svc.SaveProfileAsync(ProfileStoreTests.Sample());
            (await svc.ListProfilesAsync()).Select(p => p.Name).Should().Equal("Evening");
            (await svc.LoadProfileAsync("Evening")).Scene.Assignments.Should().HaveCount(2);
            await svc.ApplyProfileAsync("Evening");
            host.Engine.ActiveProfileName.Should().Be("Evening");
            host.Engine.Loop.Scene.Assignments[0].EffectId.Should().Be("Runway");
            var active = new ProfileStore(host.Options).LoadActive()!;
            active.Name.Should().Be("Evening");
            active.Scene.Assignments.Should().HaveCount(2);

            var traversal = () => svc.LoadProfileAsync("..\\..\\Windows\\win");
            await traversal.Should().ThrowAsync<RemoteInvocationException>();
            var missing = () => svc.ApplyProfileAsync("Missing");
            await missing.Should().ThrowAsync<RemoteInvocationException>();

            (await svc.GetStatusAsync(diagnostics: true)).Packets.Should().NotBeNull();
            (await svc.GetTelemetryAsync()).Should().NotBeNull();

            var virt = () => svc.SetTakeoverAsync("Virtual", true);
            await virt.Should().ThrowAsync<RemoteInvocationException>();

            (await svc.RescanAsync()).Should().HaveCount(9, "rescan does not duplicate known controllers");
        }
    }

    [Fact]
    public async Task Takeover_over_rpc_uses_scm_only_through_the_guard_and_restores_on_stop()
    {
        var sys = new FakeSystem().Service("LConnectService").Service("LConnectServiceWatcher");
        var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<ISystemSnapshot>(sys);
            s.AddSingleton<IServiceControl>(sys);
        });
        try
        {
            var (rpc, svc, pipe) = await ConnectAsync(host);
            using (rpc)
            await using (pipe)
            {
                var r = await svc.SetTakeoverAsync("LianLi", true);
                r.Enabled.Should().BeTrue();
                r.StoppedServices.Should().Equal("LConnectServiceWatcher", "LConnectService");
                r.Error.Should().BeNull();
                File.Exists(Path.Combine(host.Options.ResolvedDataDirectory, "takeover.json")).Should().BeTrue();
            }
        }
        finally
        {
            await host.DisposeAsync();
        }

        sys["LConnectService"].Should().Be((true, "Automatic"), "clean exit restores what Takeover stopped");
        sys["LConnectServiceWatcher"].Should().Be((true, "Automatic"));
    }

    [Fact]
    public async Task Status_and_telemetry_notifications_arrive()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            var status = new TaskCompletionSource<StatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var telemetry = new TaskCompletionSource<IReadOnlyList<TelemetryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            svc.StatusChanged += (_, s) => status.TrySetResult(s);
            svc.TelemetryTick += (_, t) => telemetry.TrySetResult(t);
            (await status.Task.WaitAsync(TimeSpan.FromSeconds(10))).Devices.Should().HaveCount(9);
            (await telemetry.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Active_json_is_restored_at_start_including_zones_of_absent_controllers()
    {
        var profile = new ProfileDto("Night", new SceneDto(
        [
            new AssignmentDto([new ZoneRefDto("virtual:gpu", 0), new ZoneRefDto("lianli:rf:AA-BB-CC-DD-EE-01", 0)], "Breathing", ["#FF00FF"]),
        ]));
        await using var host = await TestHost.StartAsync(seed: o => new ProfileStore(o).SaveActive(profile));
        host.Engine.ActiveProfileName.Should().Be("Night");
        host.Engine.Loop.Scene.Assignments.Should().ContainSingle().Which.Targets.Should().HaveCount(2, "the offline Lian Li zone is kept for later");
        await Task.Delay(300);
        var gpu = host.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id.Value == "virtual:gpu");
        gpu.Writer!.GetStats().Written.Should().BePositive();
    }

    [Fact]
    public async Task Corrupt_active_json_falls_back_to_the_default_scene()
    {
        await using var host = await TestHost.StartAsync(seed: o =>
        {
            var dir = Path.Combine(o.ResolvedDataDirectory, "profiles");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "active.json"), "{ torn");
        });
        host.Engine.Loop.Scene.Assignments.Should().ContainSingle().Which.EffectId.Should().Be("Rainbow");
    }
}
