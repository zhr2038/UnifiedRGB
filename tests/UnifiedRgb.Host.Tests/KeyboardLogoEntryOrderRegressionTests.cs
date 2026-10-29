using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public void Native_fresh_pre_arm_selection_resets_expired_previous_entry_without_a_separate_not_armed_tick()
    {
        using var state = new PresentationState(logo: true);
        var presentation = state.Presentation;
        presentation.Latest(state.Status, pageEnabled: true).Should().BeSameAs(state.Logo);
        presentation.Stepped(true);
        state.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        presentation.Latest(state.Status, pageEnabled: true).Should().BeSameAs(state.Status);
        presentation.Stepped(true);

        // A new actor is not armed yet; no intervening Stepped(false) is assumed by the presentation selector.
        presentation.Latest(state.Status, pageEnabled: true, newEntry: true).Should().BeSameAs(state.Logo);
        state.Clock.Advance(TimeSpan.FromSeconds(10)); // the upload has not been acknowledged yet
        presentation.Latest(state.Status, pageEnabled: true).Should().BeSameAs(state.Logo);
        presentation.Stepped(true);
        state.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        presentation.Latest(state.Status, pageEnabled: true).Should().BeSameAs(state.Logo);
        state.Clock.Advance(TimeSpan.FromMilliseconds(1));
        presentation.Latest(state.Status, pageEnabled: true).Should().BeSameAs(state.Status);
    }

    [Fact]
    public async Task Ordinary_page_entry_uploads_logo_first_for_1200ms_then_current_case_status_without_native_capability()
    {
        var capability = new DeclaredPresentation(KeyboardNativePresentationSupport.Available);
        await using var rig = new PresentationRig(capability, mode: 7, policy: false, animation: false);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
        await rig.NextTick(); // the first upload and its acknowledgement completed
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        await rig.NextTick();
        rig.Control.ImageBegins.Should().Be(1);
        rig.Shows(rig.Logo).Should().BeTrue();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Control.ImageBegins == 2 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        capability.Calls.Should().Be(0, "ordinary application presentation needs no native firmware capability");
        rig.Control.Arms.Should().Be(0);
        rig.Timeline.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Ordinary_page_entry_holds_shared_animation_until_logo_expires_then_selects_its_current_position()
    {
        await using var rig = new PresentationRig(null, mode: 7, policy: false);
        rig.Timeline.At(1500);
        await rig.Owner.StartAsync(default);
        await rig.Prepared();
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
        await rig.NextTick();
        rig.Timeline.Reads.Should().Be(0, "neither the ordinary upload check nor lease wake may select animation before the logo");
        rig.Timeline.At(2500);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Control.ImageBegins == 2 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x33)));
        var animation = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        animation.Acknowledged.Should().Be(1, "the logo is not an authored animation frame");
        rig.Control.Arms.Should().Be(0);
    }

    [Fact]
    public async Task Ordinary_page_disable_reenable_starts_one_fresh_logo_on_the_same_owner()
    {
        await using var rig = new PresentationRig(null, mode: 7, policy: false, animation: false);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins == 1 && rig.Shows(rig.Logo));
        await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
        await Until(() => rig.Control.ImageBegins == 2 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        await rig.Owner.SetKeyboardDisplayEnabledAsync(false);
        var restores = rig.Control.NativeRestores;
        await Until(() => rig.Control.NativeRestores > restores);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.Owner.SetKeyboardDisplayEnabledAsync(true);
        await Until(() => rig.Control.ImageBegins == 3 && rig.Shows(rig.Logo));
        await rig.NextTick();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs - 1));
        await rig.NextTick();
        rig.Control.ImageBegins.Should().Be(3);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Control.ImageBegins == 4 && rig.Shows(rig.Owner.ArtworkPixels.ToArray()));
        rig.Factory.Opens.Should().Be(1);
        rig.Control.Arms.Should().Be(0);
    }
}
