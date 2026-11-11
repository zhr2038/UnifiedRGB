using System.Reflection;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task OutsideCap_quiet_ticks_do_no_IO_or_artwork_selection_and_preserve_actual_observation_and_intent()
    {
        await using var rig = new CapRig(); rig.Io.Mode = 8; var evaluations = 0;
        byte[]? Latest() { evaluations++; return null; }
        (await rig.Actor.StepAsync(Latest, default)).Should().BeFalse();
        rig.Caps.Should().Be(1); var proof = rig.Link.PairingEvidence;
        var at = typeof(RogNativeMenuSource.Actor).GetField("_lastReadCapabilityAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var observedAt = at.GetValue(rig.Actor);
        var writes = rig.Io.Writes.Count;
        for (var i = 0; i < 33; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(3));
            (await rig.Actor.StepAsync(Latest, default)).Should().BeFalse();
        }
        rig.Io.Writes.Should().HaveCount(writes); rig.Caps.Should().Be(1); evaluations.Should().Be(0);
        rig.Input.Opens.Should().Be(0); rig.Input.Enumerations.Should().Be(0);
        rig.Link.PairingEvidence.Should().Be(proof); at.GetValue(rig.Actor).Should().Be(observedAt);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ((Func<Task>)(() => rig.Actor.StepAsync(Latest, cancelled.Token))).Should().ThrowAsync<OperationCanceledException>();
        rig.Io.Writes.Should().HaveCount(writes);
        rig.Source.RequestEnabled(true); // same-value explicit request is still a new intent
        await rig.Actor.StepAsync(Latest, default);
        rig.Caps.Should().Be(2); rig.Link.PairingEvidence!.Observations.Should().Be(proof!.Observations + 1);
        evaluations.Should().Be(0);
    }

    [Fact]
    public async Task OutsideCap_real_native_entry_after_100ms_requires_fresh_CAP_ARM_and_genuine_neutral_input()
    {
        await using var rig = new CapRig(); rig.Io.Mode = 8; var evaluations = 0;
        byte[]? Latest() { evaluations++; return null; }
        await rig.Actor.StepAsync(Latest, default); var proof = rig.Link.PairingEvidence;
        rig.Io.Mode = RogNativeMenuCodec.NativeMode;
        rig.Time.Advance(TimeSpan.FromMilliseconds(99));
        (await rig.Actor.StepAsync(Latest, default)).Should().BeFalse();
        rig.Caps.Should().Be(1); rig.Io.Arms.Should().Be(0); evaluations.Should().Be(0);
        rig.Link.PairingEvidence.Should().Be(proof);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await rig.Actor.StepAsync(Latest, default)).Should().BeFalse("control ARM needs no Host pixels");
        rig.Caps.Should().Be(2); rig.Io.Arms.Should().Be(1); evaluations.Should().Be(1);
        rig.Actor.ControlArmed.Should().BeTrue(); rig.Source.IsPresent.Should().BeTrue(); rig.Input.Opens.Should().Be(1);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        typeof(RogNativeMenuSource.Actor).GetField("_neutral", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(rig.Actor).Should().Be((ushort)0, "no cached capability invents neutral input");
        rig.Input.Send(Notice(1, 0, 3, 2)); rig.Input.Send(Notice(2, 2, 1, 2));
        await rig.Input.WaitReads(2); await rig.Actor.StepAsync(Latest, default);
        rig.Caps.Should().Be(3); rig.Events.OfType<NativeMenuPress>().Should().ContainSingle();
        rig.PressObservedAtCap.Should().Be(3); rig.Io.ImageBegins.Should().Be(0);
    }
}
