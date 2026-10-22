using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using Xunit.Abstractions;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The UNWIRED bounded overlay preparer (S1a part C): dedupe on the last REQUESTED key, one in flight + one pending + one result,
/// no starvation under fast-changing telemetry, generation poisoning, age, byte-affecting keys, ownership, a stuck encoder that is
/// abandoned without spawning more threads, and the 36 representative overlays hashing identically to the worker's inline
/// operations in WIC and NONE mode. Desktop only: no device, Host or USB.
/// </summary>
public sealed class TurzxOverlayPreparerTests(ITestOutputHelper output)
{
    /// <summary>Set to a directory to also write the 36-overlay table and summary there.</summary>
    public const string BenchmarkDirectoryVariable = "UNIFIEDRGB_S1A_BENCH_DIR";

    private static readonly ScreenAssignmentDto Assignment = new("screen:turzx:a", Overlay: "dashboard", Fps: 30);

    [Fact]
    public async Task Repeated_requests_are_deduplicated_against_the_last_requested_key_which_is_not_the_last_sent()
    {
        using var encoder = new FakeEncoder();
        using var preparer = Preparer(encoder);
        var a = Layer(1);
        preparer.Request(Request(a)).Should().Be(OverlayRequestOutcome.Queued);
        preparer.Request(Request(a)).Should().Be(OverlayRequestOutcome.Deduplicated, "pending or in flight");
        await Held(preparer);
        preparer.Request(Request(a)).Should().Be(OverlayRequestOutcome.Deduplicated, "held, not yet taken");
        preparer.TryTake(1, out var first).Should().Be(OverlayTakeOutcome.Ready);
        preparer.Request(Request(a)).Should().Be(OverlayRequestOutcome.Deduplicated, "already handed over: an unchanged layer is never re-prepared");
        preparer.Request(Request(a, assignment: Assignment with { Fps = 60, AccentColor = "#00FF00", Fit = "cover", BackgroundColor = "#123456" }))
            .Should().Be(OverlayRequestOutcome.Deduplicated, "fields that cannot change this rendered layer's bytes");

        encoder.Gated = true;
        var b = Layer(2);
        preparer.Request(Request(b)).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(2).Should().BeTrue();
        preparer.Request(Request(a)).Should().Be(OverlayRequestOutcome.Queued, "a was last SENT, but b was last REQUESTED: going back to a is new work");
        encoder.Release(2);
        await Wait.UntilAsync(() => preparer.Snapshot() is { Completed: 3, InFlight: false, Pending: false }, "both encodes");
        preparer.TryTake(1, out var last).Should().Be(OverlayTakeOutcome.Ready);
        (first!.Stamp, last!.Stamp, last.Bytes![^1]).Should().Be((1L, 3L, (byte)1));
        var s = preparer.Snapshot();
        (s.Requests, s.Queued, s.Deduplicated, s.Encodes, s.ReplacedResults, s.Handed).Should().Be((7L, 3L, 4L, 3L, 1L, 2L));
        encoder.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Each_byte_affecting_input_prepares_again_and_an_older_generation_is_refused()
    {
        using var encoder = new FakeEncoder();
        using var preparer = Preparer(encoder);
        var layer = Layer(1);
        var distinct = new[]
        {
            Request(layer), Request(layer, assignment: Assignment with { Rotation = 180 }),
            Request(layer, assignment: Assignment with { Brightness = 0.6 }), Request(layer, internalRotation: 270),
            Request(layer, simplePng: true), Request(Layer(1), simplePng: true), Request(layer, generation: 2),
        };
        foreach (var request in distinct)
        {
            preparer.Request(request).Should().Be(OverlayRequestOutcome.Queued);
            await Held(preparer);
            preparer.TryTake(request.Generation, out _).Should().Be(OverlayTakeOutcome.Ready);
        }

        preparer.Request(Request(layer, generation: 2)).Should().Be(OverlayRequestOutcome.Deduplicated);
        preparer.Request(Request(Layer(9), generation: 1)).Should().Be(OverlayRequestOutcome.StaleGeneration);
        var s = preparer.Snapshot();
        (s.Queued, s.Encodes, s.Handed, s.Deduplicated, s.StaleRequests, s.Generation).Should().Be((7L, 7L, 7L, 1L, 1L, 2L));
        encoder.Seen.Select(r => (r.Generation, r.Assignment.Rotation, r.Assignment.Brightness, r.InternalRotation, r.SimplePng)).Should().Equal(
            (1L, 0, 1.0, 90, false), (1L, 180, 1.0, 90, false), (1L, 0, 0.6, 90, false), (1L, 0, 1.0, 270, false),
            (1L, 0, 1.0, 90, true), (1L, 0, 1.0, 90, true), (2L, 0, 1.0, 90, false));
    }

    [Fact]
    public async Task A_slow_encoder_holds_at_most_one_in_flight_one_pending_and_one_result()
    {
        using var encoder = new FakeEncoder { Gated = true };
        using var preparer = Preparer(encoder);
        var layers = Enumerable.Range(0, 5).Select(i => Layer((byte)(10 + i))).ToArray();
        preparer.Request(Request(layers[0])).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(1).Should().BeTrue();
        foreach (var layer in layers.Skip(1)) preparer.Request(Request(layer)).Should().Be(OverlayRequestOutcome.Queued);
        var s = preparer.Snapshot();
        (s.InFlight, s.Pending, s.ResultHeld, s.ReplacedPending, s.RetainedLayers, s.RetainedLayerBytes).Should()
            .Be((true, true, false, 3L, 2, (long)(layers[0].ByteCount + layers[4].ByteCount)), "replaced pending layers are released, never queued");

        encoder.Release();
        encoder.WaitCalls(2).Should().BeTrue("the latest pending request starts as soon as the encoder is free");
        await Held(preparer);
        s = preparer.Snapshot();
        (s.InFlight, s.Pending, s.ResultHeld, s.RetainedLayers, s.RetainedLayerBytes, s.ResultBytes).Should()
            .Be((true, false, true, 1, (long)layers[4].ByteCount, 12));
        preparer.TryTake(1, out var older).Should().Be(OverlayTakeOutcome.Ready);
        older!.Bytes![^1].Should().Be(10);
        encoder.Release();
        await Held(preparer);
        preparer.TryTake(1, out var newest).Should().Be(OverlayTakeOutcome.Ready);
        (older.Stamp, newest!.Stamp, newest.Bytes![^1]).Should().Be((1L, 5L, (byte)14));
        encoder.Seen.Select(r => r.Layer).Should().Equal(layers[0], layers[4]);

        preparer.Request(Request(layers[1])).Should().Be(OverlayRequestOutcome.Queued);
        encoder.Release();
        await Held(preparer);
        preparer.Request(Request(layers[2])).Should().Be(OverlayRequestOutcome.Queued);
        encoder.Release();
        await Wait.UntilAsync(() => preparer.Snapshot().Completed == 4, "the replacing result");
        preparer.TryTake(1, out var replaced).Should().Be(OverlayTakeOutcome.Ready);
        replaced!.Bytes![^1].Should().Be(12, "a newer result replaces the held one; results never queue");
        s = preparer.Snapshot();
        (s.Encodes, s.Completed, s.ReplacedResults, s.Handed, s.InFlight, s.Pending, s.ResultHeld, s.RetainedLayers, s.RetainedLayerBytes, s.ResultBytes)
            .Should().Be((4L, 4L, 1L, 3L, false, false, false, 1, (long)layers[2].ByteCount, 0), "idle: the dedupe key still references the last requested layer");
    }

    [Fact]
    public async Task Fast_changing_telemetry_cannot_starve_the_sender()
    {
        using var encoder = new FakeEncoder { Gated = true };
        using var preparer = Preparer(encoder);
        preparer.Request(Request(Layer(0))).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(1).Should().BeTrue();
        var handed = new List<long>();
        for (var i = 1; i <= 12; i++)
        {
            preparer.Request(Request(Layer((byte)i))).Should().Be(OverlayRequestOutcome.Queued, "telemetry changed again before the encode finished");
            preparer.Request(Request(Layer((byte)(100 + i)))).Should().Be(OverlayRequestOutcome.Queued, "and again");
            encoder.Release();
            encoder.WaitCalls(i + 1).Should().BeTrue();
            await Held(preparer);
            preparer.TryTake(1, out var taken).Should().Be(OverlayTakeOutcome.Ready, "a completed result is accepted although newer requests exist");
            handed.Add(taken!.Stamp);
        }

        handed.Should().HaveCount(12).And.BeInAscendingOrder().And.OnlyHaveUniqueItems();
        handed.Zip(handed.Skip(1)).Should().OnlyContain(p => p.Second > p.First);
        var s = preparer.Snapshot();
        (s.Handed, s.NotNewer, s.StaleResults, s.ReplacedPending).Should().Be((12L, 0L, 0L, 12L));
        encoder.Release();
    }

    [Fact]
    public async Task A_generation_change_poisons_pending_in_flight_and_held_results()
    {
        using var encoder = new FakeEncoder { Gated = true };
        using var preparer = Preparer(encoder);
        var a = Layer(1);
        preparer.Request(Request(a)).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(1).Should().BeTrue();
        preparer.Request(Request(Layer(2))).Should().Be(OverlayRequestOutcome.Queued);
        preparer.Invalidate(2);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => !s.Pending && s.InFlight && s.Poisoned == 1 && s.Generation == 2);
        encoder.Release();
        await Wait.UntilAsync(() => !preparer.Snapshot().InFlight, "the in-flight encode");
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.Poisoned == 2 && !s.ResultHeld && s.Completed == 0);
        preparer.TryTake(2, out _).Should().Be(OverlayTakeOutcome.None);
        preparer.Request(Request(Layer(3))).Should().Be(OverlayRequestOutcome.StaleGeneration);

        encoder.Gated = false;
        preparer.Request(Request(a, generation: 2)).Should().Be(OverlayRequestOutcome.Queued, "the same layer under a new generation is new work");
        await Held(preparer);
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.StaleCaller, "a caller still on generation 1 cannot use it, nor remove it");
        preparer.Request(Request(a, generation: 2)).Should().Be(OverlayRequestOutcome.Deduplicated, "the generation-2 result and its key are kept");
        preparer.TryTake(2, out var current).Should().Be(OverlayTakeOutcome.Ready);
        current!.Generation.Should().Be(2);
        preparer.Request(Request(Layer(4), generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(3, out _).Should().Be(OverlayTakeOutcome.None, "a caller ahead raises the generation first; the held result is poisoned");
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.Poisoned == 3 && s.StaleCallers == 1 && s.Generation == 3 && s.Handed == 1);
    }

    [Fact]
    public async Task A_result_older_than_the_maximum_age_is_dropped_and_may_be_requested_again()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        using var encoder = new FakeEncoder();
        using var preparer = new TurzxOverlayPreparer(time, TimeSpan.FromSeconds(2), encoder.Encode);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        preparer.Request(Request(Layer(1))).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        time.Advance(TimeSpan.FromSeconds(2));
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.Ready, "exactly the maximum age is still fresh");

        var b = Layer(2);
        preparer.Request(Request(b)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        time.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1));
        preparer.TryTake(1, out var stale).Should().Be(OverlayTakeOutcome.Stale);
        stale.Should().BeNull();
        preparer.Request(Request(b)).Should().Be(OverlayRequestOutcome.Queued, "a stale result does not dedupe its own layer forever");
        await Held(preparer);
        preparer.TryTake(1, out var fresh).Should().Be(OverlayTakeOutcome.Ready);
        (fresh!.RequestedAt, fresh.CompletedAt).Should().Be((time.GetTimestamp(), time.GetTimestamp()));
        (preparer.Snapshot().StaleResults, encoder.Calls).Should().Be((1L, 3));
        FluentActions.Invoking(() => new TurzxOverlayPreparer(time, TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Handed_bytes_belong_to_the_taker_and_the_layer_is_never_modified()
    {
        var layer = new PixelFrame(64, 32);
        new Random(5).NextBytes(layer.Pixels);
        var layerHash = Sha256(layer.Pixels);
        using var preparer = new TurzxOverlayPreparer(TimeProvider.System, TurzxOverlayPreparer.DefaultMaxAge);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        var dimmed = Assignment with { Brightness = 0.6 };

        preparer.Request(Request(layer, assignment: dimmed)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(1, out var wic).Should().Be(OverlayTakeOutcome.Ready);
        var wicHash = Sha256(wic!.Bytes!);
        preparer.Request(Request(layer, assignment: dimmed, simplePng: true)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(1, out var none).Should().Be(OverlayTakeOutcome.Ready);
        preparer.Request(Request(layer, generation: 2, assignment: dimmed)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(2, out var again).Should().Be(OverlayTakeOutcome.Ready);

        again!.Bytes.Should().NotBeSameAs(wic.Bytes).And.Equal(wic.Bytes, "the same inputs give the same bytes in a fresh array");
        Sha256(wic.Bytes!).Should().Be(wicHash, "nothing touches a handed array afterwards");
        Sha256(layer.Pixels).Should().Be(layerHash, "the first rotation copies the layer before brightness");
        (wic.Note, none!.Note).Should().Be(("RGBA8 WIC PNG", "RGBA8 filter NONE PNG; exact lossless repack"));
        wic.AllocatedBytes.Should().BeGreaterThan(0);
        output.WriteLine(FormattableString.Invariant($"64x32 layer: WIC {wic.Bytes!.Length} B, {wic.AllocatedBytes} B allocated; NONE {none.Bytes!.Length} B, {none.AllocatedBytes} B allocated"));
    }

    [Fact]
    public async Task A_stuck_encoder_is_abandoned_after_the_bounded_join_and_blocks_new_threads_until_it_really_exits()
    {
        using var encoder = new FakeEncoder { Gated = true };
        using var preparer = Preparer(encoder);
        preparer.Request(Request(Layer(1))).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(1).Should().BeTrue();
        var clock = Stopwatch.StartNew();
        preparer.Stop().Should().Be(OverlayStopOutcome.Abandoned);
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(450)).And.BeLessThan(TimeSpan.FromSeconds(3));
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.Thread == OverlayPreparerThreadState.Abandoned && s.ThreadAlive
            && s.InFlight && s.JoinTimeouts == 1 && s.ThreadsCreated == 1 && s.RetainedLayers == 1, "the abandoned encode still references its layer");
        for (var i = 0; i < 50; i++) preparer.Start().Should().Be(OverlayStartOutcome.Blocked);
        preparer.Request(Request(Layer(2))).Should().Be(OverlayRequestOutcome.NotRunning, "the caller keeps today's inline path");
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.BlockedStarts == 50 && s.ThreadsCreated == 1 && s.NotRunning == 1);

        encoder.Release();
        await Wait.UntilAsync(() => !preparer.Snapshot().ThreadAlive, "the abandoned thread to exit");
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.Thread == OverlayPreparerThreadState.None && !s.InFlight
            && !s.ResultHeld && s.Poisoned == 1 && s.Completed == 0 && s.RetainedLayers == 0);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        encoder.Gated = false;
        preparer.Request(Request(Layer(2))).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.Ready);
        preparer.Stop().Should().Be(OverlayStopOutcome.Joined);
        preparer.Snapshot().ThreadsCreated.Should().Be(2);
    }

    [Fact]
    public async Task An_encoder_failure_is_reported_once_and_never_retried_in_a_loop()
    {
        var bad = Layer(1);
        var empty = Layer(2);
        using var encoder = new FakeEncoder { Fail = bad, Empty = empty };
        using var preparer = Preparer(encoder);
        preparer.Request(Request(bad)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(1, out var failed).Should().Be(OverlayTakeOutcome.Failed);
        (failed!.Bytes, failed.Note, failed.Error).Should().Be(((byte[]?)null, (string?)null, "InvalidOperationException: fake WIC failure"));
        preparer.Request(Request(bad)).Should().Be(OverlayRequestOutcome.Deduplicated, "the same key is not prepared again");
        preparer.Request(Request(empty)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(1, out var nothing).Should().Be(OverlayTakeOutcome.Failed);
        nothing!.Error.Should().Be("The overlay encoder returned no bytes.");
        preparer.Request(Request(Layer(3))).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.Ready);
        (preparer.Snapshot().Failed, preparer.Snapshot().Completed, preparer.Snapshot().Handed, encoder.Calls).Should().Be((2L, 1L, 1L, 3));
    }

    [Fact]
    public async Task Every_encode_runs_on_one_dedicated_named_sta_background_thread()
    {
        using var encoder = new FakeEncoder();
        using var preparer = Preparer(encoder);
        for (var i = 0; i < 4; i++)
        {
            preparer.Request(Request(Layer((byte)i))).Should().Be(OverlayRequestOutcome.Queued);
            await Held(preparer);
            preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.Ready);
        }

        encoder.Threads.Should().HaveCount(4);
        encoder.Threads.Select(t => t.Id).Distinct().Should().ContainSingle().Which.Should().NotBe(Environment.CurrentManagedThreadId);
        encoder.Threads.Should().OnlyContain(t => t.Apartment == ApartmentState.STA && t.Background && t.Name == "TURZX overlay preparer");
    }

    [Fact]
    public async Task Lifecycle_outcomes_are_explicit_and_stop_or_dispose_poison_everything()
    {
        using var encoder = new FakeEncoder();
        var layer = Layer(1);
        var preparer = new TurzxOverlayPreparer(TimeProvider.System, TurzxOverlayPreparer.DefaultMaxAge, encoder.Encode);
        preparer.Request(Request(layer)).Should().Be(OverlayRequestOutcome.NotRunning);
        preparer.Stop().Should().Be(OverlayStopOutcome.NotRunning);
        preparer.Snapshot().Thread.Should().Be(OverlayPreparerThreadState.None);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        preparer.Start().Should().Be(OverlayStartOutcome.AlreadyRunning);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.Thread == OverlayPreparerThreadState.Running && s.ThreadAlive);
        preparer.Request(Request(layer)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.Stop().Should().Be(OverlayStopOutcome.Joined);
        preparer.Stop().Should().Be(OverlayStopOutcome.NotRunning);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => !s.ResultHeld && s.Poisoned == 1 && !s.ThreadAlive);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.None, "a stopped owner's result never survives a restart");
        preparer.Request(Request(layer)).Should().Be(OverlayRequestOutcome.Queued, "stop forgets the last requested key");
        await Held(preparer);
        preparer.Dispose();
        preparer.Dispose();
        preparer.Start().Should().Be(OverlayStartOutcome.Disposed);
        preparer.Request(Request(Layer(2))).Should().Be(OverlayRequestOutcome.NotRunning);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s =>
            s.Thread == OverlayPreparerThreadState.Disposed && !s.ThreadAlive && !s.ResultHeld && s.Poisoned == 2 && s.ThreadsCreated == 2);
    }

    // ---- root review 2026-10-22 (CODEX-S1A-REVIEW): each reproduced first against the original helper (work/s1a-fix) ----

    [Fact]
    public async Task Retained_layers_count_every_distinct_layer_the_preparer_references_once()
    {
        using var encoder = new FakeEncoder { Gated = true };
        using var preparer = Preparer(encoder);
        var tiny = new PixelFrame(2, 2); // the root probe's 16-byte layer
        preparer.Request(Request(tiny, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        encoder.Release();
        await Held(preparer);
        Retained(preparer).Should().Be((false, false, 1, 16L), "ready: only the dedupe key references the layer, and it counts");
        preparer.TryTake(2, out _).Should().Be(OverlayTakeOutcome.Ready);
        Retained(preparer).Should().Be((false, false, 1, 16L), "taken: the key keeps referencing it, to dedupe");

        PixelFrame a = Layer(1), b = new(4, 4), c = new(8, 8);
        preparer.Request(Request(a, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(2).Should().BeTrue();
        Retained(preparer).Should().Be((true, false, 1, (long)a.ByteCount), "in flight and keyed: one layer, counted once");
        preparer.Request(Request(b, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        Retained(preparer).Should().Be((true, true, 2, (long)(a.ByteCount + b.ByteCount)), "the key is the pending request's");
        preparer.Request(Request(c, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        Retained(preparer).Should().Be((true, true, 2, (long)(a.ByteCount + c.ByteCount)), "a replaced pending layer is no longer referenced");
        preparer.Request(Request(a, generation: 2, simplePng: true)).Should().Be(OverlayRequestOutcome.Queued);
        Retained(preparer).Should().Be((true, true, 1, (long)a.ByteCount), "one layer in flight and pending (other options) counts once");

        preparer.Invalidate(3);
        Retained(preparer).Should().Be((true, false, 1, (long)a.ByteCount), "invalidation drops the key and pending at once, the in-flight layer on return");
        encoder.Release();
        await Wait.UntilAsync(() => !preparer.Snapshot().InFlight, "the poisoned encode");
        Retained(preparer).Should().Be((false, false, 0, 0L));

        preparer.Request(Request(c, generation: 3)).Should().Be(OverlayRequestOutcome.Queued);
        encoder.Release();
        await Held(preparer);
        Retained(preparer).Should().Be((false, false, 1, (long)c.ByteCount));
        preparer.Stop().Should().Be(OverlayStopOutcome.Joined);
        Retained(preparer).Should().Be((false, false, 0, 0L));
    }

    [Fact]
    public async Task A_stale_taker_changes_nothing_while_current_and_future_takers_keep_their_meaning()
    {
        using var encoder = new FakeEncoder();
        using var preparer = Preparer(encoder);
        var layer = Layer(7);
        preparer.Request(Request(layer, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        var before = preparer.Snapshot();
        preparer.TryTake(1, out var stale).Should().Be(OverlayTakeOutcome.StaleCaller);
        stale.Should().BeNull();
        preparer.Snapshot().Should().Be(before with { StaleCallers = 1 }, "a caller behind the preparer changes nothing else");
        preparer.Request(Request(layer, generation: 2)).Should().Be(OverlayRequestOutcome.Deduplicated, "its dedupe key is kept");
        preparer.TryTake(2, out var current).Should().Be(OverlayTakeOutcome.Ready);
        (current!.Generation, current.Stamp, current.Bytes![^1]).Should().Be((2L, 1L, (byte)7));
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.StaleCaller, "with nothing held, a stale caller is still told so");
        preparer.TryTake(2, out _).Should().Be(OverlayTakeOutcome.None);

        preparer.Request(Request(Layer(8), generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        await Held(preparer);
        preparer.TryTake(3, out var future).Should().Be(OverlayTakeOutcome.None, "a caller ahead raises the generation and poisons generation 2");
        future.Should().BeNull();
        preparer.TryTake(2, out _).Should().Be(OverlayTakeOutcome.StaleCaller);
        preparer.Request(Request(layer, generation: 2)).Should().Be(OverlayRequestOutcome.StaleGeneration);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.Generation == 3 && !s.ResultHeld && s.RetainedLayers == 0
            && s.Handed == 1 && s.StaleCallers == 3 && s.Poisoned == 1 && s.Completed == 2);
    }

    [Fact]
    public async Task Stale_current_and_future_takers_during_an_in_flight_encode()
    {
        using var encoder = new FakeEncoder { Gated = true };
        using var preparer = Preparer(encoder);
        PixelFrame a = Layer(1), b = Layer(2);
        preparer.Request(Request(a, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        encoder.WaitCalls(1).Should().BeTrue();
        preparer.Request(Request(b, generation: 2)).Should().Be(OverlayRequestOutcome.Queued);
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.StaleCaller);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.InFlight && s.Pending && s.Poisoned == 0 && s.Generation == 2 && s.RetainedLayers == 2,
            "a stale caller neither poisons the in-flight encode nor drops the pending request");

        encoder.Release();
        await Held(preparer);
        encoder.WaitCalls(2).Should().BeTrue("the pending request is now in flight");
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.StaleCaller);
        preparer.TryTake(2, out var first).Should().Be(OverlayTakeOutcome.Ready);
        first!.Bytes![^1].Should().Be(1);

        preparer.TryTake(3, out _).Should().Be(OverlayTakeOutcome.None);
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => s.InFlight && s.Generation == 3 && s.RetainedLayers == 1,
            "a caller ahead forgets the key at once; the in-flight layer stays referenced until its encode returns");
        encoder.Release();
        await Wait.UntilAsync(() => !preparer.Snapshot().InFlight, "the poisoned encode");
        preparer.Snapshot().Should().Match<OverlayPreparerSnapshot>(s => !s.ResultHeld && s.Poisoned == 1 && s.Completed == 1 && s.RetainedLayers == 0);
        preparer.TryTake(3, out _).Should().Be(OverlayTakeOutcome.None);
        preparer.TryTake(2, out _).Should().Be(OverlayTakeOutcome.StaleCaller);

        encoder.Gated = false;
        preparer.Request(Request(b, generation: 3)).Should().Be(OverlayRequestOutcome.Queued, "the same layer under the new generation is new work");
        await Held(preparer);
        preparer.TryTake(3, out var third).Should().Be(OverlayTakeOutcome.Ready);
        (third!.Generation, third.Bytes![^1]).Should().Be((3L, (byte)2));
        (preparer.Snapshot().StaleCallers, preparer.Snapshot().Handed).Should().Be((3L, 2L));
    }

    /// <summary>
    /// Concurrency regression: a producer moves through generations while a stale taker, a current taker (with occasional
    /// future takes) and a snapshot sampler run concurrently. Every outcome is counted on both sides and must match exactly.
    /// </summary>
    [Fact]
    public async Task Concurrent_stale_current_and_future_takers_never_cross_generations_and_counts_match_exactly()
    {
        const int generations = 1000, perGeneration = 20;
        using var preparer = new TurzxOverlayPreparer(TimeProvider.System, TimeSpan.FromMinutes(1),
            static r => (BitConverter.GetBytes(r.Generation), "fake"));
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        preparer.Invalidate(2);
        var layers = Enumerable.Range(0, 4).Select(i => Layer((byte)i)).ToArray();
        long published = 2;
        var done = 0;
        var requestOutcomes = new long[4];

        var producer = Task.Run(() =>
        {
            for (var g = 2L; g < 2 + generations; g++)
            {
                Volatile.Write(ref published, g);
                for (var i = 0; i < perGeneration; i++)
                {
                    requestOutcomes[(int)preparer.Request(Request(layers[i % 4], g, internalRotation: i))]++;
                    Thread.Yield(); // let the takers interleave with every request
                }
            }

            Volatile.Write(ref done, 1);
        });
        var staleTaker = Task.Run(() =>
        {
            long calls = 0;
            while (Volatile.Read(ref done) == 0)
            {
                preparer.TryTake(1, out var taken).Should().Be(OverlayTakeOutcome.StaleCaller);
                taken.Should().BeNull();
                calls++;
            }

            return calls;
        });
        var currentTaker = Task.Run(() =>
        {
            var outcomes = new long[8];
            long lastStamp = 0, iteration = 0;
            while (Volatile.Read(ref done) == 0 || preparer.Snapshot() is { InFlight: true } or { Pending: true } or { ResultHeld: true })
            {
                var g = Volatile.Read(ref published) + (++iteration % 64 == 0 ? 1 : 0); // now and then a caller ahead
                var outcome = preparer.TryTake(g, out var taken);
                outcomes[(int)outcome]++;
                if (outcome != OverlayTakeOutcome.Ready) continue;
                (taken!.Generation, BitConverter.ToInt64(taken.Bytes!)).Should().Be((g, g), "a take never crosses generations");
                taken.Stamp.Should().BeGreaterThan(lastStamp);
                lastStamp = taken.Stamp;
            }

            return outcomes;
        });
        var maxRetained = 0;
        var sampler = Task.Run(() =>
        {
            while (Volatile.Read(ref done) == 0)
            {
                var s = preparer.Snapshot();
                maxRetained = Math.Max(maxRetained, s.RetainedLayers);
                s.ThreadsCreated.Should().Be(1);
            }
        });

        await Task.WhenAll(producer, staleTaker, currentTaker, sampler).WaitAsync(TimeSpan.FromSeconds(60));
        var staleCalls = await staleTaker;
        var takes = await currentTaker;
        var final = preparer.Snapshot();
        output.WriteLine(FormattableString.Invariant(
            $"requests {final.Requests} (queued {final.Queued}, stale {final.StaleRequests}); encodes {final.Encodes}; handed {final.Handed}; stale callers {final.StaleCallers} ({staleCalls} from the stale taker); future-raised none {takes[(int)OverlayTakeOutcome.None]}; poisoned {final.Poisoned}; max retained layers {maxRetained}"));

        final.Requests.Should().Be(generations * perGeneration);
        (final.Queued, final.Deduplicated, final.StaleRequests, final.NotRunning).Should().Be((
            requestOutcomes[(int)OverlayRequestOutcome.Queued], requestOutcomes[(int)OverlayRequestOutcome.Deduplicated],
            requestOutcomes[(int)OverlayRequestOutcome.StaleGeneration], 0L));
        final.Handed.Should().Be(takes[(int)OverlayTakeOutcome.Ready]).And.BeGreaterThan(0);
        final.StaleCallers.Should().Be(staleCalls + takes[(int)OverlayTakeOutcome.StaleCaller]);
        (final.NotNewer, final.StaleResults, final.Failed, final.ThreadsCreated).Should().Be((0L, 0L, 0L, 1L));
        (takes[(int)OverlayTakeOutcome.NotNewer], takes[(int)OverlayTakeOutcome.Stale], takes[(int)OverlayTakeOutcome.Failed]).Should().Be((0L, 0L, 0L));
        maxRetained.Should().BeLessThanOrEqualTo(2);
        preparer.Stop().Should().Be(OverlayStopOutcome.Joined);
        preparer.Snapshot().RetainedLayers.Should().Be(0);
    }

    [Fact]
    public async Task A_layer_the_snapshot_no_longer_counts_is_unreachable_from_the_preparer()
    {
        {
            using var encoder = new FakeEncoder { Gated = true };
            using var gated = Preparer(encoder);
            gated.Request(Request(Layer(1))).Should().Be(OverlayRequestOutcome.Queued);
            encoder.WaitCalls(1).Should().BeTrue();
            var replaced = RequestUnreferencedLayer(gated, 1);
            gated.Request(Request(Layer(2))).Should().Be(OverlayRequestOutcome.Queued);
            Collect();
            replaced.IsAlive.Should().BeFalse("a replaced pending layer is released at once");
            encoder.Release(2);
        }

        using var preparer = new TurzxOverlayPreparer(TimeProvider.System, TurzxOverlayPreparer.DefaultMaxAge, static _ => ([1], "fake"));
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        var invalidated = RequestUnreferencedLayer(preparer, 1);
        await Held(preparer);
        preparer.TryTake(1, out _).Should().Be(OverlayTakeOutcome.Ready);
        preparer.Invalidate(2);
        preparer.Snapshot().RetainedLayerBytes.Should().Be(0);
        Collect();
        invalidated.IsAlive.Should().BeFalse("nothing in the preparer, its idle thread frame included, may keep an uncounted layer");

        var stopped = RequestUnreferencedLayer(preparer, 2);
        await Held(preparer);
        preparer.Stop().Should().Be(OverlayStopOutcome.Joined);
        preparer.Snapshot().RetainedLayerBytes.Should().Be(0);
        Collect();
        stopped.IsAlive.Should().BeFalse();
    }

    /// <summary>
    /// The 36 representative overlays of the existing lossless benchmark (six layouts by six telemetry states, same definitions),
    /// prepared by the preparer's default encoder off the caller's thread and compared by complete SHA-256 with the worker's inline
    /// operations restated here exactly (rotation, brightness, landscape rotation, WIC encode, NONE repack and its fallback). Desktop
    /// timings and managed allocation only; nothing here measures a device.
    /// </summary>
    [Fact]
    public async Task The_36_representative_overlays_hash_identically_to_the_inline_operations_in_wic_and_none()
    {
        var layouts = new (string Name, ScreenAssignmentDto Assignment)[]
        {
            ("example-region", Example()),
            ("dashboard", new("screen:turzx:a", Overlay: "dashboard", Fps: 30)),
            ("compact", new("screen:turzx:a", Overlay: "compact", Fps: 30)),
            ("region-backed", Example() with { OverlayLayout = new(0.70, 0.0, 0.30, 1.0, 1, 0.6) }),
            ("region-full-4col", Example() with { OverlayLayout = new(0.0, 0.0, 1.0, 1.0, 4, 0.0) }),
            ("example-rotated-dimmed", Example() with { Rotation = 180, Brightness = 0.6 }),
        };
        var states = new (string Name, PcTelemetryDto? Telemetry)[]
        {
            ("typical", HeadlessProbe.Telemetry), ("longest", RoundExamples.Long), ("no-gpu", RoundExamples.NoGpu),
            ("missing", null), ("idle", Idle), ("busy", Busy),
        };
        var internalRotation = new ScreensOptions().TurzxLandscapeRotation;
        using var preparer = new TurzxOverlayPreparer(TimeProvider.System, TurzxOverlayPreparer.DefaultMaxAge);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);

        _ = await Measure(preparer, "warm-up", layouts[0].Assignment, states[0].Telemetry, internalRotation, 1); // JIT and WIC start-up
        var rows = new List<BenchRow>();
        foreach (var (layout, assignment) in layouts)
        {
            foreach (var (state, telemetry) in states) rows.AddRange(await Measure(preparer, layout + "/" + state, assignment, telemetry, internalRotation, 1));
        }

        rows.Should().HaveCount(72);
        rows.Where(r => r.PreparedSha256 != r.InlineSha256 || r.PreparedNote != r.InlineNote).Select(r => r.Name + " " + r.Mode).Should().BeEmpty();
        rows.Should().OnlyContain(r => r.LayerUnchanged && r.Bytes <= ScreenProtocol.TurzxMaxPngBytes);
        rows.Where(r => r.Mode == "none").Should().OnlyContain(r => r.InlineNote == "RGBA8 filter NONE PNG; exact lossless repack");
        var s = preparer.Snapshot();
        (s.Encodes, s.Completed, s.Handed, s.Failed, s.Poisoned, s.StaleResults, s.ReplacedResults).Should().Be((74L, 74L, 74L, 0L, 0L, 0L, 0L));
        preparer.Stop().Should().Be(OverlayStopOutcome.Joined);

        var summary = Summary(rows, s);
        foreach (var line in summary) output.WriteLine(line);
        if (Environment.GetEnvironmentVariable(BenchmarkDirectoryVariable) is { Length: > 0 } directory) WriteBenchmark(directory, rows, summary);
    }

    // ---- benchmark helpers (definitions mirror LosslessOverlayPngTests) ----

    private static readonly DateTimeOffset Now = HeadlessProbe.Now;
    private static readonly ScreenOverlayLayoutDto ExampleRegion = new(0.5, 0.2, 0.47, 0.66, 2, 0);
    private static readonly PcTelemetryDto Idle = new(Now, 0, 4L << 30, 32L << 30, 0, 30, 1L << 30, 8L << 30, "bench", null);
    private static readonly PcTelemetryDto Busy = new(Now, 57, (long)(21.3 * (1L << 30)), 64L << 30, 88, 74, (long)(11.2 * (1L << 30)), 24L << 30, "bench", null);

    private static ScreenAssignmentDto Example() =>
        new("screen:turzx:a", Overlay: "dashboard", Fps: 30, AccentColor: "#FAD61D", Fit: "contain", OverlayLayout: ExampleRegion);

    private sealed record BenchRow(string Name, string Mode, int Bytes, string InlineSha256, string PreparedSha256, string InlineNote,
        string? PreparedNote, double InlineMs, double PreparedEncodeMs, double RequestToCompleteMs, long PreparedAllocatedBytes, long LayerBytes,
        bool LayerUnchanged);

    private sealed record Inline(PixelFrame Layer, string LayerSha256, byte[] Wic, double WicMs, byte[] None, string NoneNote, double NoneMs);

    private static async Task<BenchRow[]> Measure(TurzxOverlayPreparer preparer, string name, ScreenAssignmentDto assignment,
        PcTelemetryDto? telemetry, int internalRotation, long generation)
    {
        var inline = await Sta.RunAsync(() => InlineReference(assignment, telemetry, internalRotation));
        var rows = new BenchRow[2];
        for (var i = 0; i < 2; i++)
        {
            var simple = i == 1;
            preparer.Request(new(generation, inline.Layer, assignment, internalRotation, simple)).Should().Be(OverlayRequestOutcome.Queued);
            await Wait.UntilAsync(() => preparer.Snapshot().ResultHeld, "the prepared overlay", 30_000);
            preparer.TryTake(generation, out var prepared).Should().Be(OverlayTakeOutcome.Ready);
            var bytes = prepared!.Bytes!;
            rows[i] = new(name, simple ? "none" : "wic", bytes.Length, Sha256(simple ? inline.None : inline.Wic), Sha256(bytes),
                simple ? inline.NoneNote : "RGBA8 WIC PNG", prepared.Note, simple ? inline.NoneMs : inline.WicMs, prepared.EncodeMs,
                Stopwatch.GetElapsedTime(prepared.RequestedAt, prepared.CompletedAt).TotalMilliseconds, prepared.AllocatedBytes,
                inline.Layer.ByteCount, Sha256(inline.Layer.Pixels) == inline.LayerSha256);
        }

        return rows;
    }

    /// <summary>The worker's inline operations restated exactly: OverlayCanvas, then EncodeOverlayPng's WIC and NONE rules.</summary>
    private static Inline InlineReference(ScreenAssignmentDto assignment, PcTelemetryDto? telemetry, int internalRotation)
    {
        var layer = ScreenWorker.RenderVideoOverlay(new OverlayRenderer(), ScreenFamilyInfo.Turzx, assignment, telemetry, Now)!;
        var layerSha = Sha256(layer.Pixels);
        var start = Stopwatch.GetTimestamp();
        var canvas = PixelOps.Rotate(layer, assignment.Rotation);
        PixelOps.ApplyBrightness(canvas, assignment.Brightness);
        canvas = PixelOps.Rotate(canvas, internalRotation);
        var wic = WpfCodec.EncodePng(canvas);
        if (wic.Length > ScreenProtocol.TurzxMaxPngBytes) throw new InvalidDataException("the inline WIC overlay exceeds the budget"); // none of the 36 does
        var wicMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var repackStart = Stopwatch.GetTimestamp();
        var repacked = LosslessOverlayPng.Repack(wic);
        var noneMs = wicMs + Stopwatch.GetElapsedTime(repackStart).TotalMilliseconds;
        var (none, note) = repacked.Candidate is { } candidate ? (candidate, "RGBA8 filter NONE PNG; exact lossless repack")
            : repacked.OriginalWithinBudget ? (wic, "RGBA8 WIC PNG; filter NONE candidate over budget, explicit fallback")
            : throw new InvalidDataException("neither inline overlay fits the budget");
        return new(layer, layerSha, wic, wicMs, none, note, noneMs);
    }

    private static List<string> Summary(List<BenchRow> rows, OverlayPreparerSnapshot snapshot)
    {
        var lines = new List<string>
        {
            FormattableString.Invariant($"overlays={rows.Count / 2} encodes={rows.Count} identical_sha256={rows.Count(r => r.PreparedSha256 == r.InlineSha256)}/{rows.Count} (WIC and NONE; desktop only)"),
        };
        foreach (var mode in new[] { "wic", "none" })
        {
            var m = rows.Where(r => r.Mode == mode).ToList();
            lines.Add(FormattableString.Invariant(
                $"{mode}: bytes median={Median(m.Select(r => (double)r.Bytes))} max={m.Max(r => r.Bytes)}; inline_ms median={Median(m.Select(r => r.InlineMs)):F2} max={m.Max(r => r.InlineMs):F2}; prepared_encode_ms median={Median(m.Select(r => r.PreparedEncodeMs)):F2} max={m.Max(r => r.PreparedEncodeMs):F2}; request_to_complete_ms median={Median(m.Select(r => r.RequestToCompleteMs)):F2} max={m.Max(r => r.RequestToCompleteMs):F2}; managed_allocated_per_encode median={Median(m.Select(r => (double)r.PreparedAllocatedBytes))} max={m.Max(r => r.PreparedAllocatedBytes)}"));
        }

        lines.Add(FormattableString.Invariant(
            $"layers referenced by the preparer's fields at most: 2 distinct (in flight; pending or dedupe key), {rows[0].LayerBytes} B each, referenced not copied; plus one held result (max {rows.Max(r => r.Bytes)} B). Not total memory. Max managed allocation per encode={snapshot.MaxAllocatedBytes} B (native WIC memory not included); max encode={snapshot.MaxEncodeMs:F2} ms"));
        using var process = Process.GetCurrentProcess();
        lines.Add(FormattableString.Invariant($"process_peak_working_set_bytes={process.PeakWorkingSet64} (whole test host process)"));
        return lines;
    }

    private static void WriteBenchmark(string directory, List<BenchRow> rows, List<string> summary)
    {
        Directory.CreateDirectory(directory);
        var c = CultureInfo.InvariantCulture;
        var table = new List<string>
        {
            "name,mode,bytes,inline_sha256,prepared_sha256,identical,inline_ms,prepared_encode_ms,request_to_complete_ms,prepared_managed_allocated_bytes,layer_bytes,layer_unchanged",
        };
        table.AddRange(rows.Select(r => string.Join(',', r.Name, r.Mode, r.Bytes.ToString(c), r.InlineSha256, r.PreparedSha256,
            (r.InlineSha256 == r.PreparedSha256).ToString(c), r.InlineMs.ToString("F3", c), r.PreparedEncodeMs.ToString("F3", c),
            r.RequestToCompleteMs.ToString("F3", c), r.PreparedAllocatedBytes.ToString(c), r.LayerBytes.ToString(c), r.LayerUnchanged.ToString(c))));
        File.WriteAllLines(Path.Combine(directory, "s1a-overlays.csv"), table);
        File.WriteAllLines(Path.Combine(directory, "s1a-summary.txt"), summary);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    // ---- lifecycle helpers ----

    private static TurzxOverlayPreparer Preparer(FakeEncoder encoder)
    {
        var preparer = new TurzxOverlayPreparer(TimeProvider.System, TurzxOverlayPreparer.DefaultMaxAge, encoder.Encode);
        preparer.Start().Should().Be(OverlayStartOutcome.Started);
        return preparer;
    }

    private static OverlayPreparationRequest Request(PixelFrame layer, long generation = 1, ScreenAssignmentDto? assignment = null,
        int internalRotation = 90, bool simplePng = false) => new(generation, layer, assignment ?? Assignment, internalRotation, simplePng);

    private static PixelFrame Layer(byte value) => PixelFrame.Solid(8, 4, value, value, value); // opaque: Pixels[0] == value

    private static Task Held(TurzxOverlayPreparer preparer) => Wait.UntilAsync(() => preparer.Snapshot().ResultHeld, "a held result");

    /// <summary>Requests a fresh layer that only the preparer references afterwards (no local of the caller keeps it).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RequestUnreferencedLayer(TurzxOverlayPreparer preparer, long generation)
    {
        var layer = PixelFrame.Solid(8, 4, 1, 2, 3);
        preparer.Request(Request(layer, generation)).Should().Be(OverlayRequestOutcome.Queued);
        return new WeakReference(layer);
    }

    private static (bool InFlight, bool Pending, int Layers, long Bytes) Retained(TurzxOverlayPreparer preparer)
    {
        var s = preparer.Snapshot();
        return (s.InFlight, s.Pending, s.RetainedLayers, s.RetainedLayerBytes);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// A lifecycle fake (never production codec rules): optionally blocks each call until released, records the calling thread and
    /// request, fails or returns nothing for chosen layers, and otherwise returns a fresh 12-byte array whose last byte is the layer's
    /// first pixel value.
    /// </summary>
    private sealed class FakeEncoder : IDisposable
    {
        private readonly SemaphoreSlim _release = new(0);
        private volatile bool _gated;
        private int _calls;

        public bool Gated { get => _gated; set => _gated = value; }

        public PixelFrame? Fail { get; init; }

        public PixelFrame? Empty { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public ConcurrentQueue<OverlayPreparationRequest> Seen { get; } = new();

        public ConcurrentQueue<(int Id, ApartmentState Apartment, bool Background, string? Name)> Threads { get; } = new();

        public (byte[] Bytes, string Note) Encode(OverlayPreparationRequest request)
        {
            Interlocked.Increment(ref _calls);
            Seen.Enqueue(request);
            var thread = Thread.CurrentThread;
            Threads.Enqueue((thread.ManagedThreadId, thread.GetApartmentState(), thread.IsBackground, thread.Name));
            if (_gated && !_release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("the test never released the fake encoder");
            if (ReferenceEquals(request.Layer, Fail)) throw new InvalidOperationException("fake WIC failure");
            if (ReferenceEquals(request.Layer, Empty)) return ([], "fake");
            byte[] bytes = [(byte)request.Generation, (byte)request.Assignment.Rotation, (byte)(request.Assignment.Brightness * 100),
                (byte)(request.InternalRotation / 10), request.SimplePng ? (byte)1 : (byte)0, 0, 0, 0, 0, 0, 0, request.Layer.Pixels[0]];
            return (bytes, "fake");
        }

        /// <summary>Waits until at least <paramref name="count"/> encodes have started (entered this fake).</summary>
        public bool WaitCalls(int count) => SpinWait.SpinUntil(() => Volatile.Read(ref _calls) >= count, TimeSpan.FromSeconds(10));

        public void Release(int count = 1) => _release.Release(count);

        public void Dispose()
        {
            _gated = false;
            _release.Release(64);
            _release.Dispose();
        }
    }
}
