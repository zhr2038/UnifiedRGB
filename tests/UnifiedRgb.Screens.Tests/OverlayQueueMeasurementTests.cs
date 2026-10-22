using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using static UnifiedRgb.Screens.Tests.PreparedOverlayKit;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The optional D1 measurement on the worker (MeasureQueueAfterOverlay): exactly one strict 122 right after each worker-issued PNG
/// (content or explicit transparent clear, inline or prepared), never after Begin's own PNG, a data picture or a picture that sent no
/// PNG; DeviceBufferStatus stays the data path's depth; the facts are the REPORTED block counts and HOST times of the active trial
/// only. A bad status reply faults the session: nothing is resent or written to that channel again. Fake channels only.
/// </summary>
public sealed class OverlayQueueMeasurementTests
{
    /// <summary>121 reports 1 block, 122 reports 2: the two sources stay distinguishable.</summary>
    private static byte[]? Depths(byte command, uint stamp, int writes) => command switch
    {
        TurzxVideoProtocol.DataCommand => NativeWorkerHarness.Ack(command, stamp, 1),
        122 => NativeWorkerHarness.Ack(command, stamp, 2),
        _ => null,
    };

    private static NativeVideoTrial Measured(string id) => NativeWorkerHarness.NewTrial(id, t => t with { MeasureQueueAfterOverlay = true });

    [MediaFoundationFact]
    public async Task Inline_measurement_queries_once_after_each_worker_png_and_reports_blocks_and_host_time()
    {
        using var h = await StartAsync(Measured("d1"), null, reply: Depths);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        Commands(h.Writes).Should().Equal(111, 112, 41, 102, 15, 121, 102, 122);
        var q = Facts(h).LastOverlayQueue!;
        (q.Queries, q.AccessUnitsAtWrite, q.ReportedBlocksBefore, q.ReportedBlocksAfter, q.Clear, q.Prepared)
            .Should().Be((1L, 1L, 1, 2, false, false), "before: the session's status after this picture's data; after: the query");
        q.OverlayBytes.Should().Be(WorkerPngs(h.Writes)[0].Length);
        q.HostTimestampMs.Should().Be(0, "host time since this session began (frozen clock)");
        new[] { q.OverlayWriteMs, q.OverlayReadMs, q.QueryMs }.Should().OnlyContain(v => v >= 0);
        Facts(h).DeviceBufferStatus.Should().Be(1, "the query never assigns the data path's depth");
        h.Worker.OverlayPreparerForTest.Should().BeNull("measurement alone never creates the preparer");

        await PictureAsync(h);
        h.Count(122).Should().Be(1, "no query after a picture that sent no PNG");
        h.SetTelemetry(20);
        await PictureAsync(h);
        Commands(h.Writes)[^3..].Should().Equal(121, 102, 122);
        q = Facts(h).LastOverlayQueue!;
        (q.Queries, q.AccessUnitsAtWrite, q.Clear, q.HostTimestampMs).Should().Be((2L, 3L, false, 200.0));

        await h.PostAsync(h.Assignment with { Overlay = "none" });
        Commands(h.Writes)[^3..].Should().Equal(121, 102, 122);
        q = Facts(h).LastOverlayQueue!;
        (q.Queries, q.AccessUnitsAtWrite, q.Clear, q.Prepared, q.OverlayBytes).Should().Be((3L, 4L, true, false, BeginPng(h.Writes).Length),
            "the explicit transparent clear is a worker-issued PNG");
        await PictureAsync(h);
        h.Count(122).Should().Be(3);
        Facts(h).DeviceBufferStatus.Should().Be(1);

        await SwitchTrialAsync(h, NativeWorkerHarness.NewTrial("plain"));
        Facts(h).LastOverlayQueue.Should().BeNull("another trial's measurement is not shown");
        await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
        Commands(h.Writes)[^2..].Should().Equal(121, 102);
        h.Count(122).Should().Be(3, "a default trial never queries");
        Facts(h).LastOverlayQueue.Should().BeNull();
        Commands(h.Writes).Count(c => c == 41).Should().Be(1, "one native session throughout");
    }

    [MediaFoundationFact]
    public async Task Prepared_measurement_queries_after_prepared_pngs_and_the_prepared_clear()
    {
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("pd1", measure: true), encoder, reply: Depths);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        Commands(h.Writes).Should().Equal(111, 112, 41, 102, 15, 121);
        Facts(h).LastOverlayQueue.Should().BeNull("no worker PNG yet");
        await IdleAsync(h);
        await PictureAsync(h);
        Commands(h.Writes)[^3..].Should().Equal(121, 102, 122);
        var q = Facts(h).LastOverlayQueue!;
        (q.Queries, q.AccessUnitsAtWrite, q.ReportedBlocksBefore, q.ReportedBlocksAfter, q.Clear, q.Prepared)
            .Should().Be((1L, 2L, 1, 2, false, true));
        q.OverlayBytes.Should().Be(WorkerPngs(h.Writes)[0].Length);
        q.HostTimestampMs.Should().Be(100);

        await h.PostAsync(h.Assignment with { Overlay = "none" });
        Commands(h.Writes)[^3..].Should().Equal(121, 102, 122);
        q = Facts(h).LastOverlayQueue!;
        (q.Queries, q.Clear, q.Prepared).Should().Be((2L, true, true));
        h.Count(122).Should().Be(2);
        Facts(h).DeviceBufferStatus.Should().Be(1);
        PreparedFacts(h).ClearSends.Should().Be(1);
    }

    public static TheoryData<string> BadStatuses => ["short", "stamp", "command"];

    /// <summary>A short or mismatched 122 reply: the session faults, the channel is closed with no resend or cleanup write on it.</summary>
    [MediaFoundationTheory]
    [MemberData(nameof(BadStatuses))]
    public async Task A_bad_status_reply_closes_the_channel_without_any_further_write(string kind)
    {
        using var h = await StartAsync(Measured("bad-" + kind), null, reply: (command, stamp, _) => command != 122 ? null : kind switch
        {
            "short" => NativeWorkerHarness.Ack(command, stamp)[..8],
            "stamp" => NativeWorkerHarness.Ack(command, stamp + 1),
            _ => NativeWorkerHarness.Ack(TurzxVideoProtocol.DataCommand, stamp),
        });
        await Wait.UntilAsync(() => h.Opener.Channels.FirstOrDefault() is { Disposed: true }, "the worker closes the faulted channel");
        await Task.Delay(250);
        var first = h.Opener.Channels.First();
        Commands(first.Writes).Should().Equal([111, 112, 41, 102, 15, 121, 102, 122], "no resend, stop controls or later command on the faulted channel");
        h.Aborts.Should().BeGreaterThan(0, "the failed transfer ends the trial through the existing path");
    }
}
