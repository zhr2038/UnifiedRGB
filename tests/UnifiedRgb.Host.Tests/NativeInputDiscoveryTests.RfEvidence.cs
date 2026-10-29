using System.Reflection;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Frozen RF evidence surface (IRogOledConnection.PairingEvidence / RogPairingEvidence) consumed by the actor, with logical
/// time beyond 180 s. The owner fake's evidence is a genuine in-memory observation: it starts from the captured binding,
/// advances only when the real codec validates this owner's own CAP/ARM reply, and invalidation is terminal. Production RF
/// evidence itself is A's implementation and is not exercised here.
/// </summary>
public sealed partial class NativeInputDiscoveryTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RF_genuine_same_owner_observations_keep_the_context_beyond_180_s_and_still_admit_a_center_press()
    {
        await using var rig = new Rig(2, owner: io => io.ProvidesEvidence = true); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var provenance = rig.Io.PairedAt;
        rig.Io.PairingEvidence!.Observations.Should().Be(2, "only the validated CAP and ARM replies count; 113 image ACKs and the renewal do not");
        for (var i = 0; i < 8; i++)
        {
            rig.Time.Advance(Tick);
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        }
        rig.World.Send(Notice(2, 1, 0, 3)); rig.World.Send(Notice(2, 2, 2, 1));
        await rig.World.WaitReads(2);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Action.Should().Be(DesiredAction.Toggle);
        rig.Events.OfType<NativeMenuContextEnded>().Should().BeEmpty();
        var evidence = rig.Io.PairingEvidence!;
        evidence.PairingTimestamp.Should().Be(provenance, "the initial pairing observation is never reset or relabelled");
        rig.Time.GetElapsedTime(evidence.PairingTimestamp).Should().BeGreaterThan(TimeSpan.FromMinutes(8));
        evidence.Observations.Should().Be(11, "one CAP per tick after the initial CAP and ARM; no extra query");
        rig.Io.Arms.Should().Be(1); rig.Io.Disarms.Should().Be(0); rig.World.Opens.Should().Be(1); rig.Io.MaximumControl.Should().Be(1);
    }

    [Fact]
    public async Task RF_this_tick_CAP_is_the_current_observation_judged_before_admission_even_after_a_long_gap()
    {
        await using var rig = new Rig(2, owner: io => io.ProvidesEvidence = true); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var exchanges = rig.Io.Exchanges;
        rig.Time.Advance(TimeSpan.FromSeconds(240));
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Io.Exchanges.Should().Be(exchanges + 1, "the existing per-tick CAP is the observation; no extra query, timer or sender");
        rig.Io.PairingEvidence!.Observations.Should().Be(3);
        rig.Events.OfType<NativeMenuContextEnded>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("absent")] [InlineData("never-advanced")]
    public async Task RF_absent_or_never_advanced_evidence_admits_no_queued_press_after_180_s(string kind)
    {
        await using var rig = new Rig(2, owner: io => { io.ProvidesEvidence = kind != "absent"; io.AdvanceEvidence = false; });
        rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromSeconds(100));
        rig.World.Send(Notice(2, 1, 0, 3)); rig.World.Send(Notice(2, 2, 2, 1));
        await rig.World.WaitReads(2);
        rig.Time.Advance(TimeSpan.FromSeconds(81));
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        var why = kind == "absent" ? "initial pairing observation is older than 3 minutes" : "last same-owner observation is older than 3 minutes";
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Reason.Contains("pairing proof expired", StringComparison.Ordinal)
            && e.Reason.Contains(why, StringComparison.Ordinal));
        if (kind != "absent") rig.Io.PairingEvidence!.Observations.Should().Be(0);
        rig.Io.Disarms.Should().Be(1); rig.Source.IsPresent.Should().BeFalse();
        var exchanges = rig.Io.Exchanges;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Exchanges.Should().Be(exchanges, "an expired generation performs no further native I/O");
    }

    [Theory]
    [InlineData("invalidated")] [InlineData("relabelled-route")] [InlineData("provenance")] [InlineData("replayed")]
    [InlineData("same-count-moved")] [InlineData("future")]
    public async Task RF_invalidated_relabelled_replayed_or_future_evidence_admits_nothing_even_inside_180_s(string mutation)
    {
        RogPairingEvidence? earlier = null, seen = null;
        await using var rig = new Rig(2, owner: io => io.ProvidesEvidence = true); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        earlier = rig.Io.PairingEvidence;
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        seen = rig.Io.PairingEvidence;
        rig.World.Send(Notice(2, 1, 0, 3)); rig.World.Send(Notice(2, 2, 2, 1));
        await rig.World.WaitReads(2);
        var tenSeconds = (long)(10 * rig.Time.TimestampFrequency);
        switch (mutation)
        {
            case "invalidated": rig.Io.InvalidateEvidence("keyboard asleep"); break;
            case "relabelled-route": rig.Io.EvidenceView = e => e! with { Keyboard = new(0x1C25, 3, 4) }; break;
            case "provenance": rig.Io.EvidenceView = e => e! with { PairingTimestamp = e!.PairingTimestamp - 1 }; break;
            case "replayed": rig.Io.EvidenceView = _ => earlier; break;
            case "same-count-moved": rig.Io.EvidenceView = _ => seen! with { LastObservedTimestamp = seen!.LastObservedTimestamp + 1 }; break;
            case "future": rig.Io.EvidenceView = e => e! with { LastObservedTimestamp = e!.LastObservedTimestamp + tenSeconds }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        var why = mutation switch
        {
            "invalidated" => "same-owner evidence invalidated (keyboard asleep)",
            "relabelled-route" or "provenance" => "same-owner evidence route or initial provenance differs",
            "future" => "last same-owner observation is dated in the future",
            _ => "same-owner evidence is inconsistent, replayed or went backwards",
        };
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Reason.Contains(why, StringComparison.Ordinal));
        rig.Io.Arms.Should().Be(1); rig.Io.Disarms.Should().Be(1); rig.Source.IsPresent.Should().BeFalse();
        var exchanges = rig.Io.Exchanges;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Exchanges.Should().Be(exchanges);
    }

    [Fact]
    public async Task RF_unknown_outcome_invalidates_evidence_and_latches_without_press_retry_or_cleanup_IO()
    {
        await using var rig = new Rig(2, owner: io => io.ProvidesEvidence = true); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromSeconds(200));
        rig.World.Send(Notice(2, 1, 0, 3)); rig.World.Send(Notice(2, 2, 2, 1));
        await rig.World.WaitReads(2);
        rig.Io.FailNextCapability = true;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<IOException>();
        rig.Io.PairingEvidence!.Invalidated.Should().Be("unknown outcome");
        rig.Actor.RecoveryBlocked.Should().BeTrue();
        var exchanges = rig.Io.Exchanges;
        await rig.Actor.StopAsync(default);
        rig.Io.Exchanges.Should().Be(exchanges, "an unknown outcome is never retried, disarmed or drained");
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Disconnected);
        rig.Io.Disarms.Should().Be(0);
    }

    [Theory]
    [InlineData("profile")] [InlineData("cookie")] [InlineData("container")]
    public async Task Fresh_evidence_never_overrides_profile_cookie_or_container_changes_beyond_180_s(string change)
    {
        await using var rig = new Rig(2, owner: io => io.ProvidesEvidence = true); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        for (var i = 0; i < 4; i++)
        {
            rig.Time.Advance(Tick);
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        }
        switch (change)
        {
            case "profile": rig.Io.Profile = 1; break;
            case "cookie":
                {
                    var notice = Notice(2, 1, 0, 3); notice[11] ^= 1;
                    rig.World.Send(notice); await rig.World.WaitReads(1);
                    break;
                }
            case "container": rig.World.GroupOverride[ReceiverTarget.Path] = 6; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        if (change == "container")
        {
            await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
            rig.Actor.RecoveryBlocked.Should().BeTrue();
        }
        else (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.PairingEvidence!.Invalidated.Should().BeNull("the evidence was current; the other scope check ended the context");
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle();
        rig.Actor.Armed.Should().BeFalse(); rig.Io.Arms.Should().Be(1);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task RF_ARM_after_180_s_requires_a_current_same_owner_observation(bool advancing)
    {
        await using var rig = new Rig(2, owner: io => { io.ProvidesEvidence = true; io.AdvanceEvidence = advancing; });
        rig.Source.RequestEnabled(true); rig.Io.Mode = 7;
        for (var i = 0; i < 4; i++)
        {
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
            rig.Time.Advance(Tick);
        }
        rig.Io.Mode = RogNativeMenuCodec.NativeMode;
        if (advancing)
        {
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
            rig.Io.Arms.Should().Be(1); rig.World.Opens.Should().Be(1);
            rig.Events.OfType<NativeMenuContextBegun>().Should().ContainSingle();
        }
        else
        {
            var thrown = await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
            thrown.Which.Message.Should().Contain("RF pairing proof unavailable").And.Contain("last same-owner observation is older than 3 minutes");
            rig.Io.Arms.Should().Be(0); rig.World.Enumerations.Should().Be(0); rig.World.Opens.Should().Be(0);
        }
    }

    [Fact]
    public async Task Wired_route_carries_no_pairing_evidence_and_continues_beyond_180_s()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        for (var i = 0; i < 4; i++)
        {
            rig.Time.Advance(Tick);
            (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        }
        rig.Io.PairingEvidence.Should().BeNull();
        rig.World.Send(Notice(1, 1, 0, 3)); rig.World.Send(Notice(1, 2, 2, 1));
        await rig.World.WaitReads(2);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Events.OfType<NativeMenuPress>().Should().ContainSingle().Which.Intent.Action.Should().Be(DesiredAction.Toggle);
        rig.Events.OfType<NativeMenuContextEnded>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("wired", 1)] [InlineData("wireless", 2)] [InlineData("wired-capacity-59", 0)] [InlineData("wireless-capacity-60", 0)]
    [InlineData("other-kind", 0)] [InlineData("wired-with-RF-controls", 0)] [InlineData("wireless-with-wired-control", 0)]
    [InlineData("wired-other-device-key", 0)] [InlineData("wired-with-paired-keyboard", 0)] [InlineData("no-controls", 0)]
    [InlineData("wired-no-binding", 1)] [InlineData("wireless-no-binding", 2)]
    public void Native_route_comes_from_the_owner_and_its_captured_control_descriptors(string scenario, byte expected)
    {
        var wiredKey = RogWiredOledProtocol.DeviceKey(WiredControl);
        var rfKey = new string('A', 64);
        RogNativeControlBinding wired = new([WiredControl], null, 0), rf = new(ReceiverControls, new(0x1C25, 2, 4), 0);
        var (kind, capacity, key, binding) = scenario switch
        {
            "wired" => ("wired", 60, wiredKey, (RogNativeControlBinding?)wired),
            "wireless" => ("wireless", 59, rfKey, rf),
            "wired-capacity-59" => ("wired", 59, wiredKey, wired),
            "wireless-capacity-60" => ("wireless", 60, rfKey, rf),
            "other-kind" => ("bluetooth", 59, rfKey, rf),
            "wired-with-RF-controls" => ("wired", 60, wiredKey, rf with { Keyboard = null }),
            "wireless-with-wired-control" => ("wireless", 59, rfKey, wired with { Keyboard = new(0x1C25, 2, 4) }),
            "wired-other-device-key" => ("wired", 60, new string('B', 64), wired),
            "wired-with-paired-keyboard" => ("wired", 60, wiredKey, wired with { Keyboard = new(0x1C25, 2, 4) }),
            "no-controls" => ("wireless", 59, rfKey, rf with { Controls = Array.Empty<UnifiedRgb.Transports.Hid.HidCollectionInfo>() }),
            "wired-no-binding" => ("wired", 60, wiredKey, null),
            "wireless-no-binding" => ("wireless", 59, rfKey, null),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        NativeInputDiscovery.Route(kind, capacity, key, binding).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "")] [InlineData(1, "001200000026000100")] [InlineData(1, "not-a-header")]
    [InlineData(2, "")] [InlineData(2, "0800070026000100")]
    public async Task Actor_route_does_not_depend_on_the_diagnostic_header(byte transport, string header)
    {
        await using var rig = new Rig(transport, owner: io => io.HeaderOverride = header);
        typeof(RogNativeMenuSource.Actor).GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig.Actor)
            .Should().Be(transport, "blank, stale or arbitrary header text is diagnostic only; the owner route and descriptors decide");
    }

    [Theory]
    [InlineData(1, 2)] [InlineData(2, 1)]
    public async Task Live_CAP_transport_byte_must_confirm_the_owner_route(byte transport, byte reported)
    {
        await using var rig = new Rig(transport, owner: io => { io.CapTransportOverride = reported; io.ProvidesEvidence = transport == 2; });
        rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Arms.Should().Be(0); rig.World.Enumerations.Should().Be(0); rig.World.Opens.Should().Be(0); rig.Source.IsPresent.Should().BeFalse();
        if (transport == 2) rig.Io.PairingEvidence!.Invalidated.Should().Be("capability grammar", "a wrong-transport CAP is never peer evidence");
        var exchanges = rig.Io.Exchanges;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Exchanges.Should().Be(exchanges, "no automatic probe loop");
    }
}
