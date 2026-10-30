using System.Reflection;
using System.Text;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

// The keyboard animation under R3 installed native-menu ownership, on the actual owner with the existing native-menu fakes: the
// enabled, admitted, bound animation is reported as suppressed and never reaches image67, the native status path or RGB.
public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task Installed_native_ownership_suppresses_an_enabled_animation_with_zero_legacy_images_and_keeps_its_preference()
    {
        await using var rig = new AnimatedOwnerRig(Policy(), mode: 7);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Bridge.Available
            && rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().Detail.Contains("原生光域菜单", StringComparison.Ordinal));
        await rig.Bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);

        var status = await rig.Owner.GetKeyboardDisplayAnimationAsync();
        status.State.Should().Be(KeyboardDisplayAnimationStates.SuppressedByNativeMenu);
        status.Detail.Should().Contain("不发送动画画面").And.Contain("绑定与开启偏好已保留");
        status.Settings.Enabled.Should().BeTrue();
        rig.Control.ImageBegins.Should().Be(0); rig.Control.RejectedImages.Should().Be(0);
        rig.Control.Colours.Should().Contain(new Rgb(11, 22, 33));

        // Native mode 9: the native path shows the STATUS artwork; the prepared animation never feeds it.
        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == 1 && rig.Control.Rebuilt.Length == 6656);
        rig.Control.Rebuilt.Should().Equal(rig.Owner.ArtworkPixels.ToArray());
        rig.Timeline.At(1500);
        await Task.Delay(400);
        await rig.Bridge.WriteAsync(new(44, 55, 66), default).WaitAsync(Patience);
        rig.Control.ImageBegins.Should().Be(1, "no legacy animation image is sent under installed ownership");
        rig.Control.RejectedImages.Should().Be(0);
        rig.Control.Rebuilt.Should().Equal(rig.Owner.ArtworkPixels.ToArray());
        rig.Timeline.Reads.Should().Be(0, "a suppressed animation does not even select a frame");
        (await rig.Owner.GetKeyboardDisplayAnimationAsync()).State.Should().Be(KeyboardDisplayAnimationStates.SuppressedByNativeMenu);
        File.ReadAllBytes(rig.AnimationPreference).Should().Equal(rig.AnimationBefore);
        File.ReadAllBytes(rig.PagePreference).Should().Equal(rig.PageBefore);
    }

    [Fact]
    public async Task Without_installed_ownership_the_same_owner_plays_the_animation_on_the_legacy_page()
    {
        await using var rig = new AnimatedOwnerRig(null, mode: 7, nativeFirmware: false);
        rig.Timeline.At(1500);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Control.ImageBegins >= 1);
        await Until(() => rig.Control.Rebuilt.AsSpan().SequenceEqual(KeyboardDisplayServiceTests.Fill(0x22)));
        await rig.Bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);
        (await rig.Owner.GetKeyboardDisplayAnimationAsync()).State.Should().Be(KeyboardDisplayAnimationStates.Playing);
        rig.Control.Writes.Should().NotContain("12/7D");
    }

    private sealed class AnimatedOwnerRig : IAsyncDisposable
    {
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "SolAnimatedPage-" + Guid.NewGuid().ToString("N"));
        public string PagePreference => Path.Combine(Data, "peripherals", "keyboard-display.json");
        public string AnimationPreference => Path.Combine(Data, "peripherals", KeyboardDisplayAnimation.FileName);
        public byte[] PageBefore { get; } = Encoding.UTF8.GetBytes("{\r\n  \"Schema\": 1,  \"Enabled\": true\r\n}\r\n");
        public byte[] AnimationBefore { get; } = KeyboardDisplayServiceTests.AnimationFile(enabled: true);
        public Input Input { get; }
        public Connection Control { get; }
        public RogWiredLightingBridge Bridge { get; } = new();
        public FakeKeyboardOledMedia Media { get; } = new()
        {
            Scene = new([new("screen:h2:test", KeyboardDisplayServiceTests.AssetA, TimelineId: KeyboardDisplayServiceTests.SharedTimelineId)]),
        };
        public FakeSharedTimeline Timeline { get; } = new();
        public KeyboardDisplayService Owner { get; }

        public AnimatedOwnerRig(KeyboardNativeMenuOptions? policy, byte mode, bool nativeFirmware = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PagePreference)!);
            File.WriteAllBytes(PagePreference, PageBefore);
            File.WriteAllBytes(AnimationPreference, AnimationBefore);
            var time = new SourceClock(); Input = new(time, 1);
            Control = new(time, 1) { Input = Input, Mode = mode, EnforceNativeImageGate = nativeFirmware };
            RogNativeMenuSource? source = null;
            if (policy is not null)
            {
                ulong cookie = Cookie; source = new(policy, Input, Input, time, () => cookie++); source.RequestEnabled(true);
            }
            var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
            Owner = new(new()
            {
                DataDirectory = Data,
                KeyboardDisplay = new() { Enabled = false, ControlVerified = true, LightingVerified = true,
                    DeviceKey = Control.DeviceKey, FirmwareHeader = Control.FirmwareHeader, AnimationAdmitted = true },
            }, rgb, KeyboardAnimationScreens.Create(Media), new LegacyPageFactory(Control, false), new MutationClearGuard(), Bridge,
                nativeMenu: source, keyboardMedia: Media, sharedTimeline: Timeline, time: time);
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.StopAsync(default); Owner.Dispose(); Input.Dispose(); Control.Dispose();
            if (Directory.Exists(Data)) Directory.Delete(Data, true);
        }
    }
}
