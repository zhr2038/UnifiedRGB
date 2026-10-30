using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fakes only: no HID/PnP/Host/process call. Enumerated collections copy Root's actual metadata-only snapshot
/// (evidence/ACTUAL-ROOT-METADATA.json, sourceSha256 95bb92f358378413c5491b8e33cf87278650b40ed50c8b1bd6e24d35967188ac) field for
/// field: VID, PID, MI, COL, usage page, usage, lengths, caps error, container group and the two descriptor-only report-id sets.
/// Only each path's PnP instance segment is a neutral placeholder; container identity is the fake platform property per group.
/// </summary>
[Collection("Host")]
public sealed partial class NativeInputDiscoveryTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MetadataAge = TimeSpan.FromMinutes(3);
    private const ulong Cookie = 0x2233445566778899;
    private const string HidClass = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static HidCollectionInfo Info(string pid, string mi, int index, int iface, ushort usagePage, ushort usage,
        int input, int output, int feature, string? error = null, string suffix = "") =>
        new($@"\\?\hid#vid_0b05&pid_{pid}&{mi}#root-sanitized&0&{index:D4}#{HidClass}{suffix}", 0x0B05,
            pid == "1c24" ? (ushort)0x1C24 : (ushort)0x1ACE, iface, usagePage, usage, input, output, feature, error);

    private static readonly HidCollectionInfo WiredTarget = Info("1c24", "mi_02&col03", 2, 2, 0xFFC0, 1, 21, 0, 0);
    private static readonly HidCollectionInfo WiredMi03Keyboard = Info("1c24", "mi_03", 0, 3, 0x0001, 6, 16, 0, 0, suffix: @"\kbd");
    private static readonly HidCollectionInfo WiredControl = Info("1c24", "mi_01", 0, 1, 0xFF00, 1, 65, 65, 0);
    private static readonly HidCollectionInfo ReceiverTarget = Info("1ace", "mi_03&col04", 3, 3, 0xFFC0, 1, 21, 0, 0);
    private static readonly HidCollectionInfo[] ReceiverControls =
    [
        Info("1ace", "mi_02&col01", 0, 2, 0xFF02, 1, 64, 64, 0),
        Info("1ace", "mi_02&col02", 1, 2, 0xFF00, 1, 64, 64, 0),
        Info("1ace", "mi_02&col03", 2, 2, 0xFF01, 1, 64, 64, 0),
    ];

    /// <summary>Snapshot order. Group 0 = keyboard container, 1 = receiver container, null = resolver failure (MI00).</summary>
    private static Row[] ActualRows() =>
    [
        new(Info("1c24", "mi_02&col01", 0, 2, 0x000C, 1, 4, 0, 0), 0, null),
        new(Info("1c24", "mi_02&col02", 1, 2, 0x0001, 0x80, 2, 0, 0), 0, null),
        new(WiredTarget, 0, [3]),
        new(Info("1c24", "mi_02&col04", 3, 2, 0, 0, 0, 0, 0, "CreateFile error 5"), 0, null),
        new(Info("1c24", "mi_00", 0, 0, 0x0001, 6, 9, 2, 0, suffix: @"\kbd"), null, null),
        new(WiredMi03Keyboard, 0, null),
        new(WiredControl, 0, null),
        new(Info("1ace", "mi_03&col01", 0, 3, 0x000C, 1, 4, 0, 0), 1, null),
        new(Info("1ace", "mi_03&col02", 1, 3, 0x0001, 0x80, 2, 0, 0), 1, null),
        new(Info("1ace", "mi_03&col03", 2, 3, 0xFFC2, 1, 21, 0, 0), 1, null),
        new(ReceiverTarget, 1, [4]),
        new(Info("1ace", "mi_00&col02", 1, 0, 0x0001, 6, 20, 0, 0, suffix: @"\kbd"), null, null),
        new(Info("1ace", "mi_03&col05", 4, 3, 0xFFC1, 1, 21, 0, 0), 1, null),
        new(Info("1ace", "mi_01", 0, 1, 0, 0, 0, 0, 0, "CreateFile error 5"), 1, null),
        new(ReceiverControls[0], 1, null),
        new(ReceiverControls[1], 1, null),
        new(ReceiverControls[2], 1, null),
        new(Info("1ace", "mi_00&col01", 0, 0, 0x0001, 6, 9, 2, 0, suffix: @"\kbd"), null, null),
    ];

    [Fact]
    public void Actual_wired_metadata_selects_MI02_COL03_and_derives_interface_2_where_the_fixed_MI03_rule_found_nothing()
    {
        var time = TimeProvider.System; using var world = new World(time);
        var control = RogKeyboardInputContract.Fresh(world.Resolve(WiredControl), time, MetadataAge, "Test control");
        world.ResolvedPaths.Clear();
        var enumerated = world.Enumerate(0x0B05, 0x1C24);
        enumerated.Should().HaveCount(7);
        var selected = NativeInputDiscovery.Select(NativeInputDiscovery.Wired, enumerated, [WiredControl], control, world, time, MetadataAge);
        selected.Collection.Should().Be(WiredTarget);
        selected.Rule.Interface.Should().Be(2);
        selected.Rule.ToString().Should().Be("0B05:1C24 MI_02 UP:FFC0 U:0001 in 21 out 0 feature 0 input RIDs [3]");
        selected.Rule.Reject(new HidInputDescriptor(0x0B05, 0x1C24, 0xFFC0, 1, 21, 0, 0, [3])).Should().BeNull();
        world.ResolvedPaths.Should().Equal(new[] { WiredTarget.Path }, "ordinary, MI00, caps-unavailable and control collections are never resolved");
        world.Opens.Should().Be(0);

        // The pre-fix unconditional MI03 rule: zero candidates, i.e. the observed PrepareInput blocker.
        var historical = new HidVendorInputRule(0x0B05, 0x1C24, 3, 0xFFC0, 1, 21, [3]);
        enumerated.Where(c => historical.Reject(c) is null).Should().BeEmpty();
        historical.Reject(WiredTarget).Should().Be("interface MI_02 is not MI_03");
        historical.Reject(WiredMi03Keyboard).Should().StartWith("not a vendor-defined usage page", "the actual wired MI03 is an ordinary keyboard");
    }

    [Fact]
    public void Actual_receiver_metadata_selects_FFC0_COL04_on_MI03_and_never_substitutes_FFC1_or_FFC2()
    {
        var time = TimeProvider.System; using var world = new World(time);
        var control = RogKeyboardInputContract.Fresh(world.Resolve(ReceiverControls[0]), time, MetadataAge, "Test control");
        var selected = NativeInputDiscovery.Select(NativeInputDiscovery.Receiver, world.Enumerate(0x0B05, 0x1ACE), ReceiverControls,
            control, world, time, MetadataAge);
        selected.Collection.Should().Be(ReceiverTarget);
        selected.Rule.Interface.Should().Be(3);
        selected.Rule.ToString().Should().Be(RogKeyboardInputContract.EventRule.ToString());

        world.Rows.RemoveAll(r => r.Collection == ReceiverTarget);
        var missing = () => NativeInputDiscovery.Select(NativeInputDiscovery.Receiver, world.Enumerate(0x0B05, 0x1ACE), ReceiverControls,
            control, world, time, MetadataAge);
        missing.Should().Throw<InvalidDataException>().Which.Message.Should().Contain("0 of 0 eligible")
            .And.Contain("0B05:1ACE MI_03 UP:FFC1 U:0001 in 21 out 0 feature 0: usage FFC1/0001 is not FFC0/0001")
            .And.Contain("0B05:1ACE MI_03 UP:FFC2 U:0001 in 21 out 0 feature 0: usage FFC2/0001 is not FFC0/0001");
        world.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Actual_metadata_arms_once_through_the_one_existing_owner_after_one_read_only_open(byte transport)
    {
        await using var rig = new Rig(transport); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.World.OpenedPaths.Should().Equal((transport == 1 ? WiredTarget : ReceiverTarget).Path);
        rig.World.Enumerations.Should().Be(1);
        rig.Io.Arms.Should().Be(1);
        rig.Io.LiveInputAtArm.Should().Be(1, "PrepareInput binds the actual collection before ARM");
        rig.Io.ReadingAtArm.Should().BeFalse();
        rig.Io.MaximumControl.Should().Be(1);
        rig.Events.OfType<NativeMenuContextBegun>().Should().ContainSingle();
        rig.Source.IsPresent.Should().BeTrue();
        ((Action)(() => rig.Source.Attach(rig.Io))).Should().Throw<InvalidOperationException>("no second keyboard control owner can be borrowed");
        typeof(RogNativeMenuSource).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).Should()
            .NotContain(t => typeof(IHidReportDevices).IsAssignableFrom(t) || t == typeof(IRogWiredOledFactory) || t == typeof(IRogWirelessOledFactory),
                "the input side receives only read-only input devices and a container resolver; it cannot open a control sender");
        typeof(IHidInputReportDevices).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(new[] { "Enumerate", "OpenReadOnly" });
    }

    [Theory]
    [InlineData(1, "descriptor-rid-4")] [InlineData(2, "descriptor-rid-3")] [InlineData(1, "descriptor-rids-3-4")]
    [InlineData(1, "descriptor-length-64")] [InlineData(1, "descriptor-output")] [InlineData(1, "length-20")] [InlineData(2, "length-64")]
    [InlineData(1, "usage-2")] [InlineData(2, "usage-page-ffc2")] [InlineData(1, "output-21")] [InlineData(1, "feature-1")]
    [InlineData(1, "caps-error")] [InlineData(1, "path-interface")] [InlineData(1, "control-interface")] [InlineData(2, "control-interface")]
    public async Task Report_id_length_usage_and_input_only_mismatches_fail_before_ARM(byte transport, string mutation)
    {
        var target = transport == 1 ? WiredTarget : ReceiverTarget;
        await using var rig = new Rig(transport, world =>
        {
            var pid = transport == 1 ? (ushort)0x1C24 : (ushort)0x1ACE;
            switch (mutation)
            {
                case "descriptor-rid-4": world.DescriptorOverride = new(0x0B05, pid, 0xFFC0, 1, 21, 0, 0, [4]); break;
                case "descriptor-rid-3": world.DescriptorOverride = new(0x0B05, pid, 0xFFC0, 1, 21, 0, 0, [3]); break;
                case "descriptor-rids-3-4": world.DescriptorOverride = new(0x0B05, pid, 0xFFC0, 1, 21, 0, 0, [3, 4]); break;
                case "descriptor-length-64": world.DescriptorOverride = new(0x0B05, pid, 0xFFC0, 1, 64, 0, 0, [3]); break;
                case "descriptor-output": world.DescriptorOverride = new(0x0B05, pid, 0xFFC0, 1, 21, 21, 0, [3]); break;
                case "length-20": world.Replace(target, target with { InputReportLength = 20 }); break;
                case "length-64": world.Replace(target, target with { InputReportLength = 64 }); break;
                case "usage-2": world.Replace(target, target with { Usage = 2 }); break;
                case "usage-page-ffc2": world.Replace(target, target with { UsagePage = 0xFFC2 }); break;
                case "output-21": world.Replace(target, target with { OutputReportLength = 21 }); break;
                case "feature-1": world.Replace(target, target with { FeatureReportLength = 1 }); break;
                case "caps-error": world.Replace(target, target with { Error = "CreateFile error 5" }); break;
                case "path-interface": world.Replace(target, target with { Interface = 3 }); break;
                case "control-interface":
                    world.Replace(target, transport == 1
                        ? target with { Interface = 1, Path = target.Path.Replace("mi_02", "mi_01", StringComparison.Ordinal) }
                        : target with { Interface = 2, Path = target.Path.Replace("mi_03", "mi_02", StringComparison.Ordinal) });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        rig.Source.RequestEnabled(true);
        var thrown = await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        var opened = mutation.StartsWith("descriptor-", StringComparison.Ordinal);
        rig.World.Opens.Should().Be(opened ? 1 : 0);
        if (!opened) thrown.Which.Message.Should().StartWith("Exactly one same-container native vendor21 collection is required: 0 of");
        if (mutation == "control-interface") thrown.Which.Message.Should().Contain("shares the interface of the native control owner");
        rig.Io.Arms.Should().Be(0); rig.Io.Exchanges.Should().Be(1, "only the capability query preceded the refused input binding");
        rig.Events.Should().BeEmpty(); rig.World.LiveHandles.Should().Be(0); rig.Source.IsPresent.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, "other-container")] [InlineData(2, "other-container")] [InlineData(1, "ambiguous")] [InlineData(2, "ambiguous")]
    [InlineData(1, "unresolved")] [InlineData(1, "stale")] [InlineData(2, "stale")] [InlineData(1, "future")] [InlineData(1, "control-stale")]
    [InlineData(2, "control-stale")] [InlineData(1, "after-open")] [InlineData(2, "after-open")] [InlineData(1, "control-after-open")]
    public async Task Physical_association_mismatch_ambiguity_staleness_and_post_open_change_never_ARM(byte transport, string scenario)
    {
        var target = transport == 1 ? WiredTarget : ReceiverTarget;
        var control = transport == 1 ? WiredControl : ReceiverControls[1];
        const int OtherGroup = 7;
        await using var rig = new Rig(transport, world =>
        {
            switch (scenario)
            {
                case "other-container": world.GroupOverride[target.Path] = OtherGroup; break;
                case "ambiguous": world.AddClone(target, target.Path.Replace(transport == 1 ? "col03" : "col04", "col09", StringComparison.Ordinal)); break;
                case "unresolved": world.Unavailable.Add(target.Path); break;
                case "stale": world.Stale.Add(target.Path); break;
                case "future": world.Future.Add(target.Path); break;
                case "control-stale": world.Stale.Add(control.Path); break;
                case "after-open": world.OnOpen = () => world.GroupOverride[target.Path] = OtherGroup; break;
                case "control-after-open": world.OnOpen = () => world.GroupOverride[control.Path] = OtherGroup; break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        });
        rig.Source.RequestEnabled(true);
        var thrown = await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<Exception>();
        var (type, text) = scenario switch
        {
            "other-container" => (typeof(InvalidDataException), "eligible but on another physical container"),
            "ambiguous" => (typeof(InvalidDataException), "Ambiguous: "),
            "unresolved" => (typeof(DeviceBlockedException), "Native event physical container is unavailable"),
            "stale" => (typeof(DeviceBlockedException), "Native event physical container metadata is stale"),
            "future" => (typeof(DeviceBlockedException), "Native event physical container metadata is dated in the future"),
            "control-stale" => (typeof(DeviceBlockedException), "Native control physical container metadata is stale"),
            "after-open" => (typeof(InvalidDataException), "Opened native event physical container changed"),
            _ => (typeof(InvalidDataException), "Control physical container changed while binding native input"),
        };
        thrown.Which.Should().BeOfType(type); thrown.Which.Message.Should().Contain(text);
        if (scenario == "ambiguous") thrown.Which.Message.Should().Contain("2 of 2 eligible");
        rig.World.Opens.Should().Be(scenario.EndsWith("after-open", StringComparison.Ordinal) ? 1 : 0);
        rig.Io.Arms.Should().Be(0); rig.Events.Should().BeEmpty(); rig.World.LiveHandles.Should().Be(0); rig.Source.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task A_second_identical_keyboard_on_another_physical_container_is_neither_ambiguous_nor_opened()
    {
        await using var rig = new Rig(1, world =>
        {
            foreach (var row in world.Rows.Where(r => r.Collection.Pid == 0x1C24).ToArray())
                world.Rows.Add(row with
                {
                    Collection = row.Collection with { Path = row.Collection.Path.Replace("root-sanitized", "other-keyboard", StringComparison.Ordinal) },
                    Group = row.Group is null ? null : 5,
                });
        });
        rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.World.OpenedPaths.Should().Equal(WiredTarget.Path);
        rig.World.ResolvedPaths.Should().Contain(p => p.Contains("other-keyboard", StringComparison.Ordinal) && p.Contains("col03", StringComparison.Ordinal),
            "every eligible candidate is associated by its platform container, never skipped by path text");
        rig.Io.Arms.Should().Be(1);
    }

    [Fact]
    public async Task Armed_actual_input_still_requires_the_same_fresh_physical_container_on_every_tick()
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.World.GroupOverride[WiredTarget.Path] = 6; var exchanges = rig.Io.Exchanges;
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        rig.Actor.RecoveryBlocked.Should().BeTrue(); rig.Io.Exchanges.Should().Be(exchanges, "no cross-scope control I/O follows");
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Disconnected);
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Center_held_duplicate_release_and_native_marker_rules_are_unchanged_on_the_actual_descriptor(byte transport)
    {
        await using var rig = new Rig(transport); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var ordinary72 = new byte[21]; ordinary72[0] = Rid(transport); ordinary72[1] = 0x72; ordinary72[2] = 2;
        var mode9 = new byte[21]; mode9[0] = Rid(transport); mode9[1] = 0x91; mode9[4] = RogNativeMenuCodec.NativeMode;
        rig.World.Send(ordinary72); rig.World.Send(mode9);
        byte[] masks = [0, 2, 2, 2, 0, 2, 0, 1, 0, 4];
        for (ushort n = 0; n < masks.Length; n++) rig.World.Send(Notice(transport, (ushort)(n + 1), masks[n], n == 0 ? (byte)3 : (byte)1));
        await rig.World.WaitReads(masks.Length + 2);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var presses = rig.Events.OfType<NativeMenuPress>().Select(e => e.Intent).ToArray();
        presses.Select(p => p.Action).Should().Equal(DesiredAction.Toggle, DesiredAction.Toggle);
        presses.Select(p => p.Sequence).Should().Equal(2, 6);
        presses.Select(p => p.NeutralArmSequence).Should().Equal(1, 5);
        rig.Io.Arms.Should().Be(1); rig.Actor.Armed.Should().BeTrue(); rig.World.Opens.Should().Be(1);
    }

    [Theory]
    [InlineData("profile")] [InlineData("cookie")] [InlineData("sequence")] [InlineData("epoch")] [InlineData("replay")]
    [InlineData("not-d5")] [InlineData("mode")] [InlineData("report-id")]
    public async Task Scope_and_marker_drift_on_the_actual_descriptor_ends_the_context_without_a_press(string mutation)
    {
        await using var rig = new Rig(1); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        var notice = Notice(1, 1, 0, 3);
        switch (mutation)
        {
            case "profile": notice[5] = 1; break;
            case "cookie": notice[11] ^= 1; break;
            case "sequence": BinaryPrimitives.WriteUInt16LittleEndian(notice.AsSpan(19), 2); break;
            case "epoch": BinaryPrimitives.WriteUInt32LittleEndian(notice.AsSpan(7), 3); break;
            case "replay": rig.World.Send((byte[])notice.Clone()); break;
            case "not-d5": notice[3] = 0xD4; break;
            case "mode": notice[4] = 8; break;
            case "report-id": notice[0] = 4; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        rig.World.Send(notice);
        var expected = mutation == "replay" ? 2 : 1;
        await Until(() => rig.Events.OfType<NativeMenuContextEnded>().Any() || rig.Actor.ReceivedNotifications >= expected);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle();
        rig.Actor.Armed.Should().BeFalse(); rig.Io.Arms.Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task Default_off_or_disabled_sources_never_enumerate_resolve_or_open_actual_collections(bool installed, bool requested)
    {
        await using var rig = new Rig(1, policy: installed ? null : new KeyboardNativeMenuOptions());
        rig.Source.RequestEnabled(requested);
        for (var i = 0; i < 3; i++) (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Exchanges.Should().Be(0); rig.World.Enumerations.Should().Be(0); rig.World.ResolvedPaths.Should().BeEmpty();
        rig.World.Opens.Should().Be(0); rig.Source.IsPresent.Should().BeFalse(); rig.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Refusal_diagnosis_names_descriptor_reasons_without_path_instance_or_container_text()
    {
        await using var rig = new Rig(1, world => world.Rows.RemoveAll(r => r.Collection == WiredTarget));
        rig.Source.RequestEnabled(true);
        var thrown = await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        var message = thrown.Which.Message;
        message.Should().StartWith("Exactly one same-container native vendor21 collection is required: 0 of 0 eligible "
            + "0B05:1C24 UP:FFC0 U:0001 in 21 out 0 feature 0 input RIDs [3] collections share the control container (6 enumerated).");
        message.Should().Contain("0B05:1C24 MI_03 UP:0001 U:0006 in 16 out 0 feature 0: not a vendor-defined usage page")
            .And.Contain("0B05:1C24 MI_02 caps unavailable (CreateFile error 5): caps unavailable")
            .And.Contain("0B05:1C24 MI_00 UP:0001 U:0006 in 9 out 2 feature 0: MI_00 or an unnumbered interface is never opened for input")
            .And.Contain("0B05:1C24 MI_01 UP:FF00 U:0001 in 65 out 65 feature 0: usage FF00/0001 is not FFC0/0001");
        message.Should().NotContain("root-sanitized").And.NotContain("hid#").And.NotContain(HidClass).And.NotContain(@"\");
        rig.World.Opens.Should().Be(0); rig.World.ResolvedPaths.Should().Equal(WiredControl.Path);
    }

    private static byte Rid(byte transport) => transport == 1 ? (byte)3 : (byte)4;

    private static byte[] Pixels() => Enumerable.Range(0, RogWiredOledProtocol.PixelBytes).Select(i => (byte)(i * 31 + i / 97)).ToArray();

    private static byte[] Notice(byte transport, ushort sequence, byte mask, byte flags)
    {
        var p = new byte[21]; p[0] = Rid(transport); p[1] = 0x72; p[2] = mask; p[3] = 0xD5; p[4] = RogNativeMenuCodec.NativeMode; p[6] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), 2); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), Cookie);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(19), sequence);
        return p;
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Fake pipeline did not reach the expected boundary.");
            await Task.Delay(5);
        }
    }

    private static KeyboardNativeMenuOptions Policy(byte transport) => new()
    {
        Enabled = true, SourceControlNotifications = true, NativeBuild = RogNativeMenuCodec.InstalledBuild,
        ConnectionKind = transport == 1 ? "wired" : "wireless",
        FirmwareHeader = transport == 1 ? "001200000025000100" : "0800070025000100",
        DeviceKey = transport == 1 ? RogWiredOledProtocol.DeviceKey(WiredControl) : new string('A', 64),
        CandidateSha256 = "0d7cd76d3056671bf6580196ca58041ab2417b824c980de2c9a3034d63341958",
        ContractSha256 = "b6ff1d41add209a95fb3dfebbcee7993aafbb1199b9ffb7a8551788028d4a585",
    };

    private sealed record Row(HidCollectionInfo Collection, int? Group, byte[]? DescriptorIds);

    /// <summary>System timestamps plus a logical offset (pairing/metadata age beyond 180 s); timers stay ordinary system timers.</summary>
    private sealed class OffsetClock : TimeProvider
    {
        private long _offset;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + Interlocked.Read(ref _offset);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _offset, (long)(by.TotalSeconds * TimestampFrequency));
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly IDisposable _registration;

        public Rig(byte transport, Action<World>? arrange = null, KeyboardNativeMenuOptions? policy = null, Action<Connection>? owner = null)
        {
            World = new(Time);
            arrange?.Invoke(World);
            Io = new(transport, transport == 1 ? new[] { WiredControl } : ReceiverControls, Time) { Input = World };
            owner?.Invoke(Io);
            var next = Cookie;
            Source = new(policy ?? Policy(transport), World, World, Time, () => next++);
            _registration = Source.Connect(Events.Enqueue);
            Actor = Source.Attach(Io);
            World.Admitted = () => Actor.ReceivedNotifications;
        }

        public OffsetClock Time { get; } = new();
        public World World { get; }
        public Connection Io { get; }
        public RogNativeMenuSource Source { get; }
        public RogNativeMenuSource.Actor Actor { get; }
        public ConcurrentQueue<NativeMenuSourceEvent> Events { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Actor.DisposeAsync();
            _registration.Dispose();
            World.Dispose();
        }
    }

    private sealed class World : IHidInputReportDevices, IHidContainerResolver, IDisposable
    {
        private static readonly MethodInfo FromPlatform =
            typeof(HidPhysicalContainer).GetMethod("FromPlatformContainerId", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<int, Guid> _containers = new();
        private Handle? _handle;
        private int _live, _pending;

        public World(TimeProvider time) { _time = time; Rows.AddRange(ActualRows()); }

        public List<Row> Rows { get; } = [];
        public Dictionary<string, int> GroupOverride { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Stale { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Future { get; } = new(StringComparer.Ordinal);
        public HidInputDescriptor? DescriptorOverride { get; set; }
        public Action? OnOpen { get; set; }
        public Func<long>? Admitted { get; set; }
        public int Enumerations { get; private set; }
        public int Opens { get; private set; }
        public List<string> OpenedPaths { get; } = [];
        public List<string> ResolvedPaths { get; } = [];
        public int LiveHandles => Volatile.Read(ref _live);
        public int Pending => Volatile.Read(ref _pending);

        public void Replace(HidCollectionInfo old, HidCollectionInfo replacement)
        {
            var i = Rows.FindIndex(r => r.Collection == old);
            Rows[i] = Rows[i] with { Collection = replacement };
        }

        public void AddClone(HidCollectionInfo original, string path)
        {
            var row = Rows.Single(r => r.Collection == original);
            Rows.Add(row with { Collection = original with { Path = path } });
        }

        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid)
        {
            Enumerations++;
            return Rows.Where(r => r.Collection.Vid == vid && r.Collection.Pid == pid).Select(r => r.Collection).ToArray();
        }

        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection)
        {
            Opens++; OpenedPaths.Add(collection.Path);
            var row = Rows.Single(r => r.Collection == collection);
            if (row.DescriptorIds is null) throw new InvalidOperationException("Root never opened this collection; the fixture has no descriptor for it.");
            OnOpen?.Invoke();
            var c = row.Collection;
            var descriptor = DescriptorOverride ?? new HidInputDescriptor(c.Vid, c.Pid, c.UsagePage, c.Usage, c.InputReportLength,
                c.OutputReportLength, c.FeatureReportLength, row.DescriptorIds);
            Interlocked.Increment(ref _live);
            return _handle = new Handle(this, descriptor);
        }

        public HidContainerResolution Resolve(HidCollectionInfo collection)
        {
            ResolvedPaths.Add(collection.Path);
            if (Unavailable.Contains(collection.Path)) return HidContainerResolution.Unavailable("container property unavailable in this fake");
            int? group = GroupOverride.TryGetValue(collection.Path, out var g) ? g : Rows.FirstOrDefault(r => r.Collection.Path == collection.Path)?.Group;
            if (group is null) return HidContainerResolution.Unavailable("not an enumerated HID interface path");
            var now = _time.GetTimestamp();
            var at = Stale.Contains(collection.Path) ? now - (long)(TimeSpan.FromMinutes(4).TotalSeconds * _time.TimestampFrequency)
                : Future.Contains(collection.Path) ? now + (long)(10 * _time.TimestampFrequency) : now;
            var id = _containers.GetOrAdd(group.Value, _ => Guid.NewGuid());
            return HidContainerResolution.Resolved((HidPhysicalContainer)FromPlatform.Invoke(null, [id, at])!);
        }

        public void Send(byte[] report) => (_handle ?? throw new InvalidOperationException("Input not open.")).Queue.Writer.TryWrite(report).Should().BeTrue();

        public Task WaitReads(int count) => Until(() => (Admitted?.Invoke() ?? 0) >= count);

        public void Dispose() => _handle?.Dispose();

        private sealed class Handle(World owner, HidInputDescriptor descriptor) : IHidInputReportHandle
        {
            private int _disposed;
            public Channel<byte[]> Queue { get; } = Channel.CreateUnbounded<byte[]>();
            public HidInputDescriptor Describe() => descriptor;

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._pending);
                try
                {
                    var p = await Queue.Reader.ReadAsync(ct);
                    p.CopyTo(buffer);
                    return p.Length;
                }
                finally { Interlocked.Decrement(ref owner._pending); }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._live);
            }
        }
    }

    /// <summary>The one existing keyboard control owner (fake); every control exchange is counted here and nowhere else.</summary>
    private sealed class Connection(byte transport, IReadOnlyList<HidCollectionInfo> controls, TimeProvider time) : IRogOledConnection
    {
        private readonly TimeProvider _time = time;
        private ulong _cookie;
        private int _pending;
        private RogPairingEvidence? _evidence;
        public World? Input { get; init; }
        public string DeviceKey => transport == 1 ? RogWiredOledProtocol.DeviceKey(controls[0]) : new string('A', 64);
        /// <summary>Diagnostic only for the actor route; tests may replace it before Attach.</summary>
        public string? HeaderOverride { get; set; }
        public string FirmwareHeader => HeaderOverride ?? (transport == 1 ? "001200000025000100" : "0800070025000100");
        public string ConnectionKind => transport == 1 ? "wired" : "wireless";
        public int PixelPayloadCapacity => transport == 1 ? 60 : 59;
        /// <summary>Captured connection-generation provenance; tests may replace it before Attach only.</summary>
        public long PairedAt { get; set; } = time.GetTimestamp();
        public RogPairedDevice? KeyboardOverride { get; set; }
        public bool NoKeyboard { get; set; }
        public RogNativeControlBinding? NativeControlBinding => new(controls,
            NoKeyboard ? null : KeyboardOverride ?? (transport == 2 ? new RogPairedDevice(0x1C25, 2, 4) : null), PairedAt);
        public List<byte[]> ImageChunks { get; } = [];
        public byte Mode { get; set; } = RogNativeMenuCodec.NativeMode;
        public byte Profile { get; set; }
        public byte? CapTransportOverride { get; set; }
        public bool FailNextCapability { get; set; }

        // In-memory stand-in for A's frozen owner evidence: initial provenance comes from the binding and never changes;
        // it advances only when the real codec validates this owner's own CAP/ARM reply (never image ACKs or renewals);
        // invalidation is terminal. EvidenceView lets a test expose a manipulated snapshot instead.
        public bool ProvidesEvidence { get; set; }
        public bool AdvanceEvidence { get; set; } = true;
        public Func<RogPairingEvidence?, RogPairingEvidence?>? EvidenceView { get; set; }
        public RogPairingEvidence? PairingEvidence => EvidenceView is { } view ? view(Genuine()) : Genuine();

        public void InvalidateEvidence(string reason)
        {
            if (Genuine() is { Invalidated: null } evidence) _evidence = evidence with { Invalidated = reason };
        }

        private RogPairingEvidence? Genuine() =>
            !ProvidesEvidence || NativeControlBinding is not { Keyboard: { } keyboard } binding ? null
                : _evidence ??= new(keyboard, binding.PairingTimestamp, binding.PairingTimestamp, 0, null);

        private void Observe(byte[] reply, byte command)
        {
            if (transport != 2 || !AdvanceEvidence || Genuine() is not { Invalidated: null } evidence) return;
            try { RogNativeMenuCodec.ParseCapability(reply, command, 2); }
            catch (InvalidDataException) { _evidence = evidence with { Invalidated = "capability grammar" }; return; }
            _evidence = evidence with
            {
                LastObservedTimestamp = Math.Max(evidence.LastObservedTimestamp, _time.GetTimestamp()), Observations = evidence.Observations + 1,
            };
        }
        public uint Epoch { get; private set; } = 1;
        public int Exchanges { get; private set; }
        public int Arms { get; private set; }
        public int Disarms { get; private set; }
        public int MaximumControl { get; private set; }
        public int LiveInputAtArm { get; private set; } = -1;
        public bool ReadingAtArm { get; private set; }
        public bool SameDevice() => true;
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) { }

        public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MaximumControl = Math.Max(MaximumControl, Interlocked.Increment(ref _pending));
            try
            {
                Exchanges++;
                var reply = new byte[65]; request.AsSpan(0, 5).CopyTo(reply);
                switch (request[1], request[2])
                {
                    case (0x12, 0x7D):
                        if (FailNextCapability)
                        {
                            FailNextCapability = false; InvalidateEvidence("unknown outcome");
                            throw new IOException("Injected unknown CAP outcome.");
                        }
                        var observed = Capability(0x12); Observe(observed, 0x12);
                        return Task.FromResult(observed);
                    case (0x71, 0x7D):
                        BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(19)).Should().Be(Epoch);
                        request[24].Should().Be(transport);
                        Epoch++; _cookie = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(11));
                        if (request[10] == 1)
                        {
                            Arms++; LiveInputAtArm = Input?.LiveHandles ?? 0; ReadingAtArm |= Input?.Pending > 0;
                        }
                        else Disarms++;
                        var armReply = Capability(0x71); Observe(armReply, 0x71);
                        return Task.FromResult(armReply);
                    case (0x67, 0 or 1 or 2):
                        if (request[2] == 1) ImageChunks.Add((byte[])request.Clone());
                        return Task.FromResult(reply);
                    default: throw new InvalidOperationException("Unexpected fake command.");
                }
            }
            finally { Interlocked.Decrement(ref _pending); }
        }

        private byte[] Capability(byte command)
        {
            var p = new byte[65]; p[1] = command; p[2] = 0x7D; "GYM1"u8.CopyTo(p.AsSpan(5)); p[9] = 2;
            p[10] = _cookie == 0 ? (byte)0 : (byte)1; p[11] = Mode; p[12] = Profile; p[13] = CapTransportOverride ?? transport;
            p[14] = _cookie == 0 ? (byte)48 : (byte)49;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), Epoch); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), _cookie);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), RogNativeMenuCodec.BuildTag);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208); p[35] = 64; p[36] = (byte)PixelPayloadCapacity; p[37] = 3;
            return p;
        }

        public Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            renewed();
            changed().Should().BeTrue("the actor requests one renewal and joins before its next exchange");
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
