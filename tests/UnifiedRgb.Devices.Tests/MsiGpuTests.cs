using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Transports.Nvapi;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

internal sealed class FakeNvapi : INvapi
{
    public static readonly IntPtr Gpu0 = 0x1000, Gpu1 = 0x2000, Gpu2 = 0x3000;

    public bool Available { get; init; } = true;

    public Dictionary<IntPtr, NvPciIds> Gpus { get; init; } = new()
    {
        [Gpu0] = new NvPciIds(0x2B8510DE, 0x53011462, 0xA1, 0),
    };

    public int WriteStatus { get; set; }

    public List<string> Log { get; } = [];

    public bool Initialize() => Available;

    public IReadOnlyList<IntPtr> EnumPhysicalGpus() => Gpus.Keys.ToArray();

    public NvPciIds GetPciIdentifiers(IntPtr gpu) => Gpus[gpu];

    /// <summary>Profiles each I2C call carried, in call order.</summary>
    public List<NvI2cProfile> Profiles { get; } = [];

    /// <summary>Scripted reads (status, bytes the driver writes, returned Size); empty = reads unsupported.</summary>
    public Queue<(int Status, byte[] Data, int ReturnedSize)> Reads { get; } = new();

    public int I2cWrite(IntPtr gpu, byte device, byte register, ReadOnlySpan<byte> data, NvI2cProfile profile)
    {
        Profiles.Add(profile);
        Log.Add($"W {gpu:X} {device:X2} {register:X2} {Convert.ToHexString(data)}");
        return WriteStatus;
    }

    public int I2cRead(IntPtr gpu, byte device, byte register, Span<byte> data, NvI2cProfile profile, out int returnedSize)
    {
        Profiles.Add(profile);
        Log.Add($"R {gpu:X} {device:X2} {register:X2} {data.Length}");
        if (!Reads.TryDequeue(out var r))
        {
            throw new NotSupportedException();
        }

        data.Clear();
        r.Data.AsSpan(0, Math.Min(r.Data.Length, data.Length)).CopyTo(data);
        returnedSize = r.ReturnedSize;
        return r.Status;
    }

    public int? GetGpuTemperature(IntPtr gpu) => 41;
}

public class MsiGpuTests
{
    private static readonly MsiGpuOptions Verified = new() { RegisterMapVerified = true };

    [Fact]
    public void Discovery_matches_only_the_msi_rtx_5090_suprim()
    {
        var nv = new FakeNvapi
        {
            Gpus = new()
            {
                [FakeNvapi.Gpu0] = new(0x2B8510DE, 0x12345678, 0, 0), // other board partner
                [FakeNvapi.Gpu1] = new(0x268410DE, 0x53011462, 0, 0), // MSI, different GPU
                [FakeNvapi.Gpu2] = new(0x2B8510DE, 0x53011462, 0, 0), // the target
            },
        };
        var found = MsiGpuDiscovery.Discover(nv);
        found.Should().ContainSingle();
        found[0].Handle.Should().Be(FakeNvapi.Gpu2);
        var d = found[0].Descriptor;
        d.Class.Should().Be(DeviceClass.MsiGpu);
        d.Zones.Single().Layout.Should().Be(ZoneLayout.Point);
        d.Capabilities.MaxFps.Should().Be(20);
        d.Capabilities.BrightnessLevels.Should().Be(5);
        d.Capabilities.SpeedLevels.Should().Be(3);
        d.Capabilities.MaxColors.Should().Be(3);
        MsiGpuDiscovery.Discover(new FakeNvapi { Available = false }).Should().BeEmpty();
    }

    [Fact]
    public async Task Unverified_register_map_never_opens_or_writes()
    {
        var nv = new FakeNvapi();
        var a = new MsiLegacyAdapter(nv, new MsiGpuOptions());
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        var open = () => a.OpenAsync(d.Id, CancellationToken.None);
        await open.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not verified*");
        var write = () => a.WriteFrame(d.Id, new ZoneId(0), [Rgb.White]);
        write.Should().Throw<InvalidOperationException>();
        nv.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_sequence_matches_recording()
    {
        var nv = new FakeNvapi();
        var a = new MsiLegacyAdapter(nv, Verified);
        var id = (await a.DiscoverAsync(CancellationToken.None)).Single().Id;
        await a.OpenAsync(id, CancellationToken.None);
        foreach (var c in new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255), new Rgb(0, 0, 255) })
        {
            a.WriteFrame(id, new ZoneId(0), [c]);
        }

        Fixture.Match("direct_rgb", nv.Log); // repeated blue is deduplicated
    }

    [Fact]
    public async Task Firmware_modes_match_recording_and_direct_reasserts_static()
    {
        var nv = new FakeNvapi();
        var a = new MsiLegacyAdapter(nv, Verified);
        var id = (await a.DiscoverAsync(CancellationToken.None)).Single().Id;
        await a.OpenAsync(id, CancellationToken.None);
        nv.Log.Clear();
        await a.SetFirmwareModeAsync(id, new FirmwareMode("Breathing", [new Rgb(255, 0, 128), new Rgb(0, 64, 255)], 1, 3), CancellationToken.None);
        await a.SetFirmwareModeAsync(id, new FirmwareMode("Rainbow", [], 2, 4), CancellationToken.None);
        await a.SetFirmwareModeAsync(id, new FirmwareMode("Off", [], 0, 0), CancellationToken.None);
        a.WriteFrame(id, new ZoneId(0), [new Rgb(1, 2, 3)]);
        Fixture.Match("firmware_modes", nv.Log);
    }

    [Theory]
    [InlineData("Static", 2, 0, 0)]
    [InlineData("Breathing", 1, 3, 0)]
    [InlineData("Breathing", 1, 0, 5)]
    [InlineData("Breathing", 4, 0, 0)]
    [InlineData("Breathing", 1, -1, 0)]
    [InlineData("Lava", 1, 0, 0)]
    public void Firmware_mode_parameters_out_of_range_are_rejected(string name, int colors, int speed, int brightness)
    {
        var mode = new FirmwareMode(name, Enumerable.Repeat(Rgb.White, colors).ToArray(), speed, brightness);
        var act = () => MsiGpuEncoder.FirmwareMode(mode);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_encoded_write_targets_an_allow_listed_register()
    {
        var ops = MsiGpuEncoder.EnterDirect().Append(MsiGpuEncoder.DirectColor(Rgb.White)).ToList();
        foreach (var m in MsiGpuModes.All)
        {
            for (var s = 0; s < 3; s++)
            {
                for (var b = 0; b < 5; b++)
                {
                    ops.AddRange(MsiGpuEncoder.FirmwareMode(new FirmwareMode(m.Name, Enumerable.Repeat(Rgb.White, m.MaxColors).ToArray(), s, b)));
                }
            }
        }

        ops.Should().OnlyContain(o => MsiGpuV2Registers.Writable.Contains(o.Register));
        ops.Select(o => o.ToFrame()[0]).Should().OnlyContain(d => d == MsiGpuV2Registers.I2cAddress);
    }

    [Theory]
    [InlineData(0x68, 0x3F)] // not on the allow-list (e.g. a commit/save register)
    [InlineData(0x68, 0x00)]
    [InlineData(0x50, 0x27)] // wrong device (e.g. an EEPROM)
    public async Task Firewall_blocks_off_list_writes_before_the_bus(int device, int register)
    {
        var nv = new FakeNvapi();
        var chain = MsiLegacyAdapter.BuildChain(new NvapiI2cTransport(nv, FakeNvapi.Gpu0), new DeviceState());
        var act = async () => await chain.SendAsync(new byte[] { (byte)device, (byte)register, 0x01 }, CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenRegisterException>();
        nv.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Nvapi_error_surfaces_as_exception_for_the_writer_to_count()
    {
        var nv = new FakeNvapi();
        var a = new MsiLegacyAdapter(nv, Verified);
        var id = (await a.DiscoverAsync(CancellationToken.None)).Single().Id;
        await a.OpenAsync(id, CancellationToken.None);
        nv.WriteStatus = -5;
        var act = () => a.WriteFrame(id, new ZoneId(0), [new Rgb(9, 9, 9)]);
        act.Should().Throw<NvapiException>().Which.Status.Should().Be(-5);
    }

    [Fact]
    public async Task Telemetry_is_read_only_temperature()
    {
        var a = new MsiLegacyAdapter(new FakeNvapi(), new MsiGpuOptions());
        var id = (await a.DiscoverAsync(CancellationToken.None)).Single().Id;
        a.ReadTelemetry(id)!.TemperatureC.Should().Be(41);
    }
}

/// <summary>
/// Recorded I2C sequences in tests/Fixtures/nvapi/&lt;name&gt;.txt (one write per line). Generated once from the encoder
/// (URGB_UPDATE_FIXTURES=1); to be replaced by a capture from the owner's GPU during [HW] bring-up.
/// </summary>
internal static class Fixture
{
    public static void Match(string name, IReadOnlyList<string> actual)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Fixtures", "nvapi", name + ".txt");
        if (Environment.GetEnvironmentVariable("URGB_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, actual);
        }

        File.Exists(path).Should().BeTrue($"fixture {path} must exist");
        actual.Should().Equal(File.ReadAllLines(path));
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
