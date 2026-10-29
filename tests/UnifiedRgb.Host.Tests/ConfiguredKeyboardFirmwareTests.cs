using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

public sealed class ConfiguredKeyboardFirmwareTests
{
    private const string SyntheticWired25 = "001200000025000100";
    private static KeyboardDisplayOptions Verified => new() { Enabled = true, ControlVerified = true, DeviceKey = new string('A', 64), FirmwareHeader = SyntheticWired25 };

    [Fact]
    public void Explicit_verified_local_wired_header_is_not_silently_pinned_to_legacy24()
    {
        // Synthetic .25 fixture; no physical wired identity is inferred or adopted.
        var options = new KeyboardDisplayOptions { ControlVerified = true, DeviceKey = new string('A', 64), FirmwareHeader = "001200000025000100" };
        options.HasVerifiedWiredIdentity.Should().BeTrue();
    }

    [Fact]
    public void Valid_header_format_does_not_replace_explicit_local_verification_or_safe_defaults()
    {
        new KeyboardDisplayOptions().HasVerifiedWiredIdentity.Should().BeFalse();
        new KeyboardDisplayOptions().FirmwareHeader.Should().BeEmpty();
        (Verified with { ControlVerified = false }).HasVerifiedWiredIdentity.Should().BeFalse();
        (Verified with { DeviceKey = "1C24" }).HasVerifiedWiredIdentity.Should().BeFalse();
        (Verified with { LightingVerified = false }).HasVerifiedWiredLighting.Should().BeFalse();
        (Verified with { LightingVerified = true }).HasVerifiedWiredLighting.Should().BeTrue();
        (Verified with { FirmwareHeader = "001200000024000100" }).HasVerifiedWiredIdentity.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0012000000250001")] [InlineData("001300000025000100")]
    [InlineData("001200000000000000")] [InlineData("00120000002G000100")] [InlineData("0800070025000100")]
    public async Task Invalid_or_nonwired_configured_header_cannot_reach_either_factory_entry(string? header)
    {
        var options = Verified with { FirmwareHeader = header! }; options.HasVerifiedWiredIdentity.Should().BeFalse();
        using var factory = new ConfiguredFactory(); using var service = Service(options, factory, DirectoryForFixture());
        using var connection = await Open(service); connection.Should().BeNull();
        factory.ConfiguredOpens.Should().Be(0); factory.LegacyOpens.Should().Be(0);
    }

    [Fact]
    public async Task Actual_normal_open_passes_the_exact_configured_header_to_the_new_factory_entry()
    {
        using var factory = new ConfiguredFactory(); using var service = Service(Verified, factory, DirectoryForFixture());
        using var connection = await Open(service);
        connection.Should().NotBeNull(); connection!.FirmwareHeader.Should().Be(SyntheticWired25);
        factory.Header.Should().Be(SyntheticWired25); factory.ConfiguredOpens.Should().Be(1); factory.LegacyOpens.Should().Be(0);
        factory.Io.Commands.Should().Be(0);
    }

    [Fact]
    public async Task Actual_recorded_transport_recovery_open_passes_the_exact_configured_header_without_running_recovery_business()
    {
        var directory = DirectoryForFixture(); var journal = Path.Combine(directory, "peripherals", "keyboard-display-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, DeviceKey = Verified.DeviceKey, FirmwareHeader = SyntheticWired25, Brightness = (byte)25, ProfileToken = (byte)6 });
        File.WriteAllBytes(journal, bytes);
        using var factory = new ConfiguredFactory { Failure = new InvalidOperationException("offline configured recovery entry reached") };
        using var service = Service(Verified, factory, directory);
        await ((Func<Task>)(() => service.ReleaseForPeripheralConfigurationAsync(default))).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("offline configured recovery entry reached");
        factory.Header.Should().Be(SyntheticWired25); factory.ConfiguredOpens.Should().Be(1); factory.LegacyOpens.Should().Be(0);
        factory.Io.Commands.Should().Be(0); File.ReadAllBytes(journal).Should().Equal(bytes);
    }

    private static string DirectoryForFixture() => Path.Combine(AppContext.BaseDirectory, "configured-firmware-fixtures", Guid.NewGuid().ToString("N"));
    private static KeyboardDisplayService Service(KeyboardDisplayOptions options, IRogWiredOledFactory factory, string directory) =>
        new(new UnifiedRgbOptions { DataDirectory = directory, KeyboardDisplay = options }, null!, null!, factory, new ClearGuard());
    private static Task<IRogOledConnection?> Open(KeyboardDisplayService service) =>
        (Task<IRogOledConnection?>)typeof(KeyboardDisplayService).GetMethod("OpenConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [CancellationToken.None])!;

    private sealed class ConfiguredFactory : IRogWiredOledFactory, IDisposable
    {
        public readonly NoIo Io = new(); public int LegacyOpens { get; private set; } public int ConfiguredOpens { get; private set; }
        public string? Header { get; private set; } public Exception? Failure { get; init; }
        public RogWiredOledConnection? TryOpen(string deviceKey) { LegacyOpens++; throw new InvalidOperationException("Configured Host must use the explicit header overload"); }
        public RogWiredOledConnection? TryOpen(string deviceKey, string firmwareHeader)
        { ConfiguredOpens++; Header = firmwareHeader; if (Failure is not null) throw Failure; return new(Io, () => true, () => deviceKey, () => firmwareHeader); }
        public void Dispose() => Io.Dispose();
    }
    private sealed class NoIo : IHidReportChannel
    {
        public int Commands { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) { Commands++; throw new InvalidOperationException("No device or recovery business in factory propagation fixture"); }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { Commands++; throw new InvalidOperationException("No device or recovery business in factory propagation fixture"); }
        public void Dispose() { }
    }
    private sealed class ClearGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new InvalidOperationException("No takeover");
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new InvalidOperationException("No takeover");
        public Task RestoreAsync(CancellationToken ct) => throw new InvalidOperationException("No takeover");
    }
}
