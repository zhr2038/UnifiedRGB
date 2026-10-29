using System.Reflection;
using System.Runtime.CompilerServices;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

// Terminal Host stop of the actual owner (Astra P2 #1 recheck): the hosted service's StopAsync retires the animation, its running
// preparation is cancelled and its pending binding dropped, and the decode that ends afterwards starts nothing. The keyboard display
// is off (no route, so no session) and every hardware-facing dependency throws; the media is the held, uninterruptible stub.
public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public async Task Host_stop_cancels_the_running_preparation_and_admits_no_later_work()
    {
        var media = new HeldMedia();
        var screens = KeyboardAnimationScreens.Create(media.Source);
        var hardware = new StrongBox<int>();
        using var service = StoppableOwner(media, screens, hardware);
        try
        {
            await service.StartAsync(default);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await media.StartedAsync(1);
            var saved = File.ReadAllBytes(AnimationPath());

            await service.StopAsync(default).WaitAsync(Refusal);
            media.Calls[0].Token.IsCancellationRequested.Should().BeTrue("terminal Host stop cancels the running preparation");
            media.Active.Should().Be(1, "the uninterruptible decode is not killed; it keeps the one worker until it ends");
            await FluentActions.Awaiting(() => service.SetKeyboardDisplayAnimationAsync(Enable(AssetB))).Should().ThrowAsync<ObjectDisposedException>();
            await FluentActions.Awaiting(() => service.PreviewKeyboardDisplayAnimationAsync(
                new(AssetB, SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound))).Should().ThrowAsync<ObjectDisposedException>();

            media.Calls[0].Release();
            await UntilAsync(() => media.Calls[0].Ended, "the cancelled decode ended on its own");
            await Task.Delay(300); // time for any wrongly admitted decoder to start
            (media.Started, media.Active, media.MaxActive).Should().Be((1, 0, 1), "nothing starts after Host stop");
            File.ReadAllBytes(AnimationPath()).Should().Equal(saved, "the saved animation preference is kept as it was");
            (hardware.Value, ((KeyboardAnimationScreens)(object)screens).Mutations).Should().Be((0, 0));
        }
        finally { media.ReleaseAll(); }
    }

    [Fact]
    public async Task Host_stop_drops_the_pending_binding_and_the_retired_decode_ending_afterwards_starts_nothing()
    {
        var media = new HeldMedia();
        var screens = KeyboardAnimationScreens.Create(media.Source);
        var hardware = new StrongBox<int>();
        using var service = StoppableOwner(media, screens, hardware);
        try
        {
            await service.StartAsync(default);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await media.StartedAsync(1);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetB)); // A is retired; B waits for the one worker
            await Task.Delay(300);
            (media.Started, media.Calls[0].Token.IsCancellationRequested).Should().Be((1, true));

            await service.StopAsync(default).WaitAsync(Refusal);
            media.Active.Should().Be(1, "the retired decode still runs and still holds the one worker");
            media.Calls[0].Release(); // the retired decode ends only after Host stop
            await UntilAsync(() => media.Calls[0].Ended, "the retired decode ended on its own");
            await Task.Delay(300); // time for the dropped binding to start if it were still pending
            (media.Started, media.Active, media.MaxActive).Should().Be((1, 0, 1), "Host stop dropped the pending binding; it never starts");
            (hardware.Value, ((KeyboardAnimationScreens)(object)screens).Mutations).Should().Be((0, 0));
        }
        finally { media.ReleaseAll(); }
    }

    /// <summary>The actual owner with the keyboard display off and every hardware-facing dependency denied; the screen service only
    /// reads the scene and assets of <paramref name="media"/>.</summary>
    private KeyboardDisplayService StoppableOwner(HeldMedia media, IScreenService screens, StrongBox<int> hardware) => new(
        new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = new() { AnimationAdmitted = true } },
        DeniedHardware.Create<IRgbService>(hardware), screens, DeniedHardware.Create<IRogWiredOledFactory>(hardware),
        DeniedHardware.Create<IWriterGuard>(hardware), keyboardMedia: media, sharedTimeline: new FakeSharedTimeline(), time: new ManualTimeProvider());
}

/// <summary>A hardware-facing dependency that must not be used: every call is counted and throws.</summary>
public class DeniedHardware : DispatchProxy
{
    private StrongBox<int> _calls = null!;

    internal static T Create<T>(StrongBox<int> calls) where T : class
    {
        var proxy = DispatchProxy.Create<T, DeniedHardware>();
        ((DeniedHardware)(object)proxy)._calls = calls;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Interlocked.Increment(ref _calls.Value);
        throw new InvalidOperationException("hardware dependency used: " + targetMethod?.Name);
    }
}
