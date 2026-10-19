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
    [InlineData("lianli:rf:06-A8-F0-72-AB-3C", false)]
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
}
