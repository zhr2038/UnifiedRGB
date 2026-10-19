using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Nvapi;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

/// <summary>A native adapter stand-in: records every call as "&lt;id&gt; &lt;action&gt;" in a (possibly shared) log; never touches hardware.</summary>
internal sealed class NativeFake : IDeviceAdapter, INativeBringup, IAsyncDisposable
{
    private readonly string _id;

    public NativeFake(DeviceClass cls, string id, ConcurrentQueue<string>? log = null, bool verified = false)
    {
        Class = cls;
        _id = id;
        Log = log ?? new ConcurrentQueue<string>();
        Verified = verified;
    }

    public ConcurrentQueue<string> Log { get; }

    public bool Verified { get; set; }

    /// <summary>When set, a probe waits on it (after signalling <see cref="StepEntered"/>).</summary>
    public SemaphoreSlim? StepGate { get; set; }

    public SemaphoreSlim StepEntered { get; } = new(0);

    public NativeProbeResult? NextProbe { get; set; }

    public Exception? ProbeError { get; set; }

    public Exception? OpenError { get; set; }

    /// <summary>When set, the writer open (IDeviceAdapter.OpenAsync) fails with it.</summary>
    public Exception? WriterOpenError { get; set; }

    public int Releases => Log.Count(l => l == $"{_id} release");

    public string Vendor => "Fake";

    public DeviceClass Class { get; }

    public IReadOnlyList<string> Probes => ["p"];

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ControllerDescriptor>>([new ControllerDescriptor(new ControllerId(_id), "Fake", _id, DeviceType.Gpu, Class, _id,
            [AggregateZone.Descriptor("Fake")], Capabilities.DirectOnly(20))]);

    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        Log.Enqueue($"{_id} writer-open");
        return WriterOpenError is { } e ? Task.FromException(e) : Task.CompletedTask;
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

    public Telemetry? ReadTelemetry(ControllerId id) => null;

    public Task CloseAsync(ControllerId id)
    {
        Log.Enqueue($"{_id} close");
        return Task.CompletedTask;
    }

    public bool IsControlVerified(ControllerId id) => Verified;

    public string UnverifiedReason(ControllerId id) => "fake control not verified";

    public Task OpenForBringupAsync(ControllerId id, CancellationToken ct)
    {
        Log.Enqueue($"{_id} open");
        return OpenError is { } e ? Task.FromException(e) : Task.CompletedTask;
    }

    public async Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct)
    {
        Log.Enqueue($"{_id} probe {probe}");
        StepEntered.Release();
        if (StepGate is { } gate)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }

        Log.Enqueue($"{_id} probe-done");
        if (ProbeError is { } e)
        {
            throw e;
        }

        return NextProbe ?? new NativeProbeResult(probe, true, "ok", [new NativeProbeRow("read", "68EC04", 0, "91500009", 0.5)])
        {
            Fields = new Dictionary<string, string> { ["comm"] = "ok", ["readback"] = "none" },
        };
    }

    public Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct)
    {
        Log.Enqueue($"{_id} marker {marker.Start}+{marker.Count} #{marker.Color.R:X2}{marker.Color.G:X2}{marker.Color.B:X2}");
        return Task.FromResult(new NativeProbeResult("marker", true, "marker frame sent", []) { Fields = new Dictionary<string, string> { ["comm"] = "ok" } });
    }

    public Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct)
    {
        Log.Enqueue($"{_id} end-marker");
        return Task.FromResult(new NativeProbeResult("end-marker", true, "black frame sent", []));
    }

    public Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct)
    {
        Log.Enqueue($"{_id} release");
        return Task.FromResult(new NativeProbeResult("release", true, "released", []) { Fields = new Dictionary<string, string> { ["release"] = "done" } });
    }

    public ValueTask DisposeAsync()
    {
        Log.Enqueue($"{_id} dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>A guard whose verdicts and takeover toggles the test sets; counts restores; never touches the SCM.</summary>
internal sealed class FakeGuard(ConcurrentQueue<string>? events = null) : IWriterGuard
{
    private readonly HashSet<DeviceClass> _enabled = [];
    private int _restores;

    /// <summary>Takeover switches and restores in call order, shared with adapter fakes to check ordering.</summary>
    public ConcurrentQueue<string> Events { get; } = events ?? new ConcurrentQueue<string>();

    public ConcurrentDictionary<DeviceClass, GuardVerdict> Verdicts { get; } = new();

    public int Restores => Volatile.Read(ref _restores);

    public FakeGuard Enable(params DeviceClass[] classes)
    {
        lock (_enabled)
        {
            _enabled.UnionWith(classes);
        }

        return this;
    }

    public GuardVerdict Check(DeviceClass deviceClass) => Verdicts.TryGetValue(deviceClass, out var v) ? v : GuardVerdict.Clear;

    public bool IsTakeoverEnabled(DeviceClass deviceClass)
    {
        lock (_enabled)
        {
            return _enabled.Contains(deviceClass);
        }
    }

    public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct)
    {
        lock (_enabled)
        {
            if (enabled)
            {
                _enabled.Add(deviceClass);
            }
            else
            {
                _enabled.Remove(deviceClass);
            }
        }

        Events.Enqueue($"guard takeover {(enabled ? "on" : "off")} {deviceClass}");
        return Task.FromResult(new TakeoverResult(deviceClass, enabled, [], [], null));
    }

    public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) =>
        Task.FromResult(new TakeoverResult(deviceClass, IsTakeoverEnabled(deviceClass), [], [], null));

    public Task RestoreAsync(CancellationToken ct)
    {
        Events.Enqueue("guard restore");
        Interlocked.Increment(ref _restores);
        return Task.CompletedTask;
    }
}

internal sealed class NativeFakeFactory(NativeFake adapter) : IDeviceAdapterFactory
{
    public DeviceClass Class => adapter.Class;

    public IDeviceAdapter Create(IServiceProvider sp) => adapter;
}

internal sealed class InstanceFactory(IDeviceAdapter adapter) : IDeviceAdapterFactory
{
    public DeviceClass Class => adapter.Class;

    public IDeviceAdapter Create(IServiceProvider sp) => adapter;
}

/// <summary>Offline NVAPI for the Host tests (thread-safe log: the writer thread writes while the test reads).</summary>
internal sealed class HostFakeNvapi : INvapi
{
    public const string FamilyRead = "R 68 EC 4";
    public const string Enter = "W 68 06 00";
    public const string Release = "W 68 06 01";

    private static readonly IntPtr Gpu0 = 0x1000;

    public ConcurrentQueue<string> Log { get; } = new();

    public ConcurrentQueue<(int Status, byte[] Data, int ReturnedSize)> Reads { get; } = new();

    public bool Initialize() => true;

    public IReadOnlyList<IntPtr> EnumPhysicalGpus() => [Gpu0];

    public NvPciIds GetPciIdentifiers(IntPtr gpu) => new(0x2B8510DE, 0x53011462, 0xA1, 0);

    public int I2cWrite(IntPtr gpu, byte device, byte register, ReadOnlySpan<byte> data, NvI2cProfile profile)
    {
        Log.Enqueue($"W {device:X2} {register:X2} {Convert.ToHexString(data)}");
        return 0;
    }

    public int I2cRead(IntPtr gpu, byte device, byte register, Span<byte> data, NvI2cProfile profile, out int returnedSize)
    {
        Log.Enqueue($"R {device:X2} {register:X2} {data.Length}");
        if (!Reads.TryDequeue(out var r))
        {
            throw new NotSupportedException();
        }

        data.Clear();
        r.Data.AsSpan(0, Math.Min(r.Data.Length, data.Length)).CopyTo(data);
        returnedSize = r.ReturnedSize;
        return r.Status;
    }

    public int? GetGpuTemperature(IntPtr gpu) => null;
}

/// <summary>NATIVE design §6 + overnight addendum §4/§9: session ownership, lease, idempotent end, holds and evidence.</summary>
public sealed class BringupSessionTests
{
    private const string Gpu = "gpu:1";

    [Fact]
    public async Task Unverified_native_controller_is_blocked_with_no_writer_and_never_opened()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(fake);
        var e = rig.Entry(Gpu);
        e.IsActive.Should().BeFalse();
        e.Writer.Should().BeNull();
        e.Verdict.Should().Be(GuardVerdict.Blocked("fake control not verified"));
        DtoMapper.ToDto(e).State.Should().Be(ControllerStates.Blocked);
        fake.Log.Should().BeEmpty("discovery only: neither the writer open nor a bring-up open ran");
        (await rig.Adapters.RecheckAsync(default)).Should().BeFalse();
        fake.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Controller_dto_reports_control_verified_only_from_the_native_gate()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(fake);
        var dto = DtoMapper.ToDto(rig.Entry(Gpu));
        (dto.State, dto.BlockedBy, dto.ControlVerified).Should().Be((ControllerStates.Blocked, "fake control not verified", false));
        dto.Zones.Should().OnlyContain(z => !z.Mapped, "the aggregate zone is unmapped");

        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        DtoMapper.ToDto(rig.Entry(Gpu)).ControlVerified.Should().BeFalse("a bring-up session does not verify control");
        await rig.Bringup.EndAsync("pipe:A", Gpu);
        fake.Verified = true;
        DtoMapper.ToDto(rig.Entry(Gpu)).ControlVerified.Should().BeTrue("only the class gate sets it");
    }

    [Fact]
    public async Task Begin_needs_takeover_a_clear_guard_and_a_native_controller()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(new FakeGuard(), null, fake);
        var off = () => rig.Bringup.BeginAsync("pipe:A", Gpu, 0, default);
        await off.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Takeover is off*");
        rig.Guard.Enable(DeviceClass.MsiGpu).Verdicts[DeviceClass.MsiGpu] = GuardVerdict.Blocked("LEDKeeper2");
        await off.Should().ThrowAsync<InvalidOperationException>().WithMessage("*LEDKeeper2*");
        var unknown = () => rig.Bringup.BeginAsync("pipe:A", "gpu:9", 0, default);
        await unknown.Should().ThrowAsync<ArgumentException>();
        fake.Log.Should().BeEmpty();

        rig.Guard.Verdicts.Clear();
        var s = await rig.Bringup.BeginAsync("pipe:A", Gpu, 0, default);
        s.LeaseSeconds.Should().Be(120);
        s.HoldBlackOnRelease.Should().BeFalse();
        s.Probes.Should().Equal("p");
        await rig.Bringup.EndAsync("pipe:A", Gpu);
        (await rig.Bringup.BeginAsync("pipe:A", Gpu, 9999, default)).LeaseSeconds.Should().Be(600, "MaxLeaseSeconds caps the lease");
    }

    [Fact]
    public async Task Session_belongs_to_the_connection_that_began_it()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        var e = rig.Entry(Gpu);
        e.BringupLease.Should().Be("pipe:A");
        rig.Loop.IsBringupHeld(e.Descriptor.Id).Should().BeTrue();
        DtoMapper.ToDto(e).State.Should().Be(ControllerStates.BringUp);

        var other = new Func<Task>[]
        {
            () => rig.Bringup.BeginAsync("pipe:B", Gpu, 60, default),
            () => rig.Bringup.RunProbeAsync("pipe:B", Gpu, "p", default),
            () => rig.Bringup.BeginMarkerAsync("pipe:B", new MarkerDto(Gpu, 0, 1, "#000000"), default),
            () => rig.Bringup.EndMarkerAsync("pipe:B", Gpu, default),
            () => rig.Bringup.RenewAsync("pipe:B", Gpu),
            () => rig.Bringup.EndAsync("pipe:B", Gpu),
        };
        foreach (var act in other)
        {
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another connection*");
        }

        await rig.Bringup.DisconnectAsync("pipe:B");
        rig.Bringup.Sessions.Should().Equal((e.Descriptor.Id, "pipe:A"));
        fake.Log.Should().Equal($"{Gpu} open");

        (await rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default)).Ok.Should().BeTrue();
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default); // the owner's second Begin renews
        fake.Log.Count(l => l == $"{Gpu} open").Should().Be(1);

        var end = await rig.Bringup.EndAsync("pipe:A", Gpu);
        end.AlreadyEnded.Should().BeFalse();
        end.Release!.Fields["release"].Should().Be("done");
        (await rig.Bringup.EndAsync("pipe:A", Gpu)).AlreadyEnded.Should().BeTrue();
        fake.Releases.Should().Be(1);
        e.BringupLease.Should().BeNull();
        rig.Loop.IsBringupHeld(e.Descriptor.Id).Should().BeFalse();
        await rig.Bringup.BeginAsync("pipe:B", Gpu, 60, default);
        rig.Bringup.Sessions.Should().Equal((e.Descriptor.Id, "pipe:B"));
    }

    [Fact]
    public async Task Disconnect_ends_only_that_connections_sessions()
    {
        var log = new ConcurrentQueue<string>();
        var gpu = new NativeFake(DeviceClass.MsiGpu, Gpu, log);
        var ene = new NativeFake(DeviceClass.EneDram, "ene:1", log);
        await using var rig = await Rig.StartAsync(gpu, ene);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        await rig.Bringup.BeginAsync("pipe:B", "ene:1", 60, default);
        await rig.Bringup.DisconnectAsync("pipe:A");
        await rig.Bringup.DisconnectAsync("pipe:A");
        gpu.Releases.Should().Be(1);
        ene.Releases.Should().Be(0);
        rig.Bringup.Sessions.Select(s => s.Owner).Should().Equal("pipe:B");
        rig.Entry("ene:1").BringupLease.Should().Be("pipe:B");
    }

    [Fact]
    public async Task Lease_expiry_ends_the_session_once_and_renew_extends_it()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 10, default);
        rig.Time.Advance(TimeSpan.FromSeconds(8));
        (await rig.Bringup.RenewAsync("pipe:A", Gpu)).ExpiresAt.Should().Be(rig.Time.GetUtcNow().AddSeconds(10));
        rig.Time.Advance(TimeSpan.FromSeconds(8));
        fake.Releases.Should().Be(0);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => fake.Releases == 1 && rig.Bringup.Sessions.Count == 0);
        rig.Time.ActiveTimers.Should().Be(0);
        rig.Entry(Gpu).BringupLease.Should().BeNull();

        var probe = () => rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        await probe.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no bring-up session*");
        (await rig.Bringup.EndAsync("pipe:A", Gpu)).AlreadyEnded.Should().BeTrue();
        fake.Releases.Should().Be(1);
        rig.Journal.Recent[^1].Summary.Should().StartWith("lease expired");
    }

    [Fact]
    public async Task End_during_a_running_step_waits_for_it_and_releases_once()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu) { StepGate = new SemaphoreSlim(0) };
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 5, default);
        var probe = rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        (await fake.StepEntered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        var end = rig.Bringup.EndAsync("pipe:A", Gpu);
        rig.Time.Advance(TimeSpan.FromSeconds(10)); // the lease timer was stopped by End
        await rig.Bringup.DisconnectAsync("pipe:A"); // already ending: no second release
        end.IsCompleted.Should().BeFalse("the release waits for the running step");
        fake.Releases.Should().Be(0);

        fake.StepGate.Release();
        (await probe).Ok.Should().BeTrue();
        (await end).AlreadyEnded.Should().BeFalse();
        fake.Releases.Should().Be(1);
        fake.Log.Should().Equal($"{Gpu} open", $"{Gpu} probe p", $"{Gpu} probe-done", $"{Gpu} release");
    }

    [Fact]
    public async Task Expiry_during_a_running_step_waits_for_it_and_a_racing_end_is_a_no_op()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu) { StepGate = new SemaphoreSlim(0) };
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 5, default);
        var probe = rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        (await fake.StepEntered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        (await rig.Bringup.EndAsync("pipe:A", Gpu)).AlreadyEnded.Should().BeTrue("expiry already owns the end");
        await rig.Bringup.EndAllAsync("host stop");
        fake.Releases.Should().Be(0);

        fake.StepGate.Release();
        (await probe).Ok.Should().BeTrue();
        await WaitUntilAsync(() => fake.Releases == 1);
        await Task.Delay(50);
        fake.Releases.Should().Be(1);
        fake.Log.Should().Equal($"{Gpu} open", $"{Gpu} probe p", $"{Gpu} probe-done", $"{Gpu} release");
    }

    [Fact]
    public async Task Failed_step_releases_and_ends_the_session_but_a_malformed_request_does_not()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu) { ProbeError = new ArgumentException("bad probe") };
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        var bad = () => rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        await bad.Should().ThrowAsync<ArgumentException>();
        rig.Bringup.Sessions.Should().ContainSingle();

        fake.ProbeError = new InvalidOperationException("bus refused");
        var r = await rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        r.Ok.Should().BeFalse();
        r.SessionEnded.Should().BeTrue();
        r.Fields["comm"].Should().Be("error(exception)");
        fake.Releases.Should().Be(1);
        rig.Bringup.Sessions.Should().BeEmpty();
        rig.Entry(Gpu).BringupLease.Should().BeNull();

        fake.ProbeError = null;
        fake.NextProbe = new NativeProbeResult("p", false, "family read invalid", []) { Fields = new Dictionary<string, string> { ["comm"] = "ok" } };
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        (await rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default)).SessionEnded.Should().BeTrue();
        fake.Releases.Should().Be(2);
    }

    [Fact]
    public async Task A_writer_appearing_mid_session_skips_the_step_and_ends_the_session()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        rig.Guard.Verdicts[DeviceClass.MsiGpu] = GuardVerdict.Blocked("LEDKeeper2");
        var r = await rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        r.Ok.Should().BeFalse();
        r.Summary.Should().Contain("LEDKeeper2");
        r.Fields["comm"].Should().Be("not-attempted");
        fake.Log.Should().NotContain($"{Gpu} probe p");
        r.SessionEnded.Should().BeTrue();
    }

    [Fact]
    public async Task Hold_black_accepts_only_black_markers_before_any_packet()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(new FakeGuard().Enable(DeviceClass.MsiGpu), new BringupOptions { HoldBlackOnRelease = true }, fake);
        (await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default)).HoldBlackOnRelease.Should().BeTrue();
        foreach (var colour in new[] { "#FF0000", "#010000", "#FFFFFF" })
        {
            var act = () => rig.Bringup.BeginMarkerAsync("pipe:A", new MarkerDto(Gpu, 0, 1, colour), default);
            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only black*");
        }

        rig.Bringup.Sessions.Should().ContainSingle("a refused marker does not end the session");
        (await rig.Bringup.BeginMarkerAsync("pipe:A", new MarkerDto(Gpu, 0, 34, "#000000"), default)).Ok.Should().BeTrue();
        fake.Log.Should().Equal($"{Gpu} open", $"{Gpu} marker 0+34 #000000");
    }

    [Fact]
    public async Task Open_failure_ends_the_session_and_lifts_the_holds()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu) { OpenError = new InvalidOperationException("no device") };
        await using var rig = await Rig.StartAsync(fake);
        var begin = () => rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        await begin.Should().ThrowAsync<InvalidOperationException>().WithMessage("no device");
        rig.Bringup.Sessions.Should().BeEmpty();
        rig.Entry(Gpu).BringupLease.Should().BeNull();
        rig.Loop.IsBringupHeld(new ControllerId(Gpu)).Should().BeFalse();
        fake.Releases.Should().Be(1, "the adapter decides; a never-entered GPU sends nothing on release");
        rig.Time.ActiveTimers.Should().Be(0);

        fake.OpenError = null;
        await begin();
        rig.Bringup.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Host_stop_ends_sessions_gpu_then_ene_then_aura()
    {
        var log = new ConcurrentQueue<string>();
        var aura = new NativeFake(DeviceClass.AsusAura, "aura:1", log);
        var ene = new NativeFake(DeviceClass.EneDram, "ene:1", log);
        var gpu = new NativeFake(DeviceClass.MsiGpu, Gpu, log);
        await using var rig = await Rig.StartAsync(aura, ene, gpu);
        foreach (var id in new[] { "aura:1", "ene:1", Gpu })
        {
            await rig.Bringup.BeginAsync("local", id, 60, default);
        }

        await rig.Bringup.EndAllAsync("host stop");
        log.Where(l => l.EndsWith(" release", StringComparison.Ordinal)).Should().Equal($"{Gpu} release", "ene:1 release", "aura:1 release");
        AdapterHost.StopOrder(DeviceClass.MsiGpu).Should().BeLessThan(AdapterHost.StopOrder(DeviceClass.EneDram));
        AdapterHost.StopOrder(DeviceClass.EneDram).Should().BeLessThan(AdapterHost.StopOrder(DeviceClass.AsusAura));
    }

    [Fact]
    public async Task Verified_native_writer_pauses_for_the_session_and_resumes_only_when_clear()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu, verified: true);
        await using var rig = await Rig.StartAsync(fake);
        var e = rig.Entry(Gpu);
        e.IsActive.Should().BeTrue();
        e.Writer!.Paused.Should().BeFalse();

        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        e.Writer!.Paused.Should().BeTrue("no scene frame reaches a controller in a bring-up session");
        await rig.Adapters.RecheckAsync(default);
        e.Writer!.Paused.Should().BeTrue("a recheck keeps the bring-up pause");
        await rig.Bringup.EndAsync("pipe:A", Gpu);
        e.Writer!.Paused.Should().BeFalse();

        fake.Log.Count(l => l == $"{Gpu} writer-open").Should().Be(2, "End reopens the writer's device state the release ended");

        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        rig.Guard.Verdicts[DeviceClass.MsiGpu] = GuardVerdict.Contended("LEDKeeper2");
        await rig.Bringup.EndAsync("pipe:A", Gpu);
        e.IsActive.Should().BeFalse("the class is not Clear: the writer is detached and opened afresh once it is");
        e.Verdict.Should().Be(GuardVerdict.Contended("LEDKeeper2"));
        fake.Log.Count(l => l == $"{Gpu} writer-open").Should().Be(2);
        rig.Guard.Verdicts.Clear();
        await rig.Adapters.RecheckAsync(default);
        e.IsActive.Should().BeTrue();
        e.Writer!.Paused.Should().BeFalse();
        fake.Log.Count(l => l == $"{Gpu} writer-open").Should().Be(3);
    }

    [Fact]
    public async Task Leased_controller_without_a_writer_is_not_opened_by_a_scan()
    {
        // Codex review: ControlVerified with an initially offline controller; the lease, not a periodic scan, owns it
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu, verified: true) { WriterOpenError = new IOException("offline") };
        await using var rig = await Rig.StartAsync(fake);
        var e = rig.Entry(Gpu);
        e.IsActive.Should().BeFalse();
        fake.WriterOpenError = null;
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);

        rig.Guard.Verdicts[DeviceClass.MsiGpu] = GuardVerdict.Contended("LEDKeeper2");
        await rig.Adapters.RecheckAsync(default);
        rig.Guard.Verdicts.Clear();
        await rig.Adapters.RecheckAsync(default);
        await rig.Adapters.RescanAsync(default);
        e.IsActive.Should().BeFalse("the session's lease owns the hardware");
        DtoMapper.ToDto(e).State.Should().Be(ControllerStates.BringUp);
        fake.Log.Count(l => l == $"{Gpu} writer-open").Should().Be(1, "only the failed open before the session");

        await rig.Bringup.EndAsync("pipe:A", Gpu);
        await rig.Adapters.RescanAsync(default);
        e.IsActive.Should().BeTrue("after the session the normal scan opens it");
        fake.Log.Count(l => l == $"{Gpu} writer-open").Should().Be(2);
    }

    [Fact]
    public async Task Verified_gpu_writer_reopens_after_the_session_and_writes_again()
    {
        // Codex review: the session release ends host mode (0x06 <- 1); the resumed writer must re-enter before its frames
        var nv = new HostFakeNvapi();
        nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var sleeps = new ConcurrentQueue<TimeSpan>();
        var gpu = new MsiNv5000Adapter(nv, new MsiGpuOptions { ControlVerified = true }, sleep: sleeps.Enqueue);
        await using var rig = await Rig.StartWithAsync(new FakeGuard().Enable(DeviceClass.MsiGpu), null, new InstanceFactory(gpu));
        var e = rig.Adapters.Controllers.Single();
        var id = e.Descriptor.Id;
        var writer = e.Writer!;
        nv.Log.Should().Equal(HostFakeNvapi.FamilyRead, HostFakeNvapi.Enter);
        await PublishAsync(rig, e, new Rgb(1, 1, 1), 1);

        await rig.Bringup.BeginAsync("pipe:A", id.Value, 60, default);
        (await rig.Bringup.BeginMarkerAsync("pipe:A", new MarkerDto(id.Value, 0, 1, "#FF0000"), default)).Ok.Should().BeTrue();
        (await rig.Bringup.EndMarkerAsync("pipe:A", id.Value, default)).Ok.Should().BeTrue();
        var end = await rig.Bringup.EndAsync("pipe:A", id.Value);
        end.Release!.Fields["release"].Should().Be("done");
        sleeps.Should().Equal(TimeSpan.FromMilliseconds(100));
        nv.Log.TakeLast(2).Should().Equal(HostFakeNvapi.Release, HostFakeNvapi.Enter);

        e.Writer.Should().BeSameAs(writer, "reopened in place");
        writer.Paused.Should().BeFalse();
        await PublishAsync(rig, e, new Rgb(2, 2, 2), 2);
        var stats = writer.GetStats();
        (stats.Written, stats.Errors).Should().Be((2, 0));
        nv.Log.Last().Should().StartWith("W 68 04 0100020202");
        nv.Log.Count(l => l == HostFakeNvapi.Enter).Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Relinquishing_end_never_reopens_the_writer(bool hostStop)
    {
        var nv = new HostFakeNvapi();
        nv.Reads.Enqueue((0, [0x91, 0x50, 0x00, 0x09], 4));
        var gpu = new MsiNv5000Adapter(nv, new MsiGpuOptions { ControlVerified = true }, sleep: _ => { });
        await using var rig = await Rig.StartWithAsync(new FakeGuard().Enable(DeviceClass.MsiGpu), null, new InstanceFactory(gpu));
        var e = rig.Adapters.Controllers.Single();
        await rig.Bringup.BeginAsync("pipe:A", e.Descriptor.Id.Value, 60, default);
        if (hostStop)
        {
            rig.Adapters.BeginStop();
            await rig.Bringup.EndAllAsync("host stop");
        }
        else
        {
            await rig.Bringup.EndClassAsync(DeviceClass.MsiGpu, "takeover switched off");
            await rig.Guard.SetTakeoverAsync(DeviceClass.MsiGpu, false, default);
        }

        e.IsActive.Should().BeFalse();
        nv.Log.Last().Should().Be(HostFakeNvapi.Release);
        await rig.Adapters.RecheckAsync(default);
        await rig.Adapters.RescanAsync(default);
        e.IsActive.Should().BeFalse();
        e.Verdict.Writer.Should().Be(hostStop ? "host stop" : "takeover off for MsiGpu");
        nv.Log.Count(l => l == HostFakeNvapi.Enter).Should().Be(1, "nothing re-enters a relinquished GPU");
        nv.Log.Count(l => l == HostFakeNvapi.Release).Should().Be(1, "the writer close after the release sends nothing more");
    }

    [Fact]
    public async Task Step_cancelled_while_waiting_leaves_the_session_untouched()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu) { StepGate = new SemaphoreSlim(0) };
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        var first = rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        (await fake.StepEntered.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        using var cts = new CancellationTokenSource();
        var second = rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", cts.Token);
        await cts.CancelAsync();
        (await CancelledAsync(second)).Should().BeTrue();

        var cancelledNow = () => rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", cts.Token);
        fake.StepGate.Release();
        (await first).Ok.Should().BeTrue();
        await cancelledNow.Should().ThrowAsync<OperationCanceledException>();
        fake.Releases.Should().Be(0);
        rig.Bringup.Sessions.Should().ContainSingle();
        fake.Log.Count(l => l == $"{Gpu} probe p").Should().Be(1, "neither cancelled step ran");
        fake.StepGate = null;
        (await rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Step_cancelled_inside_its_io_releases_and_ends_the_session()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu, verified: true) { StepGate = new SemaphoreSlim(0) };
        await using var rig = await Rig.StartAsync(fake);
        var e = rig.Entry(Gpu);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        using var cts = new CancellationTokenSource();
        var step = rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", cts.Token);
        (await fake.StepEntered.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        await cts.CancelAsync();
        (await CancelledAsync(step)).Should().BeTrue();

        fake.Releases.Should().Be(1, "released with an uncancelled token, once");
        rig.Bringup.Sessions.Should().BeEmpty();
        rig.Loop.IsBringupHeld(new ControllerId(Gpu)).Should().BeFalse();
        rig.Journal.Recent.Select(x => (x.Step, x.Ok)).TakeLast(2).Should().Equal(("p", false), ("release", true));
        rig.Journal.Recent[^2].Fields["comm"].Should().Be("unknown(cancelled)");
        e.Writer!.Paused.Should().BeFalse("not a relinquish: the verified writer is reopened");
        fake.Log.Count(l => l == $"{Gpu} writer-open").Should().Be(2);
        (await rig.Bringup.EndAsync("pipe:A", Gpu)).AlreadyEnded.Should().BeTrue();
        fake.Releases.Should().Be(1);
    }

    private static async Task<bool> CancelledAsync(Task<ProbeResultDto> step)
    {
        try
        {
#pragma warning disable VSTHRD003 // the step was started by this test method; awaiting it here only observes its outcome
            await step;
#pragma warning restore VSTHRD003
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task PublishAsync(Rig rig, ControllerEntry e, Rgb color, long written)
    {
        await Task.Delay(60); // the GPU's 20 fps complete-frame budget
        var box = rig.Bus[e.Descriptor.Id]!;
        box.Back.Zones[0].AsSpan().Fill(color);
        rig.Bus.Publish(e.Descriptor.Id, box.Back);
        await WaitUntilAsync(() => e.Writer!.GetStats().Written >= written);
    }

    [Fact]
    public async Task Journal_records_owner_steps_and_the_evidence_defaults()
    {
        var fake = new NativeFake(DeviceClass.MsiGpu, Gpu);
        await using var rig = await Rig.StartAsync(fake);
        await rig.Bringup.BeginAsync("pipe:A", Gpu, 60, default);
        var r = await rig.Bringup.RunProbeAsync("pipe:A", Gpu, "p", default);
        r.Fields.Should().Contain(new Dictionary<string, string>
        {
            ["comm"] = "ok",
            ["readback"] = "none",
            ["playback"] = "unobserved",
            ["optical"] = "pending-owner",
            ["topology"] = "unknown",
        });
        r.Fields.Values.Should().NotContain(v => v.Contains("verified", StringComparison.OrdinalIgnoreCase) || v == "lit" || v == "working");
        r.Rows.Single().ResponseHex.Should().Be("91500009");
        await rig.Bringup.EndAsync("pipe:A", Gpu);

        var j = rig.Journal.Recent;
        j.Select(x => x.Step).Should().Equal("begin", "p", "release");
        j.Should().OnlyContain(x => x.Owner == "pipe:A" && x.Class == "MsiGpu" && x.ControllerId == Gpu);
        j[0].Fields["controlVerified"].Should().Be("false");
        j[0].Fields["holdBlackOnRelease"].Should().Be("false");
        j.Should().OnlyContain(x => x.Fields["optical"] == "pending-owner" && x.Fields["playback"] == "unobserved");
        fake.Verified.Should().BeFalse("no session result sets ControlVerified");
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(FakeGuard guard, FrameBus bus, AdapterHost adapters, BringupOptions options)
        {
            Guard = guard;
            Bus = bus;
            Adapters = adapters;
            Loop = new RenderLoop(EffectRegistry.Default, bus);
            Bringup = new BringupManager(adapters, Loop, guard, options, Journal, Time);
        }

        public FakeGuard Guard { get; }

        public FrameBus Bus { get; }

        public AdapterHost Adapters { get; }

        public RenderLoop Loop { get; }

        public ManualTimeProvider Time { get; } = new();

        public ProbeLog Journal { get; } = new(null);

        public BringupManager Bringup { get; }

        public static Task<Rig> StartAsync(params NativeFake[] fakes) =>
            StartAsync(new FakeGuard().Enable(fakes.Select(f => f.Class).ToArray()), null, fakes);

        public static Task<Rig> StartAsync(FakeGuard guard, BringupOptions? options, params NativeFake[] fakes) =>
            StartWithAsync(guard, options, fakes.Select(f => (IDeviceAdapterFactory)new NativeFakeFactory(f)).ToArray());

        public static async Task<Rig> StartWithAsync(FakeGuard guard, BringupOptions? options, params IDeviceAdapterFactory[] factories)
        {
            var bus = new FrameBus();
            var host = new AdapterHost(factories, null!, guard, bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
            await host.StartAsync(CancellationToken.None);
            return new Rig(guard, bus, host, options ?? new BringupOptions());
        }

        public ControllerEntry Entry(string id) => Adapters.Controllers.Single(c => c.Descriptor.Id.Value == id);

        public async ValueTask DisposeAsync()
        {
            await Bringup.DisposeAsync();
            await Adapters.StopAsync();
            Bus.Dispose();
        }
    }
}

/// <summary>NATIVE design §8: shared MSI bookkeeping, the ASUS HAL process row and informational candidates.</summary>
public sealed class NativeWriterGuardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Msi_service_is_restored_only_when_neither_gpu_nor_ene_takeover_needs_it(bool gpuFirst)
    {
        var sys = new FakeSystem().Service("Mystic_Light_Service");
        using var guard = new WriterGuard(new ProcessScanner(sys), sys, new TakeoverStore((string?)null));
        guard.Check(DeviceClass.EneDram).Should().Be(GuardVerdict.Blocked("Mystic_Light_Service"));
        var (first, second) = gpuFirst ? (DeviceClass.MsiGpu, DeviceClass.EneDram) : (DeviceClass.EneDram, DeviceClass.MsiGpu);
        (await guard.SetTakeoverAsync(first, true, default)).StoppedServices.Should().Equal("Mystic_Light_Service");
        (await guard.SetTakeoverAsync(second, true, default)).StoppedServices.Should().BeEmpty("already stopped and recorded once");
        guard.Check(DeviceClass.MsiGpu).State.Should().Be(GuardState.Clear);
        guard.Check(DeviceClass.EneDram).State.Should().Be(GuardState.Clear);

        await guard.SetTakeoverAsync(first, false, default);
        sys["Mystic_Light_Service"].Should().Be((false, "Disabled"), $"{second} takeover still needs it stopped");
        await guard.SetTakeoverAsync(second, false, default);
        sys["Mystic_Light_Service"].Should().Be((true, "Automatic"));
        sys.Calls.Count(c => c == "start Mystic_Light_Service").Should().Be(1);
    }

    [Fact]
    public async Task Asus_hal_is_a_process_writer_and_its_windows_host_is_never_stopped()
    {
        var sys = new FakeSystem().Process(KnownWriters.AsusHalProcessName).Service("DcomLaunch").Service("RpcSs");
        using var guard = new WriterGuard(new ProcessScanner(sys), sys, new TakeoverStore((string?)null));
        guard.Check(DeviceClass.AsusAura).Should().Be(GuardVerdict.Blocked(KnownWriters.AsusHalProcessName));
        guard.Check(DeviceClass.EneDram).Should().Be(GuardVerdict.Blocked(KnownWriters.AsusHalProcessName));
        var r = await guard.SetTakeoverAsync(DeviceClass.AsusAura, true, default);
        r.StoppedServices.Should().BeEmpty();
        r.ProcessesToClose.Should().Equal(KnownWriters.AsusHalProcessName);
        sys.Calls.Should().NotContain(c => c.StartsWith("stop ", StringComparison.Ordinal) || c.StartsWith("starttype ", StringComparison.Ordinal));
        sys["DcomLaunch"].Should().Be((true, "Automatic"));
        sys["RpcSs"].Should().Be((true, "Automatic"));
    }

    [Fact]
    public void Candidates_are_reported_but_never_change_a_verdict()
    {
        var sys = new FakeSystem().Service("ArmouryCrateService").Process("asComSvc");
        using var guard = new WriterGuard(new ProcessScanner(sys), sys, new TakeoverStore((string?)null));
        guard.Check(DeviceClass.AsusAura).State.Should().Be(GuardState.Clear);
        guard.Check(DeviceClass.EneDram).State.Should().Be(GuardState.Clear);
        guard.RunningCandidates().Select(c => c.Name).Should().BeEquivalentTo("ArmouryCrateService", "asComSvc");
        using var none = new WriterGuard(new ProcessScanner(new FakeSystem()));
        none.RunningCandidates().Should().BeEmpty();
    }
}

/// <summary>Host-level overnight behaviour (addendum §4/§9) and connection ownership over the real pipe.</summary>
[Collection("Host")]
public sealed class NativeHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_stop_ends_sessions_before_closing_and_defers_restore_under_hold_black(bool holdBlack)
    {
        var guard = new FakeGuard().Enable(DeviceClass.MsiGpu);
        var fake = new NativeFake(DeviceClass.MsiGpu, "gpu:1");
        var host = await TestHost.StartAsync(
            s =>
            {
                s.AddSingleton<IWriterGuard>(guard);
                s.AddSingleton<IDeviceAdapterFactory>(new NativeFakeFactory(fake));
            },
            o => o.Bringup.HoldBlackOnRelease = holdBlack);
        await host.Engine.Bringup.BeginAsync("local", "gpu:1", 60, default);
        await host.DisposeAsync();

        fake.Log.Should().Equal("gpu:1 open", "gpu:1 release", "gpu:1 dispose");
        guard.Restores.Should().Be(holdBlack ? 0 : 1, "hold-black keeps vendor services stopped and their records kept");
    }

    [Fact]
    public async Task Takeover_off_ends_the_session_and_closes_native_writers_before_the_guard_restores()
    {
        var events = new ConcurrentQueue<string>();
        var guard = new FakeGuard(events).Enable(DeviceClass.MsiGpu, DeviceClass.EneDram);
        var gpu = new NativeFake(DeviceClass.MsiGpu, "gpu:1", events, verified: true);
        var ene = new NativeFake(DeviceClass.EneDram, "ene:1", events, verified: true);
        await using var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IWriterGuard>(guard);
            s.AddSingleton<IDeviceAdapterFactory>(new NativeFakeFactory(gpu));
            s.AddSingleton<IDeviceAdapterFactory>(new NativeFakeFactory(ene));
        });
        await host.Engine.Bringup.BeginAsync("local", "gpu:1", 60, default);
        var r = await host.Get<IRgbService>().SetTakeoverAsync("MsiGpu", false);
        r.Enabled.Should().BeFalse();

        var order = events.ToList();
        order.Should().ContainInOrder("gpu:1 writer-open", "gpu:1 open", "gpu:1 release", "gpu:1 close", "guard takeover off MsiGpu");
        order.Count(l => l == "gpu:1 writer-open").Should().Be(1, "neither the session end nor the recheck reopens it while takeover is off");
        order.Should().NotContain("ene:1 close", "the shared MSI service bookkeeping is the guard's; ENE keeps its writer");
        var entries = host.Engine.Adapters.Controllers;
        entries.Single(c => c.Descriptor.Id.Value == "gpu:1").Verdict.Should().Be(GuardVerdict.Blocked("takeover off for MsiGpu"));
        entries.Single(c => c.Descriptor.Id.Value == "ene:1").IsActive.Should().BeTrue();
        await host.Engine.RescanAsync(default);
        events.Count(l => l == "gpu:1 writer-open").Should().Be(1);
    }

    [Fact]
    public async Task Pipe_sessions_belong_to_their_connection()
    {
        var guard = new FakeGuard().Enable(DeviceClass.MsiGpu);
        var fake = new NativeFake(DeviceClass.MsiGpu, "gpu:1");
        await using var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IWriterGuard>(guard);
            s.AddSingleton<IDeviceAdapterFactory>(new NativeFakeFactory(fake));
        });
        var (rpcA, a, pipeA) = await ConnectAsync(host);
        var (rpcB, b, pipeB) = await ConnectAsync(host);
        try
        {
            await a.BeginBringupAsync("gpu:1", 60);
            var refused = new Func<Task>[]
            {
                () => b.BeginBringupAsync("gpu:1", 60),
                () => b.RunNativeProbeAsync("gpu:1", "p"),
                () => b.BeginMarkerAsync(new MarkerDto("gpu:1", 0, 1, "#000000")),
                () => b.EndBringupAsync("gpu:1"),
            };
            foreach (var act in refused)
            {
                await act.Should().ThrowAsync<RemoteInvocationException>().WithMessage("*another connection*");
            }

            (await b.ListControllersAsync()).Single(c => c.Id == "gpu:1").State.Should().Be(ControllerStates.BringUp, "queries see the session");
            rpcB.Dispose();
            await pipeB.DisposeAsync();
            await Task.Delay(300);
            fake.Releases.Should().Be(0, "another connection closing never ends this session");
            (await a.RunNativeProbeAsync("gpu:1", "p")).Ok.Should().BeTrue();
        }
        finally
        {
            rpcA.Dispose();
            await pipeA.DisposeAsync();
        }

        await BringupSessionTests.WaitUntilAsync(() => fake.Releases == 1);
        host.Engine.Bringup.Sessions.Should().BeEmpty();
    }

    private static async Task<(JsonRpc Rpc, IRgbService Svc, NamedPipeClientStream Pipe)> ConnectAsync(TestHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var svc = rpc.Attach<IRgbService>();
        rpc.StartListening();
        return (rpc, svc, pipe);
    }
}

/// <summary>NATIVE design §7.2 scene fields, native maps, and the owner's overnight black profile through the new mapper.</summary>
public sealed class NativeMappingTests
{
    private static AssignmentDto A(string? strategy = null, string? policy = null, string? frameSet = null) =>
        new([new ZoneRefDto("c", 0)], "StaticColor", ["#FF0000"], LoopPolicy: policy, Strategy: strategy, FrameSetId: frameSet);

    [Fact]
    public void Strategy_wins_over_the_legacy_policy_and_frames_need_a_set()
    {
        DtoMapper.FromDto(A()).Strategy.Should().Be(ContentStrategy.Vendor);
        DtoMapper.FromDto(A(policy: "Generic")).Strategy.Should().Be(ContentStrategy.Program);
        DtoMapper.FromDto(A(strategy: "program", policy: "LConnect")).Strategy.Should().Be(ContentStrategy.Program);
        var frames = DtoMapper.FromDto(A(strategy: "Frames", frameSet: "heartflow-gpu"));
        (frames.Strategy, frames.FrameSetId).Should().Be((ContentStrategy.Frames, "heartflow-gpu"));
        foreach (var bad in new[] { "1", "Bogus", "", "Vendor,Program" })
        {
            ((Action)(() => DtoMapper.FromDto(A(strategy: bad)))).Should().Throw<ArgumentException>(bad);
        }

        ((Action)(() => DtoMapper.FromDto(A(strategy: "Frames")))).Should().Throw<ArgumentException>();

        var dto = DtoMapper.ToDto(new Scene([frames])).Assignments.Single();
        (dto.Strategy, dto.FrameSetId, dto.LoopPolicy).Should().Be(("Frames", "heartflow-gpu", "Generic"));
    }

    [Fact]
    public void Overnight_black_profile_maps_unchanged()
    {
        var fixture = Path.Combine(PreviewTests.RepoRoot(), "tests", "Fixtures", "profiles", "blackout.json");

        var dir = Path.Combine(Path.GetTempPath(), "urgb-black", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(fixture, Path.Combine(dir, "active.json"));
            var profile = new ProfileStore(dir).LoadActive()!;
            var scene = DtoMapper.FromDto(profile.Scene);
            var a = scene.Assignments.Should().ContainSingle().Subject;
            a.EffectId.Should().Be("StaticColor");
            a.Strategy.Should().Be(ContentStrategy.Vendor, "LConnect maps to Vendor; the scene stays what the owner applied");
            a.Params.Colors.Should().Equal(Rgb.Black);
            a.Params.Brightness.Should().Be(0);
            a.Targets.Select(t => t.Controller.Value).Should().HaveCount(4).And.OnlyContain(id => id.StartsWith("lianli:rf:", StringComparison.Ordinal));
            var back = DtoMapper.ToDto(scene).Assignments.Single();
            (back.LoopPolicy, back.Strategy, back.FrameSetId).Should().Be(("LConnect", "Vendor", null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Native_maps_round_trip_and_a_damaged_file_leaves_controllers_unmapped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-maps", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, NativeMapStore.FileName);
        try
        {
            var store = new NativeMapStore(path);
            var id = new ControllerId("msigpu:0:53011462");
            store.Get(id).Should().BeNull();
            var map = new NativeControllerMap("E3m 2026-10-18", [new NativeZoneMap("Upper", [0, 1, 2]), new NativeZoneMap("Badge", [33], "Point")]);
            store.Save(id.Value, map);
            var got = new NativeMapStore(path).Get(id)!;
            got.Evidence.Should().Be("E3m 2026-10-18");
            got.Zones.Select(z => (z.Name, string.Join(",", z.Indices), z.Layout)).Should().Equal(("Upper", "0,1,2", "Linear"), ("Badge", "33", "Point"));
            store.Save(id.Value, null);
            store.Get(id).Should().BeNull();

            File.WriteAllText(path, "{ not json");
            store.Get(id).Should().BeNull();
            File.WriteAllText(path, "{\"schemaVersion\":2,\"controllers\":[]}");
            store.Get(id).Should().BeNull();
            File.WriteAllBytes(path, new byte[NativeMapStore.MaxFileBytes + 1]);
            store.Get(id).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
