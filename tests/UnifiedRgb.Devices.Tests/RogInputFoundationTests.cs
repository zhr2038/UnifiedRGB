using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogInputFoundationTests
{
    private static HidCollectionInfo Events => new(@"\\?\hid#vid_0b05&pid_1ace&mi_03&col01#fake#guid", 0x0B05, 0x1ACE, 3, 0xFFC0, 1, 21, 0, 0);
    private static HidInputDescriptor Descriptor => new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, 0, 0, [4]);
    private static RogPairedDevice Target => new(0x1C25, 2, 4);
    private static RogReceiver Receiver => RogReceiver.Group(RogPeripheralTests.ReceiverCollections("INPUT")).Single();
    private static byte[] Frame(byte mask, byte type = 0x72)
    { var data = new byte[21]; data[0] = 4; data[1] = type; data[2] = mask; return data; }

    [Theory]
    [InlineData(0x61)] [InlineData(0x71)] [InlineData(0xFF)]
    public void Common_or_unknown_payloads_are_dropped_before_control_decoding(byte type)
    {
        var report = Frame(255, type); report.AsSpan(3).Fill(0xAB);
        RogControlNotificationParser.Classify(report).Should().Be(new RogControlNotification(RogControlNotificationKind.Dropped, 0));
    }

    [Fact]
    public void Only_exact_control_layouts_are_admitted()
    {
        RogControlNotificationParser.Classify(Frame(4)).Should().Be(new RogControlNotification(RogControlNotificationKind.LeverLevel, 4));
        var mode = Frame(0, 0x91); mode[4] = 7;
        RogControlNotificationParser.Classify(mode).Should().Be(new RogControlNotification(RogControlNotificationKind.Mode, 7));
        mode[20] = 1;
        RogControlNotificationParser.Classify(mode).Kind.Should().Be(RogControlNotificationKind.Malformed);
        RogControlNotificationParser.Classify(Frame(3)).Kind.Should().Be(RogControlNotificationKind.Malformed);
        RogControlNotificationParser.Classify(new byte[20]).Kind.Should().Be(RogControlNotificationKind.Malformed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(7)]
    public void Getter_echo_preserves_owner_modes_and_does_not_guess_names(byte value)
    {
        var reply = RogLeverProtocol.CurrentModeQuery(2); reply[5] = value;
        RogLeverProtocol.ParseCurrentMode(reply, 2).Mode.Should().Be(value);
        RogLeverProtocol.CurrentModeQuery(2).AsSpan(3).ToArray().Should().OnlyContain(x => x == 0);
    }

    [Fact]
    public void Getter_refusal_wrong_framing_and_unknown_values_never_become_volume_zero()
    {
        var refused = new byte[64]; refused[0] = 2; refused[1] = 0xFF; refused[2] = 0xAA;
        var invalid = RogLeverProtocol.CurrentModeQuery(2); invalid[5] = 8;
        var args = RogLeverProtocol.CurrentModeQuery(2); args[3] = 1;
        foreach (var reply in new[] { refused, invalid, args, new byte[65], RogLeverProtocol.EnabledModesQuery(2) })
        { var act = () => RogLeverProtocol.ParseCurrentMode(reply, 2); act.Should().Throw<InvalidDataException>(); }
        var wrongRoute = () => RogLeverProtocol.CurrentModeQuery(3); wrongRoute.Should().Throw<ArgumentOutOfRangeException>();
        var flags = RogLeverProtocol.EnabledModesQuery(2); foreach (var i in new[] { 0, 1, 3, 7 }) flags[5 + i] = 1;
        RogLeverProtocol.ParseEnabledModes(flags, 2).Modes.Should().Equal(0, 1, 3, 7);
        flags[6] = 2;
        var badFlag = () => RogLeverProtocol.ParseEnabledModes(flags, 2); badFlag.Should().Throw<InvalidDataException>();
        new RogLeverModeState(2).Function.Should().BeNull();
    }

    [Fact]
    public void Physical_container_is_opaque_and_not_derived_from_product_or_interface()
    {
        var id = Guid.NewGuid();
        var a = HidPhysicalContainer.FromPlatformContainerId(id, 1);
        var b = HidPhysicalContainer.FromPlatformContainerId(id, 2);
        a.SameContainer(b).Should().BeTrue();
        a.SameContainer(HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), 1)).Should().BeFalse();
        a.ToString().Should().NotContain(id.ToString());
        var empty = () => HidPhysicalContainer.FromPlatformContainerId(Guid.Empty, 1); empty.Should().Throw<ArgumentException>();
        var local = () => HidPhysicalContainer.FromPlatformContainerId(HidPhysicalContainer.LocalMachineContainer, 1); local.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("ordinary")] [InlineData("mi00")] [InlineData("drift")] [InlineData("output")]
    public void Unsafe_or_drifted_collection_fails_before_any_input_read(string scenario)
    {
        var devices = new Inputs(); var collection = Events;
        if (scenario == "ordinary") collection = collection with { UsagePage = 1 };
        if (scenario == "mi00") collection = collection with { Interface = 0, Path = collection.Path.Replace("mi_03", "mi_00", StringComparison.Ordinal) };
        if (scenario == "output") collection = collection with { OutputReportLength = 21 };
        if (scenario == "drift") devices.Handle.Description = new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, 0, 0, [5]);
        var act = () => HidVendorInputTransport.Open(devices, collection, RogKeyboardInputContract.EventRule, TimeSpan.FromMilliseconds(25));
        act.Should().Throw<Exception>(); devices.Handle.Reads.Should().Be(0);
        devices.Opens.Should().Be(scenario == "drift" ? 1 : 0);
        if (scenario == "drift") devices.Handle.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Exact_read_clears_private_buffer_and_idle_does_not_synthesize_release()
    {
        var devices = new Inputs(); devices.Handle.Reports.Writer.TryWrite(Frame(1));
        using var input = Open(devices);
        var output = new byte[21];
        (await input.ReadAsync(output, default)).Should().BeTrue(); output[2].Should().Be(1);
        devices.Handle.LastBuffer.ToArray().Should().OnlyContain(x => x == 0);
        (await input.ReadAsync(output, default)).Should().BeFalse();
        output.Should().OnlyContain(x => x == 0); input.IsClosed.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 4)] [InlineData(20, 4)] [InlineData(21, 5)]
    public async Task Detach_short_report_or_wrong_id_permanently_closes_input(int length, byte rid)
    {
        var devices = new Inputs(); var report = new byte[length]; if (length != 0) report[0] = rid;
        devices.Handle.Reports.Writer.TryWrite(report); using var input = Open(devices);
        var act = async () => await input.ReadAsync(new byte[21], default);
        if (length == 0) await act.Should().ThrowAsync<IOException>();
        else await act.Should().ThrowAsync<InvalidDataException>();
        input.IsClosed.Should().BeTrue(); devices.Handle.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Pending_read_cancellation_closes_handle_and_rejects_concurrent_read()
    {
        var devices = new Inputs(); using var input = Open(devices); using var stop = new CancellationTokenSource();
        var read = input.ReadAsync(new byte[21], stop.Token).AsTask();
        var concurrent = async () => await input.ReadAsync(new byte[21], default);
        await concurrent.Should().ThrowAsync<InvalidOperationException>();
        stop.Cancel(); var cancelled = async () => await read;
        await cancelled.Should().ThrowAsync<OperationCanceledException>(); input.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task Noncooperative_read_is_bounded_and_late_bytes_are_erased_without_publication()
    {
        var devices = new Inputs(); var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        devices.Handle.CustomRead = (memory, _) => { devices.Handle.LastBuffer = memory; return new(completion.Task); };
        using var input = Open(devices); var output = new byte[21];
        var act = async () => await input.ReadAsync(output, default);
        await act.Should().ThrowAsync<TimeoutException>(); input.IsClosed.Should().BeTrue();
        devices.Handle.LastBuffer.Span.Fill(0xAB); completion.SetResult(21);
        await completion.Task; // Erasure continuation must run even after the caller has stopped waiting.
        for (var i = 0; i < 100 && devices.Handle.LastBuffer.Span.ContainsAnyExcept((byte)0); i++) await Task.Delay(2);
        devices.Handle.LastBuffer.ToArray().Should().OnlyContain(x => x == 0); output.Should().OnlyContain(x => x == 0);
    }

    [Fact]
    public void Binding_requires_unique_fresh_physical_container_and_exact_pairing()
    {
        var time = TimeProvider.System; var containers = new Containers(time); var receiver = Receiver;
        var binding = RogKeyboardInputContract.Bind(receiver, Target, [Target], [Events], containers, time, TimeSpan.FromMinutes(3));
        binding.EventCollection.Should().Be(Events);
        var noPair = () => RogKeyboardInputContract.Bind(receiver, Target, [], [Events], containers, time, TimeSpan.FromMinutes(3));
        noPair.Should().Throw<DeviceBlockedException>();
        var duplicate = () => RogKeyboardInputContract.Bind(receiver, Target, [Target], [Events, Events], containers, time, TimeSpan.FromMinutes(3));
        duplicate.Should().Throw<DeviceBlockedException>();
        containers.OtherEvent = true;
        var mismatch = () => RogKeyboardInputContract.Bind(receiver, Target, [Target], [Events], containers, time, TimeSpan.FromMinutes(3));
        mismatch.Should().Throw<DeviceBlockedException>();
        containers.Unavailable = true;
        mismatch.Should().Throw<DeviceBlockedException>();
    }

    [Fact]
    public void Stale_future_or_nonpositive_metadata_ages_are_rejected()
    {
        var time = TimeProvider.System; var now = time.GetTimestamp();
        var future = HidContainerResolution.Resolved(HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), now + time.TimestampFrequency * 10));
        var stale = HidContainerResolution.Resolved(HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), now - time.TimestampFrequency * 600));
        foreach (var resolution in new[] { future, stale })
        { var act = () => RogKeyboardInputContract.Fresh(resolution, time, TimeSpan.FromMinutes(3), "test"); act.Should().Throw<DeviceBlockedException>(); }
        var invalid = () => RogKeyboardInputContract.Fresh(stale, time, TimeSpan.Zero, "test"); invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Observer_is_explicit_drops_key_bodies_and_needs_physical_neutral_for_edges()
    {
        var devices = new Inputs(); using var observer = new RogKeyboardInputObserver(devices, new Containers(TimeProvider.System));
        devices.Opens.Should().Be(0); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var seen = new List<RogInputObservation>();
        var task = observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]),
            (notice, _) => { seen.Add(notice); if (seen.Count == 5) stop.Cancel(); return ValueTask.CompletedTask; }, stop.Token);
        devices.Handle.Reports.Writer.TryWrite(Frame(99, 0x61));
        foreach (var level in new byte[] { 1, 0, 1, 1, 0 }) devices.Handle.Reports.Writer.TryWrite(Frame(level));
        var result = await task;
        seen.Count.Should().Be(5); seen.Select(x => x.PressEdge).Should().Equal(null, null, RogLeverDirection.Up, null, null);
        result.Discarded.Should().Be(1); result.ErrorType.Should().BeNull(); devices.Handle.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Observer_malformed_control_stops_and_never_publishes_an_action()
    {
        var devices = new Inputs(); using var observer = new RogKeyboardInputObserver(devices, new Containers(TimeProvider.System));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); var calls = 0;
        var task = observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]),
            (_, _) => { calls++; return ValueTask.CompletedTask; }, stop.Token);
        devices.Handle.Reports.Writer.TryWrite(Frame(3)); var result = await task;
        calls.Should().Be(0); result.Stop.Should().Be("input_fault"); result.ErrorType.Should().Be(nameof(InvalidDataException));
    }

    [Fact]
    public async Task Observer_slow_consumer_stops_with_bounded_queue_and_no_input_action()
    {
        var devices = new Inputs(); using var observer = new RogKeyboardInputObserver(devices, new Containers(TimeProvider.System));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var task = observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]),
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct), stop.Token);
        devices.Handle.Reports.Writer.TryWrite(Frame(0)); var result = await task;
        result.Stop.Should().Be("consumer_fault"); result.ErrorType.Should().Be(nameof(TimeoutException)); devices.Handle.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Observer_rejects_no_lifetime_and_stale_pairing_before_open()
    {
        var devices = new Inputs(); using var observer = new RogKeyboardInputObserver(devices, new Containers(TimeProvider.System));
        var noLifetime = () => observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]), (_, _) => ValueTask.CompletedTask, default);
        await noLifetime.Should().ThrowAsync<ArgumentException>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var noPair = () => observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([]), (_, _) => ValueTask.CompletedTask, stop.Token);
        await noPair.Should().ThrowAsync<DeviceBlockedException>(); devices.Opens.Should().Be(0);
    }

    [Fact]
    public void Native_input_factory_requests_read_only_shared_overlapped_access()
    {
        uint desired = 0, share = 0, flags = 0; var called = 0;
        var devices = new WinHidInputReportDevices(null, (_, a, s, f) =>
        { called++; desired = a; share = s; flags = f; return new SafeFileHandle(IntPtr.Zero, false); });
        var invalid = () => devices.OpenReadOnly(Events);
        invalid.Should().Throw<System.ComponentModel.Win32Exception>();
        called.Should().Be(1); desired.Should().Be(0x80000000u); share.Should().Be(3); flags.Should().Be(0x40000000u);
        var ordinary = () => devices.OpenReadOnly(Events with { UsagePage = 1 });
        ordinary.Should().Throw<InvalidOperationException>(); called.Should().Be(1);
        typeof(IHidInputReportHandle).GetMethods().Select(m => m.Name).Should().NotContain(n => n.Contains("Write", StringComparison.Ordinal) || n.Contains("Feature", StringComparison.Ordinal));
    }

    [Fact]
    public void Input_report_id_extraction_uses_each_descriptor_cap_record_not_data_payloads()
    {
        var caps = new byte[144]; caps[2] = 4; caps[74] = 5; var ids = new HashSet<byte>();
        HidInputCapsNative.AddReportIds(caps, 2, ids); ids.Should().BeEquivalentTo(new byte[] { 4, 5 });
        var shortCaps = () => HidInputCapsNative.AddReportIds(new byte[143], 2, ids);
        shortCaps.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Observer_detach_and_post_open_replacement_fail_without_an_action()
    {
        var devices = new Inputs(); using var observer = new RogKeyboardInputObserver(devices, new Containers(TimeProvider.System));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); var calls = 0;
        var task = observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]),
            (_, _) => { calls++; return ValueTask.CompletedTask; }, stop.Token);
        devices.Handle.Reports.Writer.TryWrite([]); var result = await task;
        result.Stop.Should().Be("input_fault"); result.ErrorType.Should().Be(nameof(IOException)); calls.Should().Be(0);

        var swapped = new Inputs(); var containers = new Containers(TimeProvider.System);
        swapped.OnOpen = () => containers.OtherEvent = true;
        using var other = new RogKeyboardInputObserver(swapped, containers);
        var replacement = () => other.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]), (_, _) => ValueTask.CompletedTask, stop.Token);
        await replacement.Should().ThrowAsync<DeviceBlockedException>(); swapped.Handle.Disposed.Should().BeTrue(); swapped.Handle.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Observer_dispose_closes_pending_handle_and_double_start_is_rejected()
    {
        var devices = new Inputs(); using var observer = new RogKeyboardInputObserver(devices, new Containers(TimeProvider.System));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task<RogInputObservationResult> Start() => observer.ObserveAsync(Receiver, Target, _ => Task.FromResult<IReadOnlyList<RogPairedDevice>>([Target]), (_, _) => ValueTask.CompletedTask, stop.Token);
        var first = Start(); var duplicate = () => Start(); await duplicate.Should().ThrowAsync<InvalidOperationException>();
        observer.Dispose(); var result = await first; result.Stop.Should().Be("cancelled"); devices.Handle.Disposed.Should().BeTrue();
    }

    private static HidVendorInputTransport Open(Inputs devices) => HidVendorInputTransport.Open(devices, Events,
        RogKeyboardInputContract.EventRule, TimeSpan.FromMilliseconds(25));

    private sealed class Containers(TimeProvider time) : IHidContainerResolver
    {
        private readonly Guid _shared = Guid.NewGuid(); private readonly Guid _other = Guid.NewGuid();
        public bool OtherEvent { get; set; } public bool Unavailable { get; set; }
        public HidContainerResolution Resolve(HidCollectionInfo collection) => Unavailable ? HidContainerResolution.Unavailable("test") :
            HidContainerResolution.Resolved(HidPhysicalContainer.FromPlatformContainerId(OtherEvent && collection.Interface == 3 ? _other : _shared, time.GetTimestamp()));
    }

    private sealed class Inputs : IHidInputReportDevices
    {
        public Input Handle { get; } = new(); public int Opens { get; private set; } public Action? OnOpen { get; set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => [Events];
        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection) { Opens++; OnOpen?.Invoke(); return Handle; }
    }

    private sealed class Input : IHidInputReportHandle
    {
        public HidInputDescriptor Description { get; set; } = Descriptor;
        public Channel<byte[]> Reports { get; } = Channel.CreateUnbounded<byte[]>();
        public Func<Memory<byte>, CancellationToken, ValueTask<int>>? CustomRead { get; set; }
        public Memory<byte> LastBuffer { get; set; } public int Reads { get; private set; } public bool Disposed { get; private set; }
        public HidInputDescriptor Describe() => Description;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++; LastBuffer = buffer;
            if (CustomRead is { } custom) return await custom(buffer, ct);
            var data = await Reports.Reader.ReadAsync(ct); data.CopyTo(buffer); return data.Length;
        }
        public void Dispose() => Disposed = true;
    }
}
