using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Screens.Tests;

[CollectionDefinition("ScreensHost", DisableParallelization = true)]
public class ScreensHostGroup
{
}

/// <summary>
/// The real Host composition (Virtual RGB adapter, temp data directory, unique pipe) with a fake screen platform: the additive
/// IScreenService RPC target, SceneDto.Screens through Get/Set/Save/Load/Apply, whole-candidate validation and old profiles.
/// </summary>
[Collection("ScreensHost")]
public sealed class HostIntegrationTests
{
    private static readonly SceneDto RedFan = new([new AssignmentDto([new ZoneRefDto("virtual:fan1", 0)], "StaticColor", ["#FF0000"])]);
    private static readonly SceneDto BlueFan = new([new AssignmentDto([new ZoneRefDto("virtual:fan1", 0)], "StaticColor", ["#0000FF"])]);

    [Fact]
    public async Task Both_services_answer_on_one_connection_and_an_rgb_only_scene_has_no_screens_key()
    {
        await using var host = await ScreensTestHost.StartAsync(Devices.H2);
        await using var c = await host.ConnectAsync();

        await c.Rgb.SetSceneAsync(RedFan);
        var scene = await c.Rgb.GetSceneAsync();
        var raw = await c.Rpc.InvokeWithCancellationAsync<JsonElement>("GetScene", [], CancellationToken.None);

        scene.Screens.Should().BeNull();
        raw.TryGetProperty("screens", out _).Should().BeFalse("an RGB-only scene is unchanged on the wire");
        raw.GetProperty("assignments").GetArrayLength().Should().Be(1);
        (await c.Rgb.GetTelemetryAsync()).Should().NotBeNull("the RGB telemetry RPC is untouched by the PC telemetry RPC");
        var screens = await c.Screens.ListScreensAsync();
        screens.Should().ContainSingle().Which.Should().Match<ScreenDeviceDto>(s => s.Id == Devices.H2.Id && s.State == ScreenStates.Disabled);
        (await c.Screens.GetPcTelemetryAsync()).Source.Should().StartWith("GetSystemTimes");
        (await c.Screens.GetScreenSceneAsync()).Assignments.Should().BeEmpty();
        var preview = await c.Screens.PreviewScreenAsync(new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#102030"));
        (preview.Width, preview.Height).Should().Be((480, 480));
        host.Opener.Opens.Should().Be(0, "screens are disabled by default and a preview never opens a device");
    }

    [Fact]
    public async Task A_mixed_scene_survives_set_get_save_load_and_apply()
    {
        await using var host = await ScreensTestHost.StartAsync();
        await using var c = await host.ConnectAsync();
        var png = await Images.PngAsync(Images.Solid(32, 32, 200, 100, 0));
        var asset = await c.Screens.UploadScreenAssetAsync(new ScreenAssetUploadDto("logo", Convert.ToBase64String(png)));
        var screens = new ScreenSceneDto(
        [
            new ScreenAssignmentDto(Devices.H2.Id, asset.Id, "#000000", "compact", 90, 0.8, 10, true, "#FF00FF", "contain"),
            new ScreenAssignmentDto(Devices.Turzx.Id, Overlay: "dashboard"),
        ]);
        var mixed = BlueFan with { Screens = screens };

        await c.Rgb.SetSceneAsync(mixed);
        var got = await c.Rgb.GetSceneAsync();
        got.Assignments.Should().ContainSingle().Which.Colors.Should().Equal("#0000FF");
        got.Screens.Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        (await c.Screens.GetScreenSceneAsync()).Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());

        await c.Rgb.SaveProfileAsync(new ProfileDto("Mixed", mixed));
        var loaded = await c.Rgb.LoadProfileAsync("Mixed");
        loaded.Scene.Screens.Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        File.ReadAllText(Path.Combine(host.DataDirectory, "profiles", "Mixed.json")).Should().Contain("\"screens\"");

        await c.Rgb.SetSceneAsync(RedFan); // Screens null: screen content unchanged
        (await c.Screens.GetScreenSceneAsync()).Assignments.Should().HaveCount(2);
        await c.Screens.SetScreenSceneAsync(new ScreenSceneDto([]));
        (await c.Rgb.GetSceneAsync()).Screens.Should().BeNull("an empty screen scene is not reported in the RGB scene");

        await c.Rgb.ApplyProfileAsync("Mixed");
        (await c.Screens.GetScreenSceneAsync()).Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        (await c.Rgb.GetSceneAsync()).Assignments.Single().Colors.Should().Equal("#0000FF");
        await FluentActions.Awaiting(() => c.Screens.DeleteScreenAssetAsync(asset.Id)).Should().ThrowAsync<RemoteInvocationException>()
            .WithMessage("*used by the current screen scene*");
        File.ReadAllText(Path.Combine(host.DataDirectory, "screens", "scene.json")).Should().Contain(asset.Id, "the screen scene persists");
    }

    [Fact]
    public async Task An_invalid_part_rejects_the_whole_candidate()
    {
        await using var host = await ScreensTestHost.StartAsync();
        await using var c = await host.ConnectAsync();
        var keep = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#111111")]);
        await c.Rgb.SetSceneAsync(RedFan with { Screens = keep });

        var badScreens = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, Rotation: 45)]);
        var missingAsset = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, "0123456789abcdef")]);
        var twice = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id), new ScreenAssignmentDto(Devices.H2.Id)]);
        foreach (var bad in new[] { badScreens, missingAsset, twice })
        {
            await FluentActions.Awaiting(() => c.Rgb.SetSceneAsync(BlueFan with { Screens = bad })).Should().ThrowAsync<RemoteInvocationException>();
            await FluentActions.Awaiting(() => c.Rgb.SaveProfileAsync(new ProfileDto("Bad", BlueFan with { Screens = bad }))).Should().ThrowAsync<RemoteInvocationException>();
        }

        var badRgb = new SceneDto([new AssignmentDto([new ZoneRefDto("virtual:nope", 0)], "StaticColor", [])],
            new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#FFFFFF")]));
        await FluentActions.Awaiting(() => c.Rgb.SetSceneAsync(badRgb)).Should().ThrowAsync<RemoteInvocationException>();

        // a profile edited on disk with an invalid screen part is refused before either half is applied
        var path = Path.Combine(host.DataDirectory, "profiles", "Edited.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProfileStore.Serialize(new ProfileDto("Edited", BlueFan with { Screens = badScreens })));
        await FluentActions.Awaiting(() => c.Rgb.ApplyProfileAsync("Edited")).Should().ThrowAsync<RemoteInvocationException>();

        var now = await c.Rgb.GetSceneAsync();
        now.Assignments.Single().Colors.Should().Equal("#FF0000");
        now.Screens.Should().BeEquivalentTo(keep);
        (await c.Rgb.ListProfilesAsync()).Select(p => p.Name).Should().Equal("Edited");
        await FluentActions.Awaiting(() => c.Screens.SetScreenSceneAsync(twice)).Should().ThrowAsync<RemoteInvocationException>();
        (await c.Screens.GetScreenSceneAsync()).Should().BeEquivalentTo(keep);
    }

    [Fact]
    public async Task Screen_output_stays_off_until_enabled_per_family()
    {
        await using var host = await ScreensTestHost.StartAsync(Devices.H2, Devices.Tl1);
        await using var c = await host.ConnectAsync();

        await c.Screens.SetScreenSceneAsync(new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id), new ScreenAssignmentDto(Devices.Tl1.Id)]));
        await Task.Delay(400);

        host.Opener.Opens.Should().Be(0);
        (await c.Screens.ListScreensAsync()).Should().OnlyContain(s => s.State == ScreenStates.Disabled);
        await c.Screens.StopScreensAsync();
    }

    [Fact]
    public void Rgb_only_profiles_serialize_exactly_as_before()
    {
        var dir = Path.Combine(RepoRoot(), "tests", "Fixtures", "profiles");
        var files = Directory.GetFiles(dir, "*.json");
        files.Should().NotBeEmpty();
        var json = ContractsJson.Configure(new JsonSerializerOptions { WriteIndented = true });

        // the contract as it was before OPUS-SCREENS: SceneDto without the Screens member
        var legacy = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver()).WithAddedModifier(ti =>
            {
                if (ti.Type == typeof(SceneDto))
                {
                    ti.Properties.Remove(ti.Properties.Single(p => p.Name == "screens"));
                }
            }),
        };
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file); // read only
            var p = JsonSerializer.Deserialize<ProfileDto>(bytes, json)!;
            p.Scene.Screens.Should().BeNull(file);

            var s = ProfileStore.Serialize(p);
            System.Text.Encoding.UTF8.GetString(s).Should().NotContain("\"screens\"", file);
            s.Should().Equal(JsonSerializer.SerializeToUtf8Bytes(p, legacy), file);
            ProfileStore.Serialize(JsonSerializer.Deserialize<ProfileDto>(s, json)!).Should().Equal(s, file);

            // the demo files were hand-written (no null members, "0.0" for 0); otherwise they hold the serializer's content
            JsonNode.DeepEquals(Canonical(JsonNode.Parse(bytes)), Canonical(JsonNode.Parse(s))).Should().BeTrue(file);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mixed_change_holds_the_screen_gate_until_its_screen_commit(bool applyProfile)
    {
        await using var host = await ScreensTestHost.StartAsync(PausingBinding.Register);
        await using var c = await host.ConnectAsync();
        var binding = host.Services.GetRequiredService<PausingBinding>();
        var asset = await UploadAsync(c);
        var screens = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, asset.Id)]);
        await c.Rgb.SetSceneAsync(RedFan);
        await c.Rgb.SaveProfileAsync(new ProfileDto("Mixed", BlueFan with { Screens = screens })); // validation only: no gate
        var (paused, release) = binding.Arm();

        var change = applyProfile ? c.Rgb.ApplyProfileAsync("Mixed") : c.Rgb.SetSceneAsync(BlueFan with { Screens = screens });
        await paused.WaitAsync(TimeSpan.FromSeconds(10)); // screen half validated, gate held, RGB not applied yet
        var delete = c.Screens.DeleteScreenAssetAsync(asset.Id);
        await Wait.UntilAsync(() => host.Manager.MutationWaiters == 1, "the delete RPC to wait for the screen gate");
        (await c.Rgb.GetSceneAsync()).Assignments.Single().Colors.Should().Equal(["#FF0000"], "reads never wait for the gate");
        delete.IsCompleted.Should().BeFalse();
        release.SetResult();
        await change.WaitAsync(TimeSpan.FromSeconds(30));

        await FluentActions.Awaiting(Wait.Outcome(delete)).Should().ThrowAsync<RemoteInvocationException>().WithMessage("*used by the current screen scene*");
        binding.Commits.Should().Equal([(true, 1, "#0000FF")],
            "at the screen commit the RGB half is already live, the gate is still held and the delete still waits");
        var now = await c.Rgb.GetSceneAsync();
        now.Assignments.Single().Colors.Should().Equal("#0000FF");
        now.Screens.Should().BeEquivalentTo(screens, o => o.WithStrictOrdering());
        (await c.Screens.ListScreenAssetsAsync()).Select(a => a.Id).Should().Equal(asset.Id);
        host.Manager.MutationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task A_mixed_scene_waiting_behind_a_delete_fails_as_a_whole()
    {
        await using var host = await ScreensTestHost.StartAsync();
        await using var c = await host.ConnectAsync();
        var keep = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#111111")]);
        await c.Rgb.SetSceneAsync(RedFan with { Screens = keep });
        var asset = await UploadAsync(c);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Manager.BeforeAssetDeleteForTest = async ct =>
        {
            entered.TrySetResult();
            await proceed.Task.WaitAsync(ct);
        };

        var delete = c.Screens.DeleteScreenAssetAsync(asset.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // in-use check passed, files not yet removed
        var set = c.Rgb.SetSceneAsync(BlueFan with { Screens = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, asset.Id)]) });
        await Wait.UntilAsync(() => host.Manager.MutationWaiters == 1, "the mixed SetScene to wait for the screen gate");
        set.IsCompleted.Should().BeFalse();
        proceed.SetResult();

        await delete.WaitAsync(TimeSpan.FromSeconds(30));
        await FluentActions.Awaiting(Wait.Outcome(set)).Should().ThrowAsync<RemoteInvocationException>().WithMessage($"*{asset.Id}*");
        var now = await c.Rgb.GetSceneAsync();
        now.Assignments.Single().Colors.Should().Equal(["#FF0000"], "the RGB half was never applied");
        now.Screens.Should().BeEquivalentTo(keep);
        (await c.Screens.ListScreenAssetsAsync()).Should().BeEmpty();
        host.Manager.MutationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_and_cancelled_mixed_changes_free_the_gate_and_rgb_only_changes_never_wait()
    {
        await using var host = await ScreensTestHost.StartAsync();
        await using var c = await host.ConnectAsync();
        var keep = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#111111")]);
        var other = new ScreenSceneDto([new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#222222")]);
        await c.Rgb.SetSceneAsync(RedFan with { Screens = keep });

        // invalid RGB half: the validated screen half is dropped and the gate released
        var badRgb = new SceneDto([new AssignmentDto([new ZoneRefDto("virtual:nope", 0)], "StaticColor", [])], other);
        await FluentActions.Awaiting(() => c.Rgb.SetSceneAsync(badRgb)).Should().ThrowAsync<RemoteInvocationException>();
        host.Manager.MutationHeld.Should().BeFalse();
        (await c.Screens.GetScreenSceneAsync()).Should().BeEquivalentTo(keep);

        // cancelled while waiting for the gate
        var holder = await host.Manager.BeginSceneAsync(keep).WaitAsync(TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource();
        var set = c.Rgb.SetSceneAsync(BlueFan with { Screens = other }, cancel.Token);
        await Wait.UntilAsync(() => host.Manager.MutationWaiters == 1, "the mixed SetScene to wait for the screen gate");

        // an RGB-only change (Screens null) and reads complete while the gate is held
        await c.Rgb.SetSceneAsync(RedFan).WaitAsync(TimeSpan.FromSeconds(10));
        (await c.Rgb.GetSceneAsync().WaitAsync(TimeSpan.FromSeconds(10))).Screens.Should().BeEquivalentTo(keep);

        await cancel.CancelAsync();
        await FluentActions.Awaiting(Wait.Outcome(set)).Should().ThrowAsync<OperationCanceledException>();
        await Wait.UntilAsync(() => host.Manager.MutationWaiters == 0, "the server-side wait to observe the cancellation");
        host.Manager.MutationHeld.Should().BeTrue("only the holder has the gate");
        holder.Dispose();

        host.Manager.MutationHeld.Should().BeFalse();
        var now = await c.Rgb.GetSceneAsync();
        now.Assignments.Single().Colors.Should().Equal(["#FF0000"], "the cancelled SetScene never applied its RGB half");
        now.Screens.Should().BeEquivalentTo(keep);
    }

    private static async Task<ScreenAssetDto> UploadAsync(Client c)
    {
        var png = await Images.PngAsync(Images.Solid(32, 32, 200, 100, 0));
        return await c.Screens.UploadScreenAssetAsync(new ScreenAssetUploadDto("logo", Convert.ToBase64String(png)));
    }

    /// <summary>Drops null members and rewrites numbers as doubles, so only content differences remain.</summary>
    private static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o.ToList())
                {
                    o.Remove(kv.Key);
                    if (kv.Value is not null)
                    {
                        o[kv.Key] = Canonical(kv.Value);
                    }
                }

                return o;
            case JsonArray a:
                var items = a.ToList();
                a.Clear();
                foreach (var item in items)
                {
                    a.Add(Canonical(item));
                }

                return a;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                return JsonValue.Create(v.GetValue<double>());
            default:
                return node;
        }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "src", "UnifiedRgb.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root (src/UnifiedRgb.sln) not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task Restart_rpc_uses_the_host_lianli_recovery_and_refuses_without_a_discovered_controller()
    {
        await using var host = await ScreensTestHost.StartAsync(o =>
        {
            o.Screens.Enabled = true; o.Screens.TlLcd.Output = true;
            o.Screens.FanLcdRestart = new FanLcdRestartOptions
            {
            Enabled = true,
            ControllerId = "lianli:rf:35-F7-EF-72-AB-3C",
            ScreenIds = [Devices.Tl1.Id],
            };
        }, null, Devices.Tl1, Devices.H2);
        host.Services.GetRequiredService<IFanLcdGroupRecovery>().Should().BeOfType<LianLiFanLcdRecovery>();
        await using var c = await host.ConnectAsync();
        var r = await c.Screens.RestartFanLcdAsync();
        (r.CommandAccepted, r.ScreensReconnected, r.Attempts).Should().Be((false, false, 0));
        r.Message.Should().StartWith("Precheck:").And.Contain("is not a discovered controller").And.Contain("Nothing was sent");
        r.ScreenIds.Should().Equal(Devices.Tl1.Id);
        host.Opener.Opens.Should().Be(0);
        (await c.Rgb.GetSceneAsync()).Should().NotBeNull("RGB stays responsive");
    }

    [Fact]
    public async Task Restart_rpc_round_trips_the_dto_and_default_config_is_disabled()
    {
        await using (var host = await ScreensTestHost.StartAsync(Devices.Tl1))
        {
            await using var c = await host.ConnectAsync();
            var off = await c.Screens.RestartFanLcdAsync();
            off.Message.Should().StartWith("Disabled:");
            off.ScreenIds.Should().BeEmpty();
        }

        var recovery = new AcceptingRecovery();
        await using var configured = await ScreensTestHost.StartAsync(o =>
        {
            o.Screens.Enabled = true; o.Screens.TlLcd.Output = true;
            o.Screens.FanLcdRestart = new FanLcdRestartOptions
            {
            Enabled = true,
            ControllerId = "lianli:rf:35-F7-EF-72-AB-3C",
            ScreenIds = [Devices.Tl1.Id, Devices.Tl2.Id],
            };
        }, s => s.AddSingleton<IFanLcdGroupRecovery>(recovery), Devices.Tl1, Devices.Tl2, Devices.H2);
        await using var client = await configured.ConnectAsync();
        var raw = await client.Rpc.InvokeWithCancellationAsync<JsonElement>("RestartFanLcd", [], CancellationToken.None);
        raw.GetProperty("commandAccepted").GetBoolean().Should().BeTrue();
        raw.GetProperty("screensReconnected").GetBoolean().Should().BeTrue("no content is assigned: enumeration only");
        raw.GetProperty("attempts").GetInt32().Should().Be(1);
        raw.GetProperty("screenIds").EnumerateArray().Select(e => e.GetString()).Should().Equal(Devices.Tl1.Id, Devices.Tl2.Id);
        raw.GetProperty("message").GetString().Should().StartWith("Restarted:").And.Contain("enumeration only");
        recovery.Calls.Should().Be(1);
        (await client.Screens.RestartFanLcdAsync()).Message.Should().StartWith("Cooldown:");
        recovery.Calls.Should().Be(1);
        var dto = new ScreenRestartDto(true, false, 2, ["screen:tl-lcd:a"], "Partial: x");
        var json = JsonSerializer.Serialize(dto, ContractsJsonContext.Default.ScreenRestartDto);
        JsonSerializer.Deserialize(json, ContractsJsonContext.Default.ScreenRestartDto).Should().BeEquivalentTo(dto);
    }

    [Fact]
    public async Task A_client_without_the_restart_method_still_works()
    {
        await using var host = await ScreensTestHost.StartAsync(Devices.H2);
        var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var legacy = rpc.Attach<ILegacyScreenService>();
        rpc.StartListening();
        (await legacy.ListScreensAsync()).Should().ContainSingle();
        await legacy.StopScreensAsync();
        await pipe.DisposeAsync();
    }

    /// <summary>The screen RPC surface as it was before RestartFanLcdAsync (subset).</summary>
    public interface ILegacyScreenService
    {
        Task<IReadOnlyList<ScreenDeviceDto>> ListScreensAsync(CancellationToken ct = default);

        Task StopScreensAsync(CancellationToken ct = default);
    }

    private sealed class AcceptingRecovery : IFanLcdGroupRecovery
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Validate(string controllerId, int lcdFans)
        {
        }

        public Task<FanLcdGroupResetResult> ResetAsync(string controllerId, int lcdFans, Action beforeFirstSend, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            beforeFirstSend();
            return Task.FromResult(new FanLcdGroupResetResult(true, 1, "receiver reported command sequence 2"));
        }
    }

    private sealed class ScreensTestHost : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly UnifiedRgbOptions _options;

        private ScreensTestHost(IHost host, UnifiedRgbOptions options, FakeOpener opener)
        {
            _host = host;
            _options = options;
            Opener = opener;
        }

        public FakeOpener Opener { get; }

        public string DataDirectory => _options.ResolvedDataDirectory;

        public string PipeName => _options.PipeName;

        public IServiceProvider Services => _host.Services;

        public ScreenManager Manager => _host.Services.GetRequiredService<ScreenManager>();

        public static Task<ScreensTestHost> StartAsync(params ScreenDeviceInfo[] devices) => StartAsync(null, devices);

        /// <summary><paramref name="services"/> runs after the Host composition, so its registrations win.</summary>
        public static Task<ScreensTestHost> StartAsync(Action<IServiceCollection>? services, params ScreenDeviceInfo[] devices) =>
            StartAsync(null, services, devices);

        /// <summary><paramref name="configure"/> seeds the Host options before composition.</summary>
        public static async Task<ScreensTestHost> StartAsync(Action<UnifiedRgbOptions>? configure, Action<IServiceCollection>? services, params ScreenDeviceInfo[] devices)
        {
            var options = new UnifiedRgbOptions
            {
                DataDirectory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N")),
                PipeName = "UnifiedRgb-screens-test-" + Guid.NewGuid().ToString("N"),
                UseVirtualAdapter = true,
            };
            options.Screens.NvmlTelemetry = false;
            configure?.Invoke(options);
            var opener = new FakeOpener();
            var platform = new ScreenPlatform(new FakeEnumerator(devices), opener, new FakeContention(), new FakeOs(), null, TimeProvider.System);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddUnifiedRgb(options);
            builder.Services.AddSingleton<IServiceControl, RefusingServiceControl>(); // never the real SCM
            builder.Services.AddSingleton(platform); // never real USB discovery
            services?.Invoke(builder.Services);
            var host = builder.Build();
            await host.StartAsync();
            await host.Services.GetRequiredService<PipeServer>().Listening.WaitAsync(TimeSpan.FromSeconds(10));
            return new ScreensTestHost(host, options, opener);
        }

        public async Task<Client> ConnectAsync()
        {
            var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
            var c = new Client(rpc, rpc.Attach<IRgbService>(), rpc.Attach<IScreenService>(), pipe);
            rpc.StartListening();
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private sealed record Client(JsonRpc Rpc, IRgbService Rgb, IScreenService Screens, NamedPipeClientStream Pipe) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Rpc.Dispose();
            await Pipe.DisposeAsync();
        }
    }

    /// <summary>
    /// Wraps the Host's real screen binding. An armed BeginSceneAsync pauses after the inner begin (gate held, screen half
    /// validated, RGB half not applied) until released; every commit records the gate, its waiters and the live RGB colour.
    /// </summary>
    private sealed class PausingBinding(ScreenManager manager, LightingEngine engine) : IScreenSceneBinding
    {
        private TaskCompletionSource? _paused;
        private TaskCompletionSource? _release;

        public ConcurrentQueue<(bool GateHeld, int Waiters, string RgbColor)> Commits { get; } = new();

        public static void Register(IServiceCollection services)
        {
            services.AddSingleton(sp => new PausingBinding(sp.GetRequiredService<ScreenManager>(), sp.GetRequiredService<LightingEngine>()));
            services.AddSingleton<IScreenSceneBinding>(sp => sp.GetRequiredService<PausingBinding>());
        }

        public (Task Paused, TaskCompletionSource Release) Arm()
        {
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return (_paused.Task, _release);
        }

        public ScreenSceneDto? CurrentForScene() => manager.CurrentForScene();

        public void Validate(ScreenSceneDto scene) => manager.Validate(scene);

        public async Task<IScreenSceneTransaction> BeginSceneAsync(ScreenSceneDto scene, CancellationToken ct = default)
        {
            var transaction = await manager.BeginSceneAsync(scene, ct);
            if (Interlocked.Exchange(ref _paused, null) is { } paused)
            {
                paused.SetResult();
                try
                {
                    await _release!.Task.WaitAsync(ct);
                }
                catch
                {
                    transaction.Dispose();
                    throw;
                }
            }

            return new Recording(transaction, () => Commits.Enqueue((manager.MutationHeld, manager.MutationWaiters,
                DtoMapper.ToDto(engine.Loop.Scene).Assignments.Single().Colors[0])));
        }

        private sealed class Recording(IScreenSceneTransaction inner, Action record) : IScreenSceneTransaction
        {
            public void Commit()
            {
                record();
                inner.Commit();
            }

            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class RefusingServiceControl : IServiceControl
    {
        public ServiceInfo? Query(string name) => throw new InvalidOperationException("SCM access in a test");

        public void Stop(string name, TimeSpan timeout) => throw new InvalidOperationException("SCM access in a test");

        public void Start(string name, TimeSpan timeout) => throw new InvalidOperationException("SCM access in a test");

        public void SetStartType(string name, string startType) => throw new InvalidOperationException("SCM access in a test");
    }
}
