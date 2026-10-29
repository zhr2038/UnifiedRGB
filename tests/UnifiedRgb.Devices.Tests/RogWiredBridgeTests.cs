using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogWiredBridgeTests
{
    [Fact]
    public async Task Retired_generation_fails_queued_frame_and_never_reaches_the_new_owner()
    {
        var bridge = new RogWiredLightingBridge(); var old = bridge.Attach();
        var previous = bridge.WriteAsync(new(255, 0, 0), default);
        bridge.Detach(old);
        await ((Func<Task>)(() => previous)).Should().ThrowAsync<IOException>();
        var fresh = bridge.Attach(); var next = bridge.WriteAsync(new(0, 255, 0), default);
        fresh.TryTake(out var request).Should().BeTrue(); request!.Colour.Should().Be(new Rgb(0, 255, 0));
        old.TryTake(out _).Should().BeFalse(); request.Completion.TrySetResult(); await next; bridge.Detach(fresh);
    }

    [Fact]
    public async Task Cancelled_queued_colour_is_marked_before_the_actor_can_send_it()
    {
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach();
        using var stop = new CancellationTokenSource();
        var pending = bridge.WriteAsync(new(255, 0, 0), stop.Token); stop.Cancel();
        await ((Func<Task>)(() => pending)).Should().ThrowAsync<OperationCanceledException>();
        owner.TryTake(out var request).Should().BeTrue(); request!.Cancelled.Should().BeTrue(); bridge.Detach(owner);
    }

    [Fact]
    public async Task Screen_toggle_does_not_restart_RGB_or_restore_its_previous_effect()
    {
        using var io = new RogWiredOledTests.OledFake(); using var connection = new RogWiredOledConnection(io, () => true);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var original = session.OriginalLighting!; var bank = session.OriginalStaticBank!;
        await session.StartOutputAsync(100, default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach();
        using var stop = new CancellationTokenSource(); var desired = true;
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, true, () => desired);
        await bridge.WriteAsync(new(255, 0, 0), default);
        Convert.FromHexString(io.Native.Payloads[0])[1].Should().Be(25); // Fresh active brightness, not forced100.
        desired = false;
        await Until(() => io.Brightness == 0);
        var before = io.NativeWrites;
        await Task.Delay(100); io.NativeWrites.Should().Be(before); // OLED-only toggle did not write lighting.
        await bridge.WriteAsync(new(0, 255, 0), default); io.NativeWrites.Should().Be(before + 1);
        desired = true; await Until(() => session.Frames == 2);
        stop.Cancel(); await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>();
        io.PendingReads.Should().Be(0); bridge.Detach(owner);
        await session.RestoreLightingAsync(original, bank, default); await session.RestoreAsync(0, default);
        io.Native.SameContent(original).Should().BeTrue(); io.Brightness.Should().Be(0);
        // Complete OLED uploads are never interleaved with a lighting setter.
        var activeUpload = false; var chunks = 0;
        foreach (var operation in io.Operations)
        {
            if (operation == (0x67, 0)) { activeUpload = true; chunks = 0; }
            if (operation == (0x67, 1) && ++chunks == 111) activeUpload = false;
            if (operation.Command == 0x51) activeUpload.Should().BeFalse();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, bound.Token);
    }

    [Fact]
    public async Task All_original_multi_packet_effect_chunks_and_inactive_static_bank_are_restored()
    {
        var payloads = new[] { new byte[14], new byte[16], new byte[7] }; payloads[0][0] = 80; payloads[0][1] = 25;
        var original = new RogKeyboardNativeState(5, payloads.Select(Convert.ToHexString).ToArray());
        var bank = RogProtocol.KeyboardColourFrame(new(120, 70, 20), 10, 30);
        using var io = new RogWiredOledTests.OledFake(); io.SetBanks(original, bank);
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        await bridge.WriteAsync(new(255, 255, 255), default); io.ActiveEffect.Should().Be(0);
        stop.Cancel(); await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>(); bridge.Detach(owner);
        await session.RestoreLightingAsync(session.OriginalLighting!, session.OriginalStaticBank!, default);
        io.ActiveEffect.Should().Be(5); io.Native.SameContent(original).Should().BeTrue();
        await session.SnapshotLightingAsync(default); session.OriginalStaticBank!.SameContent(bank).Should().BeTrue();
        io.DataPackets.Should().Be(0);
    }
}
