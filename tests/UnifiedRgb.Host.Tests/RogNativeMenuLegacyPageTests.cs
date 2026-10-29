using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(false, 5)]
    [InlineData(false, 9)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    public async Task Installed_policy_suppresses_saved_legacy_page_ON_at_actual_owner_startup(bool requested, byte mode)
    {
        await using var rig = new LegacyPageOwnerRig(Policy(), requested, mode);
        await rig.Owner.StartAsync(default);
        await rig.WaitForSuppression();
        await rig.Bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);
        rig.Control.ImageBegins.Should().Be(0); rig.Control.RejectedImages.Should().Be(0);
        rig.Control.Colours.Should().Contain(new Rgb(11, 22, 33));
        rig.Control.Arms.Should().Be(0); rig.Input.Opens.Should().Be(0);
        if (!requested) rig.Control.Writes.Should().NotContain("12/7D", "operational disable must not add CAP queries");
        else rig.Control.Writes.Should().Contain("12/7D");
        await rig.AssertSavedOnAndSuppressed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installed_policy_preserves_page_ON_RGB_and_native_brightness_across_native_mode_exit_or_disable(bool disable)
    {
        await using var rig = new LegacyPageOwnerRig(Policy(), true, 7);
        await rig.Owner.StartAsync(default); await rig.WaitForSuppression();
        rig.Control.Mode = RogNativeMenuCodec.NativeMode;
        await Until(() => rig.Control.ImageBegins == 1 && rig.Control.Rebuilt.Length == 6656);
        rig.Control.Arms.Should().Be(1); rig.Control.Rebuilt.Should().HaveCount(6656);
        rig.Input.Opens.Should().Be(1);
        await rig.Bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);
        var restores = rig.Control.NativeRestores;
        if (disable) rig.Source!.RequestEnabled(false); else rig.Control.Mode = 7;
        await Until(() => rig.Input.LiveHandles == 0 && rig.Control.NativeRestores > restores);
        var caps = rig.Control.Writes.Count(p => p == "12/7D");
        await rig.Bridge.WriteAsync(new(44, 55, 66), default).WaitAsync(Patience);
        await rig.Bridge.WriteAsync(new(77, 88, 99), default).WaitAsync(Patience);
        rig.Control.ImageBegins.Should().Be(1, "installed ownership must prevent legacy fallback after native output ends");
        rig.Control.RejectedImages.Should().Be(0);
        if (disable) rig.Control.Writes.Count(p => p == "12/7D").Should().Be(caps);
        rig.Control.Colours.Should().Contain(new Rgb(11, 22, 33)).And.Contain(new Rgb(44, 55, 66)).And.Contain(new Rgb(77, 88, 99));
        rig.Control.MaximumControl.Should().Be(1); rig.Control.ArmWhileInputReading.Should().BeFalse();
        await rig.AssertSavedOnAndSuppressed();
        using var preference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(rig.Data, "peripherals", "keyboard-native-display.json")));
        preference.RootElement.GetProperty("Brightness").GetByte().Should().Be(100);
        preference.RootElement.GetProperty("ProfileToken").GetByte().Should().Be(6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installed_policy_keeps_status_suppressed_during_explicit_legacy_preference_toggles_without_losing_fault_recovery(bool initialOwnerFault)
    {
        await using var rig = new LegacyPageOwnerRig(Policy(), false, 7, failFirstOpen: initialOwnerFault);
        await rig.Owner.StartAsync(default);
        if (initialOwnerFault)
        {
            await Until(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error");
            await Task.Delay(650); // Cross the real faulted owner loop before reasserting an explicit disabled preference.
            await rig.Owner.SetKeyboardDisplayEnabledAsync(false);
            await Until(() => rig.Bridge.Available);
        }
        else await rig.WaitForSuppression();
        var restores = rig.Control.NativeRestores;
        foreach (var enabled in new[] { false, true })
        {
            await rig.Owner.SetKeyboardDisplayEnabledAsync(enabled);
            var status = await rig.Owner.GetKeyboardDisplayStatusAsync();
            status.Enabled.Should().Be(enabled); status.State.Should().Be("Stopped");
            status.Detail.Should().Contain("原生光域菜单").And.Contain("偏好已保留");
            using var saved = JsonDocument.Parse(File.ReadAllBytes(rig.Preference));
            saved.RootElement.GetProperty("Enabled").GetBoolean().Should().Be(enabled);
            await rig.Bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);
        }
        rig.Control.NativeRestores.Should().Be(restores, "saving a suppressed preference does not take over native brightness");
        rig.Control.ImageBegins.Should().Be(0); rig.Control.RejectedImages.Should().Be(0);
        rig.Control.Writes.Should().NotContain("12/7D"); rig.Input.Opens.Should().Be(0);
        rig.Factory.Opens.Should().Be(initialOwnerFault ? 2 : 1, "a healthy no-op preference save must not reopen; an explicit fault recovery must still reopen");
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("default")]
    [InlineData("off")]
    [InlineData("invalid")]
    public async Task Absent_default_off_or_invalid_policy_preserves_actual_owner_legacy_page_behavior(string kind)
    {
        var policy = kind switch { "absent" => null, "default" => new KeyboardNativeMenuOptions(),
            "off" => Policy() with { Enabled = false }, _ => Policy() with { CandidateSha256 = new string('0', 64) } };
        await using var rig = new LegacyPageOwnerRig(policy, true, 7, nativeFirmware: false);
        await rig.Owner.StartAsync(default);
        await Until(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Running");
        rig.Control.ImageBegins.Should().Be(1); rig.Control.Rebuilt.Should().HaveCount(6656);
        File.ReadAllBytes(rig.Preference).Should().Equal(rig.PreferenceBefore);
        await rig.Bridge.WriteAsync(new(11, 22, 33), default).WaitAsync(Patience);
        await rig.Owner.SetKeyboardDisplayEnabledAsync(false);
        await Until(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Stopped");
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("键盘原动画已恢复");
        await rig.Owner.SetKeyboardDisplayEnabledAsync(true);
        await Until(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().FramesAcknowledged == 2);
        rig.Control.ImageBegins.Should().Be(2);
        rig.Control.Writes.Should().NotContain("12/7D"); rig.Input.Opens.Should().Be(0);
    }

    private sealed class LegacyPageOwnerRig : IAsyncDisposable
    {
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "SolLegacyPage-" + Guid.NewGuid().ToString("N"));
        public string Preference => Path.Combine(Data, "peripherals", "keyboard-display.json");
        public byte[] PreferenceBefore { get; } = Encoding.UTF8.GetBytes("{\r\n  \"Schema\": 1,  \"Enabled\": true\r\n}\r\n");
        public Input Input { get; }
        public Connection Control { get; }
        public RogNativeMenuSource? Source { get; }
        public RogWiredLightingBridge Bridge { get; } = new();
        public LegacyPageFactory Factory { get; }
        public KeyboardDisplayService Owner { get; }
        public LegacyPageOwnerRig(KeyboardNativeMenuOptions? policy, bool requested, byte mode, bool nativeFirmware = true, bool failFirstOpen = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Preference)!); File.WriteAllBytes(Preference, PreferenceBefore);
            var time = new SourceClock(); Input = new(time, 1);
            Control = new(time, 1) { Input = Input, Mode = mode, EnforceNativeImageGate = nativeFirmware };
            if (policy is not null)
            {
                ulong cookie = Cookie; Source = new(policy, Input, Input, time, () => cookie++); Source.RequestEnabled(requested);
            }
            var rgb = DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
            var screens = DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
            Factory = new(Control, failFirstOpen);
            Owner = new(new()
            {
                DataDirectory = Data,
                KeyboardDisplay = new() { Enabled = false, ControlVerified = true, LightingVerified = true,
                    DeviceKey = Control.DeviceKey, FirmwareHeader = Control.FirmwareHeader },
            }, rgb, screens, Factory, new MutationClearGuard(), Bridge, nativeMenu: Source);
        }
        public async Task WaitForSuppression()
        {
            await Until(() => Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error"
                || (Bridge.Available && Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().Detail.Contains("原生光域菜单", StringComparison.Ordinal)));
            await AssertSavedOnAndSuppressed();
        }
        public async Task AssertSavedOnAndSuppressed()
        {
            var status = await Owner.GetKeyboardDisplayStatusAsync();
            status.Enabled.Should().BeTrue("the saved page preference remains ON despite installed output ownership");
            status.State.Should().Be("Stopped"); status.LastError.Should().BeNull(); status.FramesAcknowledged.Should().Be(0);
            status.Detail.Should().Contain("原生光域菜单").And.Contain("偏好已保留");
            File.ReadAllBytes(Preference).Should().Equal(PreferenceBefore);
        }
        public async ValueTask DisposeAsync()
        {
            await Owner.StopAsync(default); Owner.Dispose(); Input.Dispose(); Control.Dispose();
            if (Directory.Exists(Data)) Directory.Delete(Data, true);
        }
    }

    private sealed class LegacyPageFactory(Connection control, bool failFirstOpen) : IRogWiredOledFactory
    {
        public int Opens { get; private set; }
        public List<KeyboardNativePresentationScope> RequestedScopes { get; } = [];
        public RogWiredOledConnection? TryOpen(string deviceKey) => throw new InvalidOperationException("Exact firmware overload required.");
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
        {
            Opens++;
            RequestedScopes.Add(new(deviceKey, firmwareHeader, "wired"));
            if (failFirstOpen && Opens == 1) throw new IOException("Injected initial owner open fault.");
            return new(new LegacyPageChannel(control), () => true, () => deviceKey, () => firmwareHeader) { NativeControlBinding = control.NativeControlBinding };
        }
    }

    private sealed class LegacyPageChannel(Connection control) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
        {
            var request = packet.ToArray();
            var reply = await control.ExchangeAsync(request, false, ct);
            if (request[1] == 0x12 && request[2] == 0) { reply[5] = 0x25; reply[7] = 1; }
            _replies.Writer.TryWrite(reply).Should().BeTrue();
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
        public void Dispose() => _replies.Writer.TryComplete();
    }
}
