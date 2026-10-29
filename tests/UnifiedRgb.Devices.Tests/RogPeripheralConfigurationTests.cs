using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogPeripheralConfigurationTests
{
    [Fact]
    public async Task Configuration_release_restores_and_disposes_every_shared_lease_then_fresh_exit_reads_current_settings()
    {
        var hid = new RogPeripheralTests.FakeDevices();
        var originalMouse = hid.MouseState;
        var originalKeyboard = hid.KeyboardState;
        var adapter = new RogAdapter(hid, Options(hid));
        var descriptors = await adapter.DiscoverAsync(default);
        var keyboard = descriptors.Single(d => d.DeviceType == DeviceType.Keyboard);
        var mouse = descriptors.Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(keyboard.Id, default);
        await adapter.OpenAsync(mouse.Id, default);
        adapter.WriteFrame(keyboard.Id, new Rgb[][] { [new(1, 2, 3)] }).Should().BeTrue();
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [new(4, 5, 6)] }).Should().BeTrue();
        hid.MouseState.Should().NotBe(originalMouse);
        hid.KeyboardState.Color.Should().Be(new Rgb(1, 2, 3));

        await adapter.ReleaseForConfigurationAsync(default);
        hid.MouseState.Should().Be(originalMouse);
        hid.KeyboardState.Should().Be(originalKeyboard);
        hid.Disposes.Should().Be(hid.Opens).And.Be(3);
        var reports = hid.Writes.Count;
        var rescan = () => adapter.DiscoverAsync(default);
        await rescan.Should().ThrowAsync<ObjectDisposedException>();
        var reopen = () => adapter.OpenAsync(mouse.Id, default);
        await reopen.Should().ThrowAsync<ObjectDisposedException>();
        var write = () => adapter.WriteFrame(mouse.Id, new Rgb[][] { [Rgb.White] });
        write.Should().Throw<ObjectDisposedException>();
        hid.Writes.Should().HaveCount(reports);

        hid.MouseState = new(42, new(90, 80, 70));
        await using var fresh = new RogAdapter(hid, Options(hid));
        var freshMouse = (await fresh.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await fresh.OpenAsync(freshMouse.Id, default);
        fresh.WriteFrame(freshMouse.Id, new Rgb[][] { [new(9, 10, 11)] }).Should().BeTrue();
        hid.MouseState.Should().Be(new RogMouseStaticState(42, new(9, 10, 11)));
    }

    [Fact]
    public async Task Release_joins_inflight_io_before_disposal_and_allows_no_later_reports()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hid = new RogPeripheralTests.FakeDevices { ReadStarted = started, ReadRelease = unblock };
        var adapter = new RogAdapter(hid, MouseOptions(hid));
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        var opening = adapter.OpenAsync(mouse.Id, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var releasing = adapter.ReleaseForConfigurationAsync(default);
        adapter.ReleaseForConfigurationAsync(default).Should().BeSameAs(releasing);
        await Task.Delay(30);
        releasing.IsCompleted.Should().BeFalse();
        hid.Disposes.Should().Be(0);
        unblock.SetResult();
        await opening;
        await releasing.WaitAsync(TimeSpan.FromSeconds(5));
        hid.Disposes.Should().Be(hid.Opens);
        var reports = hid.Writes.Count;
        var rescan = () => adapter.DiscoverAsync(default);
        await rescan.Should().ThrowAsync<ObjectDisposedException>();
        hid.Writes.Should().HaveCount(reports);
    }

    [Fact]
    public async Task Bounded_restoration_failure_disposes_what_can_be_closed_but_cannot_be_reinterpreted_as_ready()
    {
        var hid = new RogPeripheralTests.FakeDevices();
        var adapter = new RogAdapter(hid, MouseOptions(hid));
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(mouse.Id, default);
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [Rgb.White] }).Should().BeTrue();
        hid.StallWrites = true;
        var reports = hid.Writes.Count;
        using var bounded = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var release = () => adapter.ReleaseForConfigurationAsync(bounded.Token);
        await release.Should().ThrowAsync<AggregateException>();
        hid.Disposes.Should().Be(hid.Opens);
        hid.Writes.Should().HaveCount(reports, "a timed-out restoration is never retried");
        var secondRelease = () => adapter.ReleaseForConfigurationAsync(default);
        await secondRelease.Should().ThrowAsync<AggregateException>();
        hid.Writes.Should().HaveCount(reports);
    }

    [Fact]
    public async Task Direct_keyboard_without_a_complete_restore_snapshot_fails_closed_instead_of_claiming_handoff()
    {
        var hid = new RogPeripheralTests.FakeDevices();
        var pin = RogReceiver.Discover(hid).Single().Key;
        var adapter = new RogAdapter(hid, new RogOptions
        {
            HidTimeoutMs = 20,
            Keyboards = [new() { ReceiverKey = pin, ReportId = 2, LedIds = [1], ControlVerified = true }],
        });
        var keyboard = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Keyboard);
        await adapter.OpenAsync(keyboard.Id, default);
        adapter.WriteFrame(keyboard.Id, new Rgb[][] { [Rgb.White] }).Should().BeTrue();
        var release = () => adapter.ReleaseForConfigurationAsync(default);
        await release.Should().ThrowAsync<AggregateException>();
        await release.Should().ThrowAsync<AggregateException>();
        hid.Disposes.Should().Be(hid.Opens);
    }

    [Fact]
    public async Task Absent_mouse_or_receiver_is_a_bounded_no_open_release()
    {
        var hid = new RogPeripheralTests.FakeDevices { Absent = true };
        var adapter = new RogAdapter(hid, new RogOptions { HidTimeoutMs = 20 });
        (await adapter.DiscoverAsync(default)).Should().BeEmpty();
        await adapter.ReleaseForConfigurationAsync(default);
        hid.Opens.Should().Be(0);
        hid.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Diagnostics_after_configuration_retirement_are_safe_and_do_not_reopen_channels()
    {
        var hid = new RogPeripheralTests.FakeDevices();
        var adapter = new RogAdapter(hid, MouseOptions(hid));
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(mouse.Id, default);
        adapter.FrameBudget(mouse.Id).Should().NotBeNull();
        await adapter.ReleaseForConfigurationAsync(default);
        var writes = hid.Writes.Count;
        var opens = hid.Opens;
        // StatusWriter may already have captured this controller before its owner retired.
        adapter.FrameBudget(mouse.Id).Should().BeNull();
        for (var i = 0; i < 100; i++) adapter.FrameBudget(mouse.Id).Should().BeNull();
        hid.Writes.Should().HaveCount(writes);
        hid.Opens.Should().Be(opens);
        hid.Disposes.Should().Be(opens);
    }
    private static RogOptions Options(IHidReportDevices hid)
    {
        var pin = RogReceiver.Discover(hid).Single().Key;
        return new RogOptions
        {
            HidTimeoutMs = 50,
            Keyboards = [new() { ReceiverKey = pin, ReportId = 2, WholeColorOnly = true, ControlVerified = true }],
            Mice = [new() { ReceiverKey = pin, ReportId = 3, ControlVerified = true }],
        };
    }

    private static RogOptions MouseOptions(IHidReportDevices hid) => new()
    {
        HidTimeoutMs = 50,
        Mice = [new() { ReceiverKey = RogReceiver.Discover(hid).Single().Key, ReportId = 3, ControlVerified = true }],
    };
}
