using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// NATIVE-RF-CLOCK in the Host: LianLi:UseDeviceClock is false in source and in older files, the engine binds the clocks of
/// UploadedLoop controllers only when it is true, and the optional RfClock status reports the truth (fallback vs active).
/// </summary>
[Collection("Host")]
public sealed class DeviceClockHostTests
{
    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions());

    [Fact]
    public void The_source_flag_is_false_and_older_files_bind_false()
    {
        var path = Path.Combine(RepoRoot(), "src", "UnifiedRgb.Host", "appsettings.json");
        File.ReadAllText(path).Should().Contain("\"UseDeviceClock\": false,");
        Bind(File.ReadAllText(path)).LianLi.UseDeviceClock.Should().BeFalse();
        Bind("""{ "UnifiedRgb": { "LianLi": { "WireFormatVerified": true, "PhaseTrimMs": 400, "StreamedPlaybackRate": 1.6 } } }""")
            .LianLi.UseDeviceClock.Should().BeFalse("an OUTPUT file written before the key keeps the legacy anchor");
        Bind("""{ "UnifiedRgb": { "LianLi": { "UseDeviceClock": true } } }""").LianLi.UseDeviceClock.Should().BeTrue();
    }

    [Fact]
    public async Task By_default_no_clock_is_bound_and_the_status_says_disabled()
    {
        await using var host = await TestHost.StartAsync();
        host.Engine.Loop.DeviceClockEnabled.Should().BeFalse();
        var status = host.Engine.BuildStatus(true);
        status.RfClock.Should().NotBeNull();
        status.RfClock!.Enabled.Should().BeFalse();
        status.RfClock.Assignments.Should().NotContain(a => a.Mode == "DeviceClock");
    }

    [Fact]
    public async Task The_flag_binds_a_clock_map_at_start()
    {
        await using var host = await TestHost.StartAsync(seed: o => o.LianLi.UseDeviceClock = true);
        host.Engine.Loop.DeviceClockEnabled.Should().BeTrue();
        host.Engine.BuildStatus(true).RfClock!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Only_uploaded_loop_controllers_with_a_provided_clock_are_bound()
    {
        var clocked = new ClockAdapter("loop:clocked", Capabilities.UploadedLoopOnly(200, 20, 12288), withClock: true);
        var unclocked = new ClockAdapter("loop:none", Capabilities.UploadedLoopOnly(200, 20, 12288), withClock: false);
        var streamed = new ClockAdapter("direct:gpu", Capabilities.DirectOnly(60), withClock: true);
        using var bus = new FrameBus();
        var host = new AdapterHost([new Factory(clocked), new Factory(unclocked), new Factory(streamed)], null!,
            new WriterGuard(new ProcessScanner(new EmptySnapshot())), bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        try
        {
            var map = LightingEngine.DeviceClocks(host.Controllers);
            map.Keys.Select(k => k.Value).Should().Equal("loop:clocked");
            map[new ControllerId("loop:clocked")].Should().BeSameAs(clocked.Clock, "the adapter's stable handle, no copy of a sample");
            clocked.ClockQueries.Should().Be(1, "resolved once, off the hot path");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_status_mode_follows_the_timing_the_upload_callback_actually_reports(bool decimated)
    {
        // R2: the real DeviceWriter -> LightingEngine.OnLoopUploaded path; the adapter's LastUpload is the only timing source
        var adapter = new ClockAdapter("loop:clocked", Capabilities.UploadedLoopOnly(400, 20, 12288), withClock: true) { Decimate = decimated };
        await using var host = await TestHost.StartAsync(
            configure: s => s.AddSingleton<IDeviceAdapterFactory>(new Factory(adapter)),
            seed: o => o.LianLi.UseDeviceClock = true);
        host.Engine.Loop.DeviceClockEnabled.Should().BeTrue();
        host.Engine.SetScene(new Scene([Assignment.ForGroup("g",
            [new ZoneRef(new ControllerId("loop:clocked"), new ZoneId(0)), new ZoneRef(new ControllerId("virtual:gpu"), new ZoneId(0))],
            "Breathing", new EffectParams([new Rgb(255, 0, 0)], 1f, 0.35f)) with { Strategy = ContentStrategy.Program }]));

        StreamedClockDto? clock = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            clock = host.Engine.BuildStatus(true).RfClock!.Assignments.SingleOrDefault(a => a.Assignment == "g");
            if (clock?.UploadedFrames is not null && adapter.Uploads > 0)
            {
                break;
            }

            await Task.Delay(20);
        }

        adapter.Uploads.Should().BeGreaterThan(0);
        var sent = adapter.LastUpload(new ControllerId("loop:clocked"))!;
        clock.Should().NotBeNull();
        (clock!.UploadedFrames, clock.UploadedInterval).Should().Be((sent.Frames, sent.IntervalMs), "the status shows the callback data");
        if (decimated)
        {
            clock.Mode.Should().Be("UploadAnchor", "no lock against a source-only period the device does not play");
            clock.FallbackReason.Should().Contain("differs from the compiled reference").And.Contain("unsupported");
            clock.ReferenceFrame.Should().BeNull();
            (sent.Frames, sent.IntervalMs).Should().NotBe((clock.ReferenceFrames, clock.ReferenceInterval));
        }
        else
        {
            clock.Mode.Should().Be("DeviceClock");
            (clock.ReferenceFrames, clock.ReferenceInterval).Should().Be((sent.Frames, sent.IntervalMs));
        }
    }

    [Fact]
    public void The_status_dto_is_optional_and_maps_the_report_truthfully()
    {
        var reports = new[]
        {
            new StreamedClockReport("g", StreamedClockMode.DeviceClock, null)
            {
                Domain = "lianli-rf-master#3", RawTicks = 4_000_000_000, SampleAgeSeconds = 0.4321, QueryLatencyMs = 1.5, TicksPerSecond = 1600,
                ExtrapolatedTicks = 4_000_000_691.37, ReferenceFrames = 170, ReferenceInterval = 60, NominalPeriodMs = 10200,
                DerivedPeriodMs = 6375, ReferenceFrame = 12, UploadedFrames = 170, UploadedInterval = 60,
            },
            new StreamedClockReport("h", StreamedClockMode.UploadAnchor, "device-clock sample is stale (3.2 s > 3 s)") { TicksPerSecond = 1600 },
        };
        var dto = DtoMapper.ToDto(true, reports);
        dto.Enabled.Should().BeTrue();
        dto.Assignments[0].Should().Be(new StreamedClockDto("g", "DeviceClock", null, "lianli-rf-master#3", 4_000_000_000, 432.1, 1.5, 1600,
            4_000_000_691.4, 170, 60, 10200, 6375, false, 12, 170, 60));
        dto.Assignments[1].Mode.Should().Be("UploadAnchor");
        dto.Assignments[1].FallbackReason.Should().Contain("stale");
        dto.Assignments[1].ReferenceFrame.Should().BeNull();
        dto.Assignments[1].UploadedFrames.Should().BeNull();

        var status = new StatusDto(DateTimeOffset.UnixEpoch, "t", 1, 0, new ClockStatusDto(60, 60, 0, 0, 0, 0, 0), [], RfClock: dto);
        var json = JsonSerializer.Serialize(status, Json);
        var back = JsonSerializer.Deserialize<StatusDto>(json, Json)!.RfClock!;
        back.Enabled.Should().BeTrue();
        back.Assignments.Should().Equal(dto.Assignments);
        var older = JsonNode.Parse(json)!.AsObject();
        older.Remove("rfClock").Should().BeTrue();
        JsonSerializer.Deserialize<StatusDto>(older.ToJsonString(), Json)!.RfClock.Should().BeNull("a status from an older Host has none");
        JsonSerializer.Deserialize<StreamedClockDto>("""{"assignment":"x","mode":"MasterTime"}""", Json)!
            .Should().Be(new StreamedClockDto("x", "MasterTime"), "every other field is optional");
    }

    private static UnifiedRgbOptions Bind(string json)
    {
        var o = new UnifiedRgbOptions();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(UnifiedRgbOptions.Section).Bind(o);
        return o;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root (src/UnifiedRgb.sln) not found");
    }

    private sealed class EmptySnapshot : ISystemSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes() => [];

        public IReadOnlyList<ServiceInfo> Services() => [];
    }

    private sealed class Factory(IDeviceAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => adapter.Class;

        public IDeviceAdapter Create(IServiceProvider sp) => adapter;
    }

    private sealed class FixedClock : IDeviceClock
    {
        private readonly DeviceClockDomain _domain = DeviceClockDomain.Next("test");

        /// <summary>A fresh sample on every read (never stale), counter 1.</summary>
        public DeviceClockSample? Latest => new(_domain, 1, Stopwatch.GetTimestamp(), 1600, Stopwatch.GetTimestamp());
    }

    /// <summary>A Virtual-class (guard always clear) adapter with one controller and, optionally, a device clock.</summary>
    private sealed class ClockAdapter(string id, Capabilities caps, bool withClock) : ILoopUploadAdapter, IDeviceClockProvider
    {
        private readonly ControllerDescriptor _d = new(new ControllerId(id), "L", id, DeviceType.Cooler, DeviceClass.Virtual, id,
            [new ZoneDescriptor(new ZoneId(0), "z", ZoneLayout.Linear(4))], caps);

        public IDeviceClock Clock { get; } = new FixedClock();

        public int ClockQueries { get; private set; }

        /// <summary>Report the upload as the LianLi compressor would after one halving: half the frames, twice the interval.</summary>
        public bool Decimate { get; init; }

        public int Uploads => Volatile.Read(ref _uploads);

        private int _uploads;
        private LoopUploadStats? _last;

        public string Vendor => "L";

        public DeviceClass Class => DeviceClass.Virtual;

        public event Action<HotplugEvent>? Hotplug
        {
            add { }
            remove { }
        }

        public IDeviceClock? ClockOf(ControllerId controller)
        {
            ClockQueries++;
            return withClock && controller == _d.Id ? Clock : null;
        }

        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([_d]);

        public Task OpenAsync(ControllerId controller, CancellationToken ct) => Task.CompletedTask;

        public void WriteFrame(ControllerId controller, ZoneId zone, ReadOnlySpan<Rgb> colors)
        {
        }

        public Task UploadLoopAsync(ControllerId controller, ZoneId zone, LoopFrames loop, CancellationToken ct)
        {
            var (frames, interval) = Decimate ? ((loop.Frames + 1) / 2, Math.Round(loop.IntervalMs * 2 * 100) / 100) : (loop.Frames, Math.Round(loop.IntervalMs * 100) / 100);
            Volatile.Write(ref _last, new LoopUploadStats(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), loop.EffectIndex, frames, interval, 0, 0, 1,
                TimeSpan.Zero, Decimate ? 1 : 0, "test") { SourceFrames = loop.Frames });
            Interlocked.Increment(ref _uploads);
            return Task.CompletedTask;
        }

        public LoopUploadStats? LastUpload(ControllerId controller) => Volatile.Read(ref _last);

        public Task SetFirmwareModeAsync(ControllerId controller, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Telemetry? ReadTelemetry(ControllerId controller) => null;

        public Task CloseAsync(ControllerId controller) => Task.CompletedTask;
    }
}
