using System.Buffers.Binary;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// H2 layer takeover (design/OPUS-H2-LAYER-TAKEOVER-20261018.md): each newly opened H2 channel first receives ONE fully
/// transparent 480x480 PNG (command 102) that clears the independent PNG layer, then its normal JPEGs (101). Checked on the
/// decrypted packets the fake USB received.
/// </summary>
public sealed class H2LayerTakeoverTests
{
    private static readonly string H2 = Devices.H2.Id;

    [Fact]
    public async Task A_new_h2_channel_gets_one_transparent_png_then_normal_jpegs()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, BackgroundColor: "#FF0000", Overlay: "compact", Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && rig.Opener.Last.Writes.Count >= 4, "a clear and three frames");
        await PauseAsync(rig, H2);

        var writes = rig.Opener.Last.Writes.ToArray();
        var clear = Unpack(ScreenFamily.H2, writes[0]);
        clear.Command.Should().Be(ScreenProtocol.PushPngCommand);
        writes[0].Should().HaveCount(512 + clear.Image.Length, "H2 packets are the header block plus the image, unpadded");
        Ihdr(clear.Image).Should().Be((480, 480, 8, 6), "a 480x480 8-bit RGBA PNG");
        var pixels = await Sta.RunAsync(() => WpfCodec.DecodePng(clear.Image, 480 * 480));
        (pixels.Width, pixels.Height).Should().Be((480, 480));
        pixels.Pixels.Where((_, i) => i % 4 == 3).Should().OnlyContain(a => a == 0, "every pixel is fully transparent");

        foreach (var packet in writes.Skip(1))
        {
            var normal = Unpack(ScreenFamily.H2, packet);
            normal.Command.Should().Be(ScreenProtocol.PushJpegCommand);
            ScreenProtocol.IsJpeg(normal.Image).Should().BeTrue();
        }

        writes.Select(w => Unpack(ScreenFamily.H2, w).Stamp).Should().BeInAscendingOrder("request stamps follow the send order");
        rig.Opener.Last.Reads.Should().Be(rig.Opener.Last.WriteAttempts, "one bounded response read follows every write, the clear too");
        var s = await rig.ScreenAsync(H2);
        s.FramesWritten.Should().Be(writes.Length - 1, "the clear is not a content frame");
        rig.Manager.LayerBootstrapsOf(H2).Should().Be(1);
    }

    [Theory]
    [InlineData("tl")]
    [InlineData("turzx")]
    public async Task Tl_and_turzx_never_get_the_layer_clear(string which)
    {
        var device = which == "tl" ? Devices.Tl1 : Devices.Turzx;
        var family = device.Family.Family;
        await using var rig = new Rig(Rig.AllOutput, device);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(device.Id, BackgroundColor: "#FF0000", Overlay: "compact", Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && rig.Opener.Last.Writes.Count >= 3, "three frames");
        await PauseAsync(rig, device.Id);

        var writes = rig.Opener.Last.Writes.ToArray();
        foreach (var packet in writes)
        {
            var p = Unpack(family, packet);
            if (family == ScreenFamily.TlLcd)
            {
                p.Command.Should().Be(ScreenProtocol.PushJpegCommand);
            }
            else
            {
                p.Command.Should().Be(ScreenProtocol.PushPngCommand, "TURZX content itself is PNG");
                Ihdr(p.Image).Should().Be((480, 1920, 8, 6), "every TURZX PNG is the rotated content frame, never a 480x480 clear");
            }
        }

        (await rig.ScreenAsync(device.Id)).FramesWritten.Should().Be(writes.Length, "every write is a content frame");
        rig.Manager.LayerBootstrapsOf(device.Id).Should().Be(0);
        ScreenWorker.ClearsPngLayerOnOpen(family).Should().BeFalse();
        ScreenWorker.ClearsPngLayerOnOpen(ScreenFamily.H2).Should().BeTrue();
    }

    [Fact]
    public async Task Assignment_changes_keep_the_channel_without_a_new_clear_and_a_reopen_clears_again()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, BackgroundColor: "#FF0000", Overlay: "compact", Fps: 15)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && rig.Opener.Last.Writes.Count >= 3, "running");
        var first = rig.Opener.Last;

        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, BackgroundColor: "#0000FF", Overlay: "compact", Fps: 15)));
        await Wait.UntilAsync(async () => await CentreIsBlueAsync(first.Writes.Last()), "the new assignment on the same channel");
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, BackgroundColor: "#00FF00", Overlay: "dashboard", Fps: 10)));
        var count = first.Writes.Count;
        await Wait.UntilAsync(() => first.Writes.Count >= count + 3, "more frames after a second change");

        rig.Opener.Opens.Should().Be(1, "an assignment change does not reopen");
        first.Writes.Count(w => Unpack(ScreenFamily.H2, w).Command == ScreenProtocol.PushPngCommand).Should().Be(1, "one clear per channel");
        rig.Manager.LayerBootstrapsOf(H2).Should().Be(1);

        await PauseAsync(rig, H2); // closes the channel
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, BackgroundColor: "#0000FF", Fps: 15)));
        await Wait.UntilAsync(() => rig.Opener.Opens == 2 && rig.Opener.Last.Writes.Count >= 2, "reopened");
        var second = rig.Opener.Last.Writes.ToArray();
        Unpack(ScreenFamily.H2, second[0]).Command.Should().Be(ScreenProtocol.PushPngCommand, "a reopened channel clears again");
        Unpack(ScreenFamily.H2, second[1]).Command.Should().Be(ScreenProtocol.PushJpegCommand);
        rig.Manager.LayerBootstrapsOf(H2).Should().Be(2);
    }

    [Theory]
    [InlineData(ScreenTransferError.ShortWrite, "Error")]
    [InlineData(ScreenTransferError.Timeout, "Error")]
    [InlineData(ScreenTransferError.Disconnected, "Disconnected")]
    public async Task A_failed_clear_backs_off_without_a_frame_or_running_and_the_reopen_clears_again(ScreenTransferError kind, string state)
    {
        await using var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.BackoffInitialMs = 300;
            o.BackoffMaxMs = 300;
        }, Devices.H2);
        var attempts = 0;
        rig.Opener.Configure = c => c.FailWrite = _ =>
            Interlocked.Increment(ref attempts) == 1 ? new ScreenTransferException(kind, kind + " on write") : null;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2, Fps: 15)));
        rig.Manager.Start();

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(H2)).LastError?.StartsWith("H2 layer clear:", StringComparison.Ordinal) == true,
            "the failed clear");
        var during = await rig.ScreenAsync(H2);
        during.State.Should().Be(state);
        during.LastError.Should().Contain(kind.ToString());
        during.FramesWritten.Should().Be(0);
        during.Dropped.Should().Be(0, "no content frame was dropped");
        var failed = rig.Opener.Channels.First();
        failed.WriteAttempts.Should().Be(1, "no JPEG follows a failed clear on that channel");
        failed.Writes.Should().BeEmpty();
        failed.Disposed.Should().BeTrue();
        rig.Manager.LayerBootstrapsOf(H2).Should().Be(0);

        await Wait.UntilAsync(async () => (await rig.ScreenAsync(H2)).State == ScreenStates.Running, "Running after the backoff");
        rig.Opener.Opens.Should().Be(2);
        var writes = rig.Opener.Last.Writes.ToArray();
        Unpack(ScreenFamily.H2, writes[0]).Command.Should().Be(ScreenProtocol.PushPngCommand);
        Unpack(ScreenFamily.H2, writes[1]).Command.Should().Be(ScreenProtocol.PushJpegCommand);
        (await rig.ScreenAsync(H2)).Dropped.Should().Be(0);
    }

    [Fact]
    public async Task Stop_while_the_clear_is_blocked_writes_nothing_more()
    {
        var rig = new Rig(Rig.AllOutput, Devices.H2);
        rig.Opener.Configure = c => c.BlockWrites = true;
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => rig.Opener.Opens == 1 && rig.Opener.Last.WriteAttempts == 1, "the blocked clear");
        var s = await rig.ScreenAsync(H2);
        s.State.Should().NotBe(ScreenStates.Running);
        s.FramesWritten.Should().Be(0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rig.DisposeAsync();

        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
        var channel = rig.Opener.Last;
        channel.Aborts.Should().BeGreaterThan(0);
        channel.Disposed.Should().BeTrue();
        channel.WriteAttempts.Should().Be(1, "no frame is attempted after the aborted clear");
        rig.Opener.Opens.Should().Be(1);
    }

    [Fact]
    public async Task Stop_between_the_clear_and_the_first_jpeg_sends_no_jpeg()
    {
        var rig = new Rig(Rig.AllOutput, Devices.H2);
        using var inRead = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.Opener.Configure = c => c.FailRead = () =>
        {
            if (!inRead.IsSet)
            {
                inRead.Set(); // the response read after the clear: hold it until the stop has begun
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return null;
        };
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(H2)));
        rig.Manager.Start();
        await Wait.UntilAsync(() => inRead.IsSet, "the clear's response read");
        var channel = rig.Opener.Last;
        channel.Writes.Should().ContainSingle();

        var dispose = Task.Run(async () => await rig.DisposeAsync());
        await Wait.UntilAsync(() => Volatile.Read(ref channel.Aborts) > 0, "the stop has begun"); // Dispose sets the stop flag first
        release.Set();
        await Wait.Outcome(dispose)();

        channel.WriteAttempts.Should().Be(1, "the stop arrived after the clear: no JPEG is sent");
        channel.Writes.Should().ContainSingle();
        Unpack(ScreenFamily.H2, channel.Writes.Single()).Command.Should().Be(ScreenProtocol.PushPngCommand, "only the clear was sent");
        channel.Disposed.Should().BeTrue();
    }

    private static async Task PauseAsync(Rig rig, string id)
    {
        await rig.Manager.StopScreensAsync();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(id)).State == ScreenStates.Stopped && rig.Opener.Last.Disposed, "Stopped");
    }

    private static async Task<bool> CentreIsBlueAsync(byte[] packet)
    {
        var p = Unpack(ScreenFamily.H2, packet);
        if (p.Command != ScreenProtocol.PushJpegCommand)
        {
            return false;
        }

        var f = await Sta.RunAsync(() => HeadlessProbe.Decode(p.Image));
        var c = f.At(f.Width / 2, f.Height / 2);
        return c.B > 200 && c.R < 60 && c.G < 60;
    }

    /// <summary>Decrypts the header and returns the command, request stamp and image bytes.</summary>
    private static (byte Command, uint Stamp, byte[] Image) Unpack(ScreenFamily family, byte[] packet)
    {
        var header = ScreenProtocol.DecryptHeader(family, packet.AsSpan(0, ScreenProtocol.HeaderBlockBytes));
        header[2].Should().Be(0x1A);
        header[3].Should().Be(0x6D);
        var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        return (header[0], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), packet.AsSpan(ScreenProtocol.HeaderBlockBytes, len).ToArray());
    }

    /// <summary>PNG IHDR: width, height, bit depth and colour type (6 = RGBA).</summary>
    private static (int Width, int Height, int Depth, int ColourType) Ihdr(byte[] png)
    {
        ScreenProtocol.IsPng(png).Should().BeTrue();
        System.Text.Encoding.ASCII.GetString(png, 12, 4).Should().Be("IHDR");
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)), png[24], png[25]);
    }
}
