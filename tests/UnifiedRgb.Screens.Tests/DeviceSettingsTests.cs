using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Local per-screen device settings (rotation + encoder policy): the store format, the one-time migration, the legacy
/// SetScreenScene rule, normalization of every commit, the wire orientation (user rotation once, TURZX landscape mapping once),
/// explicit saves that keep pause/content/gates, refused updates, policy plumbing into workers and the truthful preview.
/// Generic fake fixtures only; nothing reaches a real device.
/// </summary>
public sealed class DeviceSettingsTests
{
    private const string Legacy = ScreenEncodingPolicies.Legacy;
    private const string Lossless = ScreenEncodingPolicies.LosslessFirstV1;
    private static readonly string H2 = Devices.H2.Id;
    private static readonly string Tl = Devices.Tl1.Id;
    private static readonly string Turzx = Devices.Turzx.Id;

    // quadrants TL, TR, BR, BL of the 64x32 test image after a clockwise content rotation
    private static readonly Dictionary<int, string[]> Expected = new()
    {
        [0] = ["red", "green", "white", "blue"],
        [90] = ["blue", "red", "green", "white"],
        [180] = ["white", "blue", "red", "green"],
        [270] = ["green", "white", "blue", "red"],
    };

    [Fact]
    public void The_store_format_is_sorted_bom_free_bounded_and_strict()
    {
        var settings = new Dictionary<string, ScreenDeviceSetting>(StringComparer.Ordinal)
        {
            ["screen:turzx:b"] = new(270, Legacy),
            ["screen:h2:a"] = new(90, Lossless),
        };

        var bytes = ScreenDeviceSettingsStore.Serialize(settings);
        bytes[0].Should().Be((byte)'{', "UTF-8 without a BOM");
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.IndexOf("screen:h2:a", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("screen:turzx:b", StringComparison.Ordinal));
        ScreenDeviceSettingsStore.Parse(bytes).Should().BeEquivalentTo(settings);
        ScreenDeviceSettingsStore.Parse([0xEF, 0xBB, 0xBF, .. bytes]).Should().BeEquivalentTo(settings, "a hand edit's BOM is tolerated");
        ScreenDeviceSettingsStore.Serialize(ScreenDeviceSettingsStore.Parse(bytes)).Should().Equal(bytes);

        const string entry = """{"screenId":"screen:h2:a","rotation":90,"encodingQuality":"legacy"}""";
        foreach (var bad in new[]
                 {
                     "{",
                     "[]",
                     "{}",
                     """{"schemaVersion":2,"screens":[]}""",
                     """{"schemaVersion":1,"screens":[],"extra":1}""",
                     """{"schemaVersion":1,"screens":{}}""",
                     """{"schemaVersion":1,"screens":[1]}""",
                     """{"schemaVersion":1,"screens":[{"screenId":"screen:h2:a","encodingQuality":"legacy"}]}""",
                     """{"schemaVersion":1,"screens":[{"screenId":"screen:h2:a","rotation":45,"encodingQuality":"legacy"}]}""",
                     """{"schemaVersion":1,"screens":[{"screenId":"screen:h2:a","rotation":"90","encodingQuality":"legacy"}]}""",
                     """{"schemaVersion":1,"screens":[{"screenId":"screen:h2:a","rotation":90,"encodingQuality":"palette-first-v1"}]}""",
                     """{"schemaVersion":1,"screens":[{"screenId":"screen:h2:a","rotation":90,"encodingQuality":"legacy","path":"C:\\x"}]}""",
                     """{"schemaVersion":1,"screens":[{"screenId":"SCREEN:H2:A","rotation":90,"encodingQuality":"legacy"}]}""",
                     """{"schemaVersion":1,"screens":[""" + entry + "," + entry + "]}",
                     """{"schemaVersion":1,"screens":[""" + string.Join(",", Enumerable.Range(0, 65).Select(i => entry.Replace("screen:h2:a", $"screen:h2:x{i}", StringComparison.Ordinal))) + "]}",
                 })
        {
            var parse = () => ScreenDeviceSettingsStore.Parse(System.Text.Encoding.UTF8.GetBytes(bad));
            parse.Should().Throw<Exception>(bad).Which.Should().Match(e => e is InvalidDataException || e is JsonException, bad);
        }

        var tooMany = Enumerable.Range(0, 65).ToDictionary(i => $"screen:tl-lcd:x{i}", _ => ScreenDeviceSetting.Default);
        FluentActions.Invoking(() => ScreenDeviceSettingsStore.Serialize(tooMany)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task An_unconfigured_screen_defaults_to_zero_and_legacy_without_device_io()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);

        (await rig.Manager.GetScreenDeviceSettingsAsync("screen:tl-lcd:new0")).Should().Be(new ScreenDeviceSettingsDto("screen:tl-lcd:new0", 0, Legacy, false));
        (await rig.Manager.GetScreenDeviceSettingsAsync(H2)).Should().Be(new ScreenDeviceSettingsDto(H2, 0, Legacy, false));
        rig.Manager.EncodingOf(H2).Should().BeSameAs(ScreenEncodingSelection.Legacy);
        rig.Opener.Opens.Should().Be(0);
        rig.Enumerator.Calls.Should().Be(0, "reads never enumerate or open a device");
        await FluentActions.Awaiting(() => rig.Manager.GetScreenDeviceSettingsAsync("screen:a065:x")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_first_start_seeds_only_nonzero_persisted_rotations_once_without_device_io()
    {
        await using var rig = new Rig(null, Devices.H2, Devices.Tl1, Devices.Turzx);
        var path = rig.Manager.DeviceSettingsPath;
        using (var t = await rig.Manager.BeginSceneAsync(Images.Scene(
                   new ScreenAssignmentDto(H2, Rotation: 90), new ScreenAssignmentDto(Tl), new ScreenAssignmentDto(Turzx, Rotation: 270))))
        {
            t.Commit(); // a raw commit never writes settings
        }

        await rig.Manager.DisposeAsync();
        File.Delete(path); // the data directory as an older Host left it: scene.json only
        var enumerations = rig.Enumerator.Calls;

        await using (var second = new ScreenManager(rig.Options, rig.DataDirectory, rig.Platform))
        {
            (await second.GetScreenSceneAsync()).Assignments.Select(a => a.Rotation).Should().Equal(90, 0, 270);
            (await second.GetScreenDeviceSettingsAsync(H2)).Rotation.Should().Be(90);
            (await second.GetScreenDeviceSettingsAsync(Turzx)).Rotation.Should().Be(270);
            (await second.GetScreenDeviceSettingsAsync(Tl)).Should().Be(new ScreenDeviceSettingsDto(Tl, 0, Legacy, false));
            ScreenDeviceSettingsStore.Parse(File.ReadAllBytes(path)).Keys.Should().BeEquivalentTo([H2, Turzx], "zero is no preference");

            using (var t = await second.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(H2), new ScreenAssignmentDto(Tl, Rotation: 180))))
            {
                t.Commit();
            }

            (await second.GetScreenSceneAsync()).Assignments.Select(a => a.Rotation).Should().Equal(90, 180);
        }

        await using var third = new ScreenManager(rig.Options, rig.DataDirectory, rig.Platform);
        (await third.GetScreenSceneAsync()).Assignments.Select(a => a.Rotation).Should().Equal(90, 180);
        ScreenDeviceSettingsStore.Parse(File.ReadAllBytes(path)).Keys.Should().BeEquivalentTo([H2, Turzx], "the migration ran once");
        (await third.GetScreenDeviceSettingsAsync(Tl)).Rotation.Should().Be(0);
        rig.Opener.Opens.Should().Be(0);
        rig.Enumerator.Calls.Should().Be(enumerations, "migration and reads do no device I/O");
    }

    [Fact]
    public async Task An_unreadable_settings_file_is_never_migrated_over_and_an_explicit_save_replaces_it()
    {
        await using var rig = new Rig(null, Devices.H2);
        var path = rig.Manager.DeviceSettingsPath;
        using (var t = await rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, Rotation: 90))))
        {
            t.Commit();
        }

        await rig.Manager.DisposeAsync();
        File.WriteAllText(path, "{ not json");

        await using var second = new ScreenManager(rig.Options, rig.DataDirectory, rig.Platform);
        (await second.GetScreenDeviceSettingsAsync(H2)).Rotation.Should().Be(0, "no settings are in effect");
        (await second.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(90, "unconfigured content keeps its own rotation");
        File.ReadAllText(path).Should().Be("{ not json", "the unreadable file is not overwritten by a migration");

        await second.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 180));
        ScreenDeviceSettingsStore.Parse(File.ReadAllBytes(path)).Should().BeEquivalentTo(
            new Dictionary<string, ScreenDeviceSetting> { [H2] = new(180, Legacy) });
        (await second.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(180);
    }

    [Fact]
    public async Task Direct_SetScreenScene_establishes_only_a_missing_nonzero_rotation_and_raw_commits_never_do()
    {
        await using var rig = new Rig(null, Devices.H2, Devices.Tl1, Devices.Turzx);
        var path = rig.Manager.DeviceSettingsPath;

        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, Rotation: 90), new ScreenAssignmentDto(Tl)));
        (await rig.Manager.GetScreenDeviceSettingsAsync(H2)).Rotation.Should().Be(90);
        ScreenDeviceSettingsStore.Parse(File.ReadAllBytes(path)).Keys.Should().Equal(H2);

        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, Rotation: 180)));
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(90, "an established local value is authoritative");
        (await rig.Manager.GetScreenDeviceSettingsAsync(H2)).Rotation.Should().Be(90);

        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 270));
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2)));
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(270);

        using (var t = await rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(H2), new ScreenAssignmentDto(Turzx, Rotation: 90))))
        {
            t.Commit();
        }

        (await rig.Manager.GetScreenSceneAsync()).Assignments.Select(a => a.Rotation).Should().Equal(270, 90);
        (await rig.Manager.GetScreenDeviceSettingsAsync(Turzx)).Rotation.Should().Be(0, "RGB scenes, profiles and packages never write a setting");
        ScreenDeviceSettingsStore.Parse(File.ReadAllBytes(path)).Keys.Should().Equal(H2);

        using (await rig.Manager.BeginSceneAsync(Images.Scene(new ScreenAssignmentDto(Tl, Rotation: 90))))
        {
            // disposed without Commit: nothing changes
        }

        (await rig.Manager.GetScreenSceneAsync()).Assignments.Select(a => a.ScreenId).Should().Equal(H2, Turzx);
    }

    [Fact]
    public async Task NormalizeToLocal_replaces_only_saved_rotations_and_never_mutates_the_candidate()
    {
        await using var rig = new Rig();
#pragma warning disable CA1859 // deliberately through the interface: the manager's implementation, not the default method, must answer
        IScreenSceneBinding binding = rig.Manager;
#pragma warning restore CA1859
        var candidate = Images.Scene(new ScreenAssignmentDto(H2, "0123456789abcdef", Overlay: "compact", Fps: 10, TimelineId: "t1"),
            new ScreenAssignmentDto(Tl, Rotation: 180));

        binding.NormalizeToLocal(candidate).Should().BeSameAs(candidate, "nothing is saved yet");
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 90));

        var shown = binding.NormalizeToLocal(candidate);
        shown.Assignments.Should().Equal(candidate.Assignments[0] with { Rotation = 90 }, candidate.Assignments[1]);
        candidate.Assignments[0].Rotation.Should().Be(0);
    }

    [Fact]
    public async Task Saved_settings_survive_a_restart_and_reach_the_startup_scene_and_workers()
    {
        await using var rig = new Rig(null, Devices.H2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2)));
        (await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 90, Lossless)))
            .Should().Be(new ScreenDeviceSettingsDto(H2, 90, Lossless, false));
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(90);
        var path = rig.Manager.DeviceSettingsPath;
        await rig.Manager.DisposeAsync();
        File.ReadAllBytes(path)[0].Should().Be((byte)'{');

        await using var second = new ScreenManager(rig.Options, rig.DataDirectory, rig.Platform);
        (await second.GetScreenDeviceSettingsAsync(H2)).Should().Be(new ScreenDeviceSettingsDto(H2, 90, Lossless, false));
        (await second.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(90);
        second.EncodingOf(H2).Should().Be(new ScreenEncodingSelection(Lossless, false));
    }

    [Theory]
    [InlineData("tl", 0, 90)]
    [InlineData("tl", 90, 90)]
    [InlineData("tl", 180, 90)]
    [InlineData("tl", 270, 90)]
    [InlineData("h2", 0, 90)]
    [InlineData("h2", 90, 90)]
    [InlineData("h2", 180, 90)]
    [InlineData("h2", 270, 90)]
    [InlineData("turzx", 0, 90)]
    [InlineData("turzx", 90, 90)]
    [InlineData("turzx", 180, 90)]
    [InlineData("turzx", 270, 90)]
    [InlineData("turzx", 90, 270)]
    [InlineData("turzx", 180, 270)]
    public async Task The_local_rotation_reaches_the_wire_once_and_turzx_maps_landscape_once(string which, int rotation, int landscape)
    {
        var device = which switch { "tl" => Devices.Tl1, "h2" => Devices.H2, _ => Devices.Turzx };
        var family = device.Family;
        await using var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.TurzxLandscapeRotation = landscape;
        }, device);
        var asset = await UploadQuadrantsAsync(rig.Manager);
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(device.Id, rotation));

        // the content itself carries a stale rotation (as a package/profile would): the local setting wins
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(device.Id, asset, Rotation: (rotation + 180) % 360)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => LastContent(rig, device.Id, family) is not null, "a content frame", 15_000);

        var image = LastContent(rig, device.Id, family)!;
        var sent = await Sta.RunAsync(() => HeadlessProbe.Decode(image));
        if (family.Codec == ScreenCodec.TurzxPng)
        {
            (sent.Width, sent.Height).Should().Be((480, 1920), "the native portrait raster: one landscape mapping");
            sent = PixelOps.Rotate(sent, (360 - landscape) % 360);
        }
        else
        {
            (sent.Width, sent.Height).Should().Be((family.Width, family.Height));
        }

        Quadrants(sent).Should().Equal(Expected[rotation], "the user rotation is composed exactly once");
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(rotation);
    }

    [Fact]
    public async Task A_save_changes_only_that_assignment_and_keeps_pause_content_gates_and_unassigned_screens()
    {
        await using var rig = new Rig(o =>
        {
            o.Enabled = true;
            o.TlLcd.Output = true;
            o.H2.Output = true; // TURZX output stays closed
        }, Devices.Tl1, Devices.Tl2, Devices.H2, Devices.Turzx);
        var asset = await UploadQuadrantsAsync(rig.Manager);
        var h2 = new ScreenAssignmentDto(H2, asset, "#102030", "compact", 0, 0.7, 10, true, "#FF00FF", "contain");
        var tl = new ScreenAssignmentDto(Tl, BackgroundColor: "#203040", Rotation: 180);
        var turzx = new ScreenAssignmentDto(Turzx, BackgroundColor: "#405060");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(h2, tl, turzx));
        rig.Manager.Start();
        await Wait.UntilAsync(() => Writes(rig, H2) > 0 && Writes(rig, Tl) > 0, "both enabled screens running", 15_000);

        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(H2)).State == ScreenStates.Stopped && (await rig.ScreenAsync(Tl)).State == ScreenStates.Stopped, "paused");
        var opens = rig.Opener.Opens;
        var writes = Writes(rig, H2) + Writes(rig, Tl);

        (await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 270))).Should().Be(new ScreenDeviceSettingsDto(H2, 270, Legacy, false));
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Devices.Tl2.Id, 90)); // unassigned
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Turzx, 180)); // output gate closed
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto("screen:h2:offline0", 90)); // never seen
        await Task.Delay(600);

        (await rig.Manager.GetScreenSceneAsync()).Assignments.Should().Equal(h2 with { Rotation = 270 }, tl, turzx with { Rotation = 180 });
        (await rig.ScreenAsync(H2)).State.Should().Be(ScreenStates.Stopped, "a save never resumes a StopScreens pause");
        (await rig.ScreenAsync(Tl)).State.Should().Be(ScreenStates.Stopped);
        (await rig.ScreenAsync(Turzx)).State.Should().Be(ScreenStates.Disabled);
        rig.Opener.Opens.Should().Be(opens);
        (Writes(rig, H2) + Writes(rig, Tl)).Should().Be(writes);
        rig.Opener.For(Devices.Tl2.Id).Should().BeEmpty("a save never starts an unassigned screen");
        rig.Opener.For(Turzx).Should().BeEmpty();
        (rig.Options.Enabled, rig.Options.Turzx.Output, rig.Options.H2.Output).Should().Be((true, false, true));

        // the next scene commit resumes as before, still in the local orientation
        await rig.Manager.SetScreenSceneAsync(Images.Scene(h2, tl, turzx));
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Select(a => a.Rotation).Should().Equal(270, 180, 180);
        await Wait.UntilAsync(() => Writes(rig, H2) + Writes(rig, Tl) > writes, "resumed by the scene commit", 15_000);
    }

    [Fact]
    public async Task Refused_updates_change_no_file_scene_setting_or_worker()
    {
        await using var rig = new Rig(null, Devices.H2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, Rotation: 90))); // establishes H2 = 90
        var path = rig.Manager.DeviceSettingsPath;
        var file = File.ReadAllBytes(path);
        var scene = await rig.Manager.GetScreenSceneAsync();

        foreach (var bad in new ScreenDeviceSettingsUpdateDto[]
                 {
                     new("screen:a065:x", 0), new("", 0), new(null!, 0), new("SCREEN:H2:X", 0), new("C:\\ProgramData\\x", 0),
                     new(H2, 45), new(H2, -90), new(H2, 360),
                     new(H2, 0, "LEGACY"), new(H2, 0, ""), new(H2, 0, null!), new(H2, 0, "lossless-first-v2"),
                     new(H2, 0, ScreenEncodingPolicies.PaletteFirstV1),
                 })
        {
            await FluentActions.Awaiting(() => rig.Manager.SetScreenDeviceSettingsAsync(bad))
                .Should().ThrowAsync<ArgumentException>(bad.ToString()).WithMessage("*Nothing was changed.*");
            File.ReadAllBytes(path).Should().Equal(file, bad.ToString());
        }

        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene, o => o.WithStrictOrdering());
        (await rig.Manager.GetScreenDeviceSettingsAsync(H2)).Rotation.Should().Be(90);

        for (var i = 0; i < ScreenDeviceSettingsStore.MaxEntries - 1; i++)
        {
            await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto($"screen:tl-lcd:x{i:00}", 90));
        }

        file = File.ReadAllBytes(path);
        await FluentActions.Awaiting(() => rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto("screen:tl-lcd:overflow", 90)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("At most 64 screens*");
        File.ReadAllBytes(path).Should().Equal(file);
        (await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 180))).Rotation.Should().Be(180, "a full store still updates a saved screen");

        file = File.ReadAllBytes(path);
        scene = await rig.Manager.GetScreenSceneAsync();
        using var lockedSettings = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // block the real atomic target replacement
        await FluentActions.Awaiting(() => rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 270)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not be saved*Nothing was changed.");
        File.ReadAllBytes(path).Should().Equal(file);
        (await rig.Manager.GetScreenDeviceSettingsAsync(H2)).Rotation.Should().Be(180);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene, o => o.WithStrictOrdering());
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Indexed8Verified_comes_only_from_configuration_and_the_palette_policy_stays_refused()
    {
        await using var rig = new Rig(o => o.Indexed8VerifiedScreenIds = [H2, "not-a-screen", "SCREEN:H2:1D3343B614A38505"]);

        (await rig.Manager.GetScreenDeviceSettingsAsync(H2)).Should().Be(new ScreenDeviceSettingsDto(H2, 0, Legacy, true));
        (await rig.Manager.GetScreenDeviceSettingsAsync(Tl)).Indexed8Verified.Should().BeFalse();
        rig.Manager.EncodingOf(H2).Should().Be(new ScreenEncodingSelection(Legacy, true));
        rig.Manager.EncodingOf(Tl).Should().BeSameAs(ScreenEncodingSelection.Legacy);

        foreach (var id in new[] { H2, Tl })
        {
            await FluentActions.Awaiting(() => rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(id, 0, ScreenEncodingPolicies.PaletteFirstV1)))
                .Should().ThrowAsync<ArgumentException>(id);
        }

        (await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Tl, 0, Lossless))).Should().Be(new ScreenDeviceSettingsDto(Tl, 0, Lossless, false));
        rig.Manager.EncodingOf(Tl).Should().Be(new ScreenEncodingSelection(Lossless, false));
        File.ReadAllText(rig.Manager.DeviceSettingsPath).Should().NotContain("palette").And.NotContain("indexed", "the capability is never persisted");
    }

    [Fact]
    public async Task The_saved_policy_reaches_the_worker_reencodes_a_cached_still_once_and_keeps_the_pause()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        var calls = new ConcurrentQueue<(string Quality, bool Indexed8)>();
        rig.Manager.EncoderForTest = (f, c, r, q, i) =>
        {
            calls.Enqueue((q, i));
            return ScreenFrameEncoder.Encode(f, c, r); // a recording double: only the plumbing is under test here
        };
        var still = new ScreenAssignmentDto(Tl, BackgroundColor: "#203040");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(still));
        rig.Manager.Start();
        await Wait.UntilAsync(() => Writes(rig, Tl) >= 2, "a cached still re-sent", 15_000);
        calls.Should().ContainSingle().Which.Should().Be((Legacy, false));

        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Tl, 0, Lossless));
        var writes = Writes(rig, Tl);
        await Wait.UntilAsync(() => Writes(rig, Tl) >= writes + 3, "three more frames", 15_000);
        calls.Count(c => c.Quality == Lossless).Should().Be(1, "the policy change re-encodes the cached still once, then reuses it");
        calls.Last().Should().Be((Lossless, false));

        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Tl)).State == ScreenStates.Stopped, "paused");
        var encodes = calls.Count;
        var opens = rig.Opener.Opens;
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Tl, 90, Legacy));
        await Task.Delay(500);
        (await rig.ScreenAsync(Tl)).State.Should().Be(ScreenStates.Stopped);
        calls.Count.Should().Be(encodes, "a paused worker encodes nothing");
        rig.Opener.Opens.Should().Be(opens);
        (await rig.Manager.GetScreenSceneAsync()).Assignments.Single().Should().Be(still with { Rotation = 90 });

        await rig.Manager.SetScreenSceneAsync(Images.Scene(still));
        await Wait.UntilAsync(() => calls.Count > encodes, "resumed by the scene commit", 15_000);
        calls.Last().Should().Be((Legacy, false));
    }

    [Fact]
    public async Task Preview_decodes_the_exact_encoded_bytes_with_the_local_rotation_and_policy()
    {
        await using var rig = new Rig();
        var asset = await UploadQuadrantsAsync(rig.Manager);
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(H2, 90, Lossless));
        var seen = new ConcurrentQueue<string>();
        rig.Manager.EncoderForTest = (f, c, r, q, i) =>
        {
            seen.Enqueue(q);
            return ScreenFrameEncoder.Encode(f, c, r);
        };
        var a = new ScreenAssignmentDto(H2, asset);

        var p = await rig.Manager.PreviewScreenAsync(a);
        seen.Should().Equal(Lossless);
        p.Note.Should().StartWith("JPEG q").And.Contain("; rotation 90 from the local device settings");
        Quadrants(await DecodeAsync(p)).Should().Equal(Expected[90]);

        // what is shown is the encoded image, not the composed canvas
        rig.Manager.EncoderForTest = (f, c, r, q, i) => ScreenFrameEncoder.Encode(f, PixelFrame.Solid(f.Width, f.Height, 0, 255, 0), r);
        Quadrants(await DecodeAsync(await rig.Manager.PreviewScreenAsync(a))).Should().OnlyContain(c => c == "green");

        rig.Manager.EncoderForTest = (f, c, r, q, i) => throw new ScreenEncodeException("over the image budget");
        await FluentActions.Awaiting(() => rig.Manager.PreviewScreenAsync(a)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NOT SENDABLE: over the image budget");

        rig.Manager.EncoderForTest = (f, c, r, q, i) => ScreenFrameEncoder.Encode(ScreenFamilyInfo.TlLcd, PixelFrame.Solid(400, 400, 1, 2, 3), r);
        await FluentActions.Awaiting(() => rig.Manager.PreviewScreenAsync(a)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NOT SENDABLE: the encoded frame is 400x400*");
        rig.Opener.Opens.Should().Be(0);
    }

    [Fact]
    public async Task An_unsendable_production_frame_is_an_explicit_preview_failure_never_the_raw_canvas()
    {
        await using var rig = new Rig(o => o.TurzxLandscapeRotation = 45);

        await FluentActions.Awaiting(() => rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(Turzx, BackgroundColor: "#102030")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("NOT SENDABLE: *");
        (await rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(H2))).Width.Should().Be(480, "other families are unaffected");
    }

    [Fact]
    public async Task A_turzx_preview_is_the_decoded_native_png_turned_back_to_landscape()
    {
        await using var rig = new Rig(o => o.TurzxLandscapeRotation = 270);
        var asset = await UploadQuadrantsAsync(rig.Manager);
        await rig.Manager.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Turzx, 180));

        var p = await rig.Manager.PreviewScreenAsync(new ScreenAssignmentDto(Turzx, asset));

        (p.Width, p.Height).Should().Be((1920, 480));
        p.Note.Should().StartWith("PNG ").And.Contain("; rotation 180 from the local device settings");
        var shown = await DecodeAsync(p);
        (shown.Width, shown.Height).Should().Be((1920, 480));
        Quadrants(shown).Should().Equal(Expected[180]);
    }

    internal static async Task<string> UploadQuadrantsAsync(ScreenManager manager)
    {
        var png = await Images.PngAsync(QuadrantImage());
        return (await manager.UploadScreenAssetAsync(new ScreenAssetUploadDto("quadrants", Convert.ToBase64String(png)))).Id;
    }

    /// <summary>64x32: top-left red, top-right green, bottom-right white, bottom-left blue (BGRA).</summary>
    internal static PixelFrame QuadrantImage()
    {
        var f = new PixelFrame(64, 32);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var (r, g, b) = (x < 32, y < 16) switch
                {
                    (true, true) => (255, 0, 0),
                    (false, true) => (0, 255, 0),
                    (false, false) => (255, 255, 255),
                    _ => (0, 0, 255),
                };
                var i = ((y * 64) + x) * 4;
                f.Pixels[i] = (byte)b;
                f.Pixels[i + 1] = (byte)g;
                f.Pixels[i + 2] = (byte)r;
                f.Pixels[i + 3] = 255;
            }
        }

        return f;
    }

    /// <summary>The colour names at the centres of the TL, TR, BR and BL quadrants.</summary>
    internal static string[] Quadrants(PixelFrame f) =>
    [
        Colour(f, f.Width / 4, f.Height / 4),
        Colour(f, 3 * f.Width / 4, f.Height / 4),
        Colour(f, 3 * f.Width / 4, 3 * f.Height / 4),
        Colour(f, f.Width / 4, 3 * f.Height / 4),
    ];

    private static string Colour(PixelFrame f, int x, int y)
    {
        var (b, g, r, _) = f.At(x, y);
        (string Name, int R, int G, int B)[] palette = [("red", 255, 0, 0), ("green", 0, 255, 0), ("blue", 0, 0, 255), ("white", 255, 255, 255)];
        var best = palette.MinBy(p => Math.Abs(p.R - r) + Math.Abs(p.G - g) + Math.Abs(p.B - b));
        return Math.Abs(best.R - r) + Math.Abs(best.G - g) + Math.Abs(best.B - b) <= 90 ? best.Name : $"?({r},{g},{b})";
    }

    private static Task<PixelFrame> DecodeAsync(ScreenPreviewDto p) =>
        Sta.RunAsync(() => WpfCodec.DecodePng(Convert.FromBase64String(p.PngBase64), 1920 * 480));

    private static int Writes(Rig rig, string id) => rig.Opener.For(id).Sum(c => c.Writes.Count);

    /// <summary>The image of the last CONTENT packet written to <paramref name="id"/> (an H2 layer-clear PNG is skipped).</summary>
    private static byte[]? LastContent(Rig rig, string id, ScreenFamilyInfo family)
    {
        var command = family.Codec == ScreenCodec.TurzxPng ? ScreenProtocol.PushPngCommand : ScreenProtocol.PushJpegCommand;
        foreach (var packet in rig.Opener.For(id).SelectMany(c => c.Writes).Reverse())
        {
            var header = ScreenProtocol.DecryptHeader(family.Family, packet.AsSpan(0, 512));
            if (header[0] == command)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                return packet.AsSpan(512, length).ToArray();
            }
        }

        return null;
    }
}

/// <summary>The separate IScreenDeviceSettingsService target on the real Host pipe (fake screen platform, temp data directory).</summary>
[Collection("ScreensHost")]
public sealed class DeviceSettingsRpcTests
{
    [Fact]
    public async Task Device_settings_travel_on_the_trusted_pipe_with_async_dropped_names_and_server_computed_capability()
    {
        var options = new UnifiedRgbOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N")),
            PipeName = "UnifiedRgb-screens-test-" + Guid.NewGuid().ToString("N"),
            UseVirtualAdapter = true,
        };
        options.Screens.NvmlTelemetry = false;
        options.Screens.Indexed8VerifiedScreenIds = [Devices.Tl2.Id];
        var opener = new FakeOpener();
        var platform = new ScreenPlatform(new FakeEnumerator(Devices.H2), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddUnifiedRgb(options);
        builder.Services.AddSingleton<IServiceControl, RefusingServiceControl>();
        builder.Services.AddSingleton(platform);
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<PipeServer>().Listening.WaitAsync(TimeSpan.FromSeconds(10));
            host.Services.GetRequiredService<IScreenDeviceSettingsService>().Should().BeSameAs(host.Services.GetRequiredService<ScreenManager>());

            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
            var settings = rpc.Attach<IScreenDeviceSettingsService>();
            var screens = rpc.Attach<IScreenService>();
            rpc.StartListening();

            (await settings.GetScreenDeviceSettingsAsync(Devices.H2.Id)).Should().Be(new ScreenDeviceSettingsDto(Devices.H2.Id, 0, "legacy", false));
            await screens.SetScreenSceneAsync(new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#102030")]));
            (await settings.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Devices.H2.Id, 90, "lossless-first-v1")))
                .Should().Be(new ScreenDeviceSettingsDto(Devices.H2.Id, 90, "lossless-first-v1", false));
            (await screens.GetScreenSceneAsync()).Assignments.Single().Rotation.Should().Be(90);

            var raw = await rpc.InvokeWithCancellationAsync<JsonElement>("GetScreenDeviceSettings", [Devices.H2.Id], CancellationToken.None);
            raw.EnumerateObject().Select(p => p.Name).Should().Equal("screenId", "rotation", "encodingQuality", "indexed8Verified");

            // a caller cannot claim the capability: the member is not part of the update and is ignored
            var claimed = await rpc.InvokeWithCancellationAsync<JsonElement>("SetScreenDeviceSettings",
                [new { screenId = Devices.H2.Id, rotation = 180, encodingQuality = "legacy", indexed8Verified = true }], CancellationToken.None);
            claimed.GetProperty("indexed8Verified").GetBoolean().Should().BeFalse();
            claimed.GetProperty("rotation").GetInt32().Should().Be(180);
            (await settings.GetScreenDeviceSettingsAsync(Devices.Tl2.Id)).Indexed8Verified.Should().BeTrue("configured on the Host only");

            await FluentActions.Awaiting(() => settings.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Devices.H2.Id, 45)))
                .Should().ThrowAsync<RemoteInvocationException>().WithMessage("*Nothing was changed.*");
            await FluentActions.Awaiting(() => settings.SetScreenDeviceSettingsAsync(new ScreenDeviceSettingsUpdateDto(Devices.H2.Id, 0, "palette-first-v1")))
                .Should().ThrowAsync<RemoteInvocationException>();
            (await settings.GetScreenDeviceSettingsAsync(Devices.H2.Id)).Rotation.Should().Be(180);
            opener.Opens.Should().Be(0, "screen output is off by default; settings never open a device");
        }
        finally
        {
            await host.StopAsync();
            try
            {
                Directory.Delete(options.ResolvedDataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private sealed class RefusingServiceControl : IServiceControl
    {
        public ServiceInfo? Query(string name) => throw new InvalidOperationException("SCM access in a test");

        public void Stop(string name, TimeSpan timeout) => throw new InvalidOperationException("SCM access in a test");

        public void Start(string name, TimeSpan timeout) => throw new InvalidOperationException("SCM access in a test");

        public void SetStartType(string name, string startType) => throw new InvalidOperationException("SCM access in a test");
    }
}
