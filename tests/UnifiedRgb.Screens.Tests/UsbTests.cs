using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

public sealed class UsbTests
{
    [Fact]
    public void The_five_discovered_screens_parse_to_stable_ids()
    {
        Devices.Tl1.Id.Should().Be("screen:tl-lcd:a4396dc0631e0a66");
        Devices.Tl2.Id.Should().Be("screen:tl-lcd:da712ac3931e0b66");
        Devices.Tl3.Id.Should().Be("screen:tl-lcd:e1f26a43041e1366");
        Devices.H2.Id.Should().Be("screen:h2:1d3343b614a38505");
        Devices.Turzx.Id.Should().Be("screen:turzx:6f2c527df4cac503");
        Devices.Turzx.Family.Should().BeSameAs(ScreenFamilyInfo.Turzx);
        ScreenDeviceInfo.FromPath(Devices.PathOf("0006", "a4396dc0631e0a66").ToUpperInvariant())!.Id.Should().Be(Devices.Tl1.Id);
    }

    [Theory]
    [InlineData(@"\\?\usb#vid_1cbf&pid_0006#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")] // other vendor
    [InlineData(@"\\?\usb#vid_1cbe&pid_a065#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")] // A065 branch: never borrowed
    [InlineData(@"\\?\usb#vid_1cbe&pid_0005#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"\\?\usb#vid_1cbe&pid_0007#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"\\?\usb#vid_0416&pid_0088#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"\\?\usb#vid_1cbe&pid_0006&mi_00#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")] // composite interface
    [InlineData(@"\\?\hid#vid_1cbe&pid_0006#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"\\?\usb#vid_1cbe&pid_0006#a4396dc0631e0a66")]
    [InlineData(@"\\?\usb#vid_1cbe&pid_0006#a43#96#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"\\?\usb#vid_1cbe&pid_0006#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"usb#vid_1cbe&pid_0006#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}")]
    [InlineData(@"\\?\usb#vid_1cbe&pid_0006#a4396dc0631e0a66#{88bae032-5a81-49f0-bc3d-a4ff138216d6}\x")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_an_exact_whole_device_path_is_rejected(string? path)
    {
        ScreenDeviceInfo.FromPath(path).Should().BeNull();
    }

    [Fact]
    public void Overlong_paths_and_instances_are_rejected()
    {
        ScreenDeviceInfo.FromPath(Devices.PathOf("0006", new string('a', 65))).Should().BeNull();
        ScreenDeviceInfo.FromPath(new string('\\', 600)).Should().BeNull();
    }

    [Fact]
    public void Vid_pid_mapping_is_exact()
    {
        ScreenFamilyInfo.FromUsb(0x1CBE, 0x0006).Should().BeSameAs(ScreenFamilyInfo.TlLcd);
        ScreenFamilyInfo.FromUsb(0x1CBE, 0xA021).Should().BeSameAs(ScreenFamilyInfo.H2);
        ScreenFamilyInfo.FromUsb(0x1CBE, 0x0088).Should().BeSameAs(ScreenFamilyInfo.Turzx);
        ScreenFamilyInfo.FromUsb(0x1CBE, 0xA065).Should().BeNull();
        ScreenFamilyInfo.FromUsb(0x1CBF, 0x0006).Should().BeNull();
        ScreenFamilyInfo.TlLcd.ExpectedOutEndpoint.Should().Be(0x01);
        ScreenFamilyInfo.TlLcd.ExpectedInEndpoint.Should().Be(0x81);
        ScreenFamilyInfo.H2.ExpectedOutEndpoint.Should().Be(0x01);
        ScreenFamilyInfo.H2.ExpectedInEndpoint.Should().Be(0x81);
    }

    [Theory]
    [InlineData("screen:tl-lcd:abc", true)]
    [InlineData("screen:h2:1d3343b614a38505", true)]
    [InlineData("screen:turzx:any", true)]
    [InlineData("screen:TURZX:any", false)]
    [InlineData("screen:turzx:ANY", false)]
    [InlineData("screen:a065:x", false)]
    [InlineData("screen:turzx:", false)]
    [InlineData("screen:turzx:a#b", false)]
    [InlineData("lianli:rf:02-00-00-00-00-01", false)]
    [InlineData(null, false)]
    public void Screen_id_grammar(string? id, bool ok) => ScreenIds.TryParse(id, out _, out _).Should().Be(ok);

    [Theory]
    [InlineData(121, ScreenTransferError.Timeout)]
    [InlineData(995, ScreenTransferError.Aborted)]
    [InlineData(5, ScreenTransferError.AccessDenied)]
    [InlineData(32, ScreenTransferError.AccessDenied)]
    [InlineData(2, ScreenTransferError.Disconnected)]
    [InlineData(1167, ScreenTransferError.Disconnected)]
    [InlineData(433, ScreenTransferError.Disconnected)]
    [InlineData(87, ScreenTransferError.Failed)]
    public void Win32_errors_are_classified(int error, ScreenTransferError kind) => ScreenTransferException.Classify(error).Should().Be(kind);

    [Fact]
    public void Backoff_doubles_up_to_the_cap_and_resets()
    {
        var b = new Backoff(1000, 30000);
        new[] { b.Fail(), b.Fail(), b.Fail(), b.Fail(), b.Fail(), b.Fail(), b.Fail() }.Should().Equal(1000, 2000, 4000, 8000, 16000, 30000, 30000);
        for (var i = 0; i < 100; i++)
        {
            b.Fail().Should().Be(30000);
        }

        b.Reset();
        b.Fail().Should().Be(1000);
    }

    [Fact]
    public async Task Latest_wins_mailbox_keeps_only_the_newest_message()
    {
        using var box = new LatestWins<string>();
        box.TryTake().Should().BeNull();
        box.Wait(0).Should().BeFalse();
        box.Post("a");
        box.Post("b");
        box.Post("c");
        box.Wait(0).Should().BeTrue();
        box.TryTake().Should().Be("c");
        box.TryTake().Should().BeNull();

        var waiter = Task.Run(() => box.Wait(5000));
        await Task.Delay(50);
        box.Post("d");
        (await waiter).Should().BeTrue();
        box.TryTake().Should().Be("d");
    }

    [Fact]
    public void Latest_wins_coalesces_concurrent_posts_and_can_take_before_waiting()
    {
        using var box = new LatestWins<string>();
        Parallel.For(0, 1000, i => box.Post(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        box.Post("latest");
        box.TryTake().Should().Be("latest");
        box.Post("after take");
        box.Wait(0).Should().BeTrue();
        box.TryTake().Should().Be("after take");
        box.Wait(0).Should().BeFalse("overwrites share one pending notification");
        box.Post("next");
        box.Wait(0).Should().BeTrue();
        box.TryTake().Should().Be("next");
    }

    [Fact]
    public void TlLcd_open_drains_stale_IN_packets_and_zlps_without_discarding_subsequent_command_ACK()
    {
        var ops = new FakeWinUsbOps();
        ops.Reads.Enqueue((new byte[512], true)); // stale 512-byte packet from previous owner
        ops.Reads.Enqueue((Array.Empty<byte>(), true)); // stale ZLP
        ops.Reads.Enqueue((Array.Empty<byte>(), false)); // clean pipe timeout ends open drain

        using var channel = WinUsbScreenChannel.OpenForTest(ScreenFamily.TlLcd, 0x01, 0x81, ops);

        ops.InTimeouts.Should().Equal(10u);
        ops.ReadCount.Should().Be(3, "open drain consumed stale packet + ZLP + 1 timeout");

        // Normal frame Read() must keep _drainReplies = false and perform only ONE read so a command's expected ACK is never drained.
        var expectedAck = new byte[] { 101, 200, 1, 2, 3, 4 };
        ops.Reads.Enqueue((expectedAck, true));
        ops.Reads.Enqueue((new byte[] { 99, 200, 0, 0, 0, 0 }, true)); // following packet must NOT be flushed by Read()

        var buffer = new byte[512];
        channel.Read(buffer, 50).Should().Be(expectedAck.Length);
        buffer.AsSpan(0, expectedAck.Length).ToArray().Should().Equal(expectedAck);
        ops.ReadCount.Should().Be(4, "TlLcd Read() executes exactly one ReadPipe without draining");
        ops.InTimeouts.Should().Equal(10u, 50u);
        ops.Reads.Should().ContainSingle("next packet remained untouched on the IN pipe");
    }

    [Fact]
    public void TlLcd_open_drain_ignores_timeout_on_idle_pipe_and_closes_babbling_pipe()
    {
        var idle = new FakeWinUsbOps();
        idle.Reads.Enqueue((Array.Empty<byte>(), false));
        using (var channel = WinUsbScreenChannel.OpenForTest(ScreenFamily.TlLcd, 0x01, 0x81, idle))
        {
            idle.ReadCount.Should().Be(1);
            idle.Closed.Should().BeFalse();
        }

        var babbling = new FakeWinUsbOps();
        for (var i = 0; i < ScreenReplyFlow.OpenDrainLimit + 2; i++)
        {
            babbling.Reads.Enqueue((new byte[64], true));
        }

        Action open = () => WinUsbScreenChannel.OpenForTest(ScreenFamily.TlLcd, 0x01, 0x81, babbling);
        open.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ReplyBacklog);
        babbling.ReadCount.Should().Be(ScreenReplyFlow.OpenDrainLimit);
        babbling.Closed.Should().BeTrue();
    }

    [Fact]
    public void WinUsbScreenChannel_TryResetPipes_aborts_and_resets_both_endpoints_best_effort()
    {
        var ops = new FakeWinUsbOps { ResetPipeResult = pipe => pipe == 0x81 };
        ops.Reads.Enqueue((Array.Empty<byte>(), false));
        var channel = WinUsbScreenChannel.OpenForTest(ScreenFamily.TlLcd, 0x01, 0x81, ops);

        channel.TryResetPipes().Should().BeFalse("OUT reset failed, yet both IN and OUT were attempted best-effort");
        ops.PipeActions.Should().Equal(
            ("abort", (byte)0x81),
            ("reset", (byte)0x81),
            ("abort", (byte)0x01),
            ("reset", (byte)0x01));

        ops.ResetPipeResult = _ => true;
        ops.PipeActions.Clear();
        channel.TryResetPipes().Should().BeTrue();
        ops.PipeActions.Should().Equal(
            ("abort", (byte)0x81),
            ("reset", (byte)0x81),
            ("abort", (byte)0x01),
            ("reset", (byte)0x01));

        channel.Dispose();
        ops.PipeActions.Clear();
        channel.TryResetPipes().Should().BeFalse("disposed channel never calls WinUSB reset");
        ops.PipeActions.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenWorker_resets_TlLcd_pipes_before_closing_on_usb_timeout_boundary()
    {
        await using var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.BackoffInitialMs = 30;
            o.BackoffMaxMs = 30;
        }, Devices.Tl1);
        var events = new List<string>();
        var opened = 0;
        var resetOpener = new ResetTrackingOpener(c =>
        {
            if (Interlocked.Increment(ref opened) == 1)
            {
                c.FailWrite = _ => new ScreenTransferException(ScreenTransferError.Timeout, "WinUsb_WritePipe failed (121)", 121);
                c.OnReset = () => events.Add("reset");
                c.BeforeDispose = () => events.Add("dispose");
            }
        });

        await using var manager = new ScreenManager(
            new ScreensOptions { Enabled = true, NvmlTelemetry = false, BackoffInitialMs = 30, BackoffMaxMs = 30, TlLcd = new ScreenFamilyOptions { Output = true } },
            rig.DataDirectory,
            new ScreenPlatform(new FakeEnumerator(Devices.Tl1), resetOpener, new FakeContention(), new FakeOs(), null, TimeProvider.System));
        await manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Tl1.Id, Fps: 15)));
        manager.Start();

        await Wait.UntilAsync(async () => (await manager.ListScreensAsync()).Single(s => s.Id == Devices.Tl1.Id).State == ScreenStates.Running,
            "TL-LCD recovers to Running after pipe reset and reopen");
        events.Should().Equal("reset", "dispose");
        resetOpener.Opens.Should().BeGreaterThanOrEqualTo(2);
    }

    private sealed class ResetTrackingChannel : IScreenUsbChannel, IScreenUsbPipeReset
    {
        public Func<int, Exception?>? FailWrite { get; set; }
        public Action? OnReset { get; set; }
        public Action? BeforeDispose { get; set; }
        private int _writes;
        public byte OutEndpoint => 0x01;
        public byte InEndpoint => 0x81;
        public void Write(ReadOnlySpan<byte> data, int timeoutMs)
        {
            if (FailWrite?.Invoke(Interlocked.Increment(ref _writes)) is { } ex) throw ex;
        }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public bool TryResetPipes() { OnReset?.Invoke(); return true; }
        public void Abort() { }
        public void Dispose() => BeforeDispose?.Invoke();
    }

    private sealed class ResetTrackingOpener(Action<ResetTrackingChannel> configure) : IScreenUsbOpener
    {
        public int Opens;
        public IScreenUsbChannel Open(ScreenDeviceInfo device)
        {
            Interlocked.Increment(ref Opens);
            var c = new ResetTrackingChannel();
            configure(c);
            return c;
        }
    }

    private sealed class FakeWinUsbOps : IWinUsbChannelOps
    {
        public Queue<(byte[] Data, bool Success)> Reads { get; } = new();
        public List<uint> InTimeouts { get; } = [];
        public List<(string Action, byte Pipe)> PipeActions { get; } = [];
        public Func<byte, bool> ResetPipeResult { get; set; } = _ => true;
        public int ReadCount { get; private set; }
        public bool Closed { get; private set; }

        public bool SetPipeTimeout(IntPtr usb, byte pipeId, uint timeoutMs, out int err)
        {
            if (pipeId == 0x81) InTimeouts.Add(timeoutMs);
            err = 0;
            return true;
        }

        public bool WritePipe(IntPtr usb, byte pipeId, ReadOnlySpan<byte> data, out uint transferred, out int err)
        {
            transferred = (uint)data.Length;
            err = 0;
            return true;
        }

        public bool ReadPipe(IntPtr usb, byte pipeId, Span<byte> buffer, out uint transferred, out int err)
        {
            ReadCount++;
            if (Reads.TryDequeue(out var next) && next.Success)
            {
                next.Data.CopyTo(buffer);
                transferred = (uint)next.Data.Length;
                err = 0;
                return true;
            }

            transferred = 0;
            err = ScreenUsbNative.ErrorSemTimeout;
            return false;
        }

        public bool AbortPipe(IntPtr usb, byte pipeId) { PipeActions.Add(("abort", pipeId)); return true; }
        public bool ResetPipe(IntPtr usb, byte pipeId) { PipeActions.Add(("reset", pipeId)); return ResetPipeResult(pipeId); }
        public void Close(IntPtr usb, Microsoft.Win32.SafeHandles.SafeFileHandle? file) => Closed = true;
    }
}
