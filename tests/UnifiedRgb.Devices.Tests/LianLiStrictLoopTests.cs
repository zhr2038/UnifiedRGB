using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// OPUS-GENERIC-SCENE-PACKAGES on the Lian Li adapter (fake USB/RF only): the budget probe runs the upload's own codec path
/// without any I/O, and a strict loop is uploaded exactly or not at all (never decimated, never a static fallback).
/// </summary>
public class LianLiStrictLoopTests
{
    private static LoopFrames Loop(bool strict, int leds = 160, double interval = 96) =>
        new(200, leds, interval, 7, TinyuzEncoder.SampleAnimation(200 * leds * 3)) { Strict = strict };

    [Fact]
    public async Task Strict_loops_are_measured_through_the_codec_without_IO_and_never_decimated()
    {
        var big = true;
        var enc = new SizedEncoder(n => big && n >= 96_000 ? 20_000 : n / 10);
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        options.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = "SLv3", Leds = 160, Channel = 8, Rx = 1 });
        var now = Rf.Now;
        await using var a = new LianLiAdapter(options, usb, _ => wire, usb, NullLogger.Instance, enc, _ => { }, () => now, startKeepAliveTimer: false);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        var sent = wire.SentFrames.Count;

        var refused = a.MeasureLoop(d.Id, LianLiAdapter.ChainZone, Loop(strict: true));
        (refused.Fits, refused.CompressedBytes, refused.MaxBytes).Should().Be((false, 20_000, 12_288));
        refused.Problem.Should().Contain("never decimated");
        a.MeasureLoop(d.Id, new ZoneId(5), Loop(strict: true)).Fits.Should().BeFalse("unknown zone");
        a.MeasureLoop(d.Id, LianLiAdapter.ChainZone, Loop(strict: true, leds: 159)).Fits.Should().BeFalse("LED count must be exact");
        a.MeasureLoop(d.Id, LianLiAdapter.ChainZone, Loop(strict: true, interval: 96.001)).Fits.Should().BeFalse("interval must fit the header exactly");
        a.MeasureLoop(new ControllerId("lianli:rf:00-00-00-00-00-00"), LianLiAdapter.ChainZone, Loop(strict: true)).Fits.Should().BeFalse();
        wire.SentFrames.Count.Should().Be(sent, "measuring performs no I/O");

        var strict = () => a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(strict: true), CancellationToken.None);
        await strict.Should().ThrowAsync<InvalidDataException>().WithMessage("*not decimated*");
        wire.SentFrames.Count.Should().Be(sent, "a refused strict loop sends nothing (the device keeps its previous loop)");

        await a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(strict: false), CancellationToken.None);
        wire.SentFrames.Count.Should().BeGreaterThan(sent, "historical (non-strict) content keeps its decimation fallback unchanged");

        big = false;
        var fits = a.MeasureLoop(d.Id, LianLiAdapter.ChainZone, Loop(strict: true));
        (fits.Fits, fits.CompressedBytes).Should().Be((true, 9_600));
        var before = wire.SentFrames.Count;
        await a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(strict: true), CancellationToken.None);
        wire.SentFrames.Count.Should().BeGreaterThan(before);
        var last = a.LastUpload(d.Id)!;
        (last.Frames, last.IntervalMs).Should().Be((200, 96.0), "uploaded exactly: all frames at 96 raw ticks");
    }
}
