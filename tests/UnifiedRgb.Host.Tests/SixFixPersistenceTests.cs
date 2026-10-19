using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class SixFixPersistenceTests
{
    [Fact]
    public async Task Lease_is_exclusive_across_async_threads_and_released_on_dispose()
    {
        var name = "Local\\UnifiedRgb-test-" + Guid.NewGuid().ToString("N");
        using (var first = new HardwareHostLease(name))
        {
            var acquire = () => new HardwareHostLease(name);
            acquire.Should().Throw<InvalidOperationException>();
            await Task.Run(first.Dispose);
        }
        using var next = new HardwareHostLease(name);
    }

    [Fact]
    public async Task Pipe_reservation_refuses_start_before_a_fake_hardware_service()
    {
        var name = "UnifiedRgb-test-collision-" + Guid.NewGuid().ToString("N");
        using var occupied = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var server = new PipeServer(new UnifiedRgbOptions { PipeName = name }, null!, NullLogger<PipeServer>.Instance);
        var sentinel = new HardwareSentinel();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(server);
        builder.Services.AddHostedService<PipeReservation>();
        builder.Services.AddSingleton<IHostedService>(sentinel);
        using var host = builder.Build();
        var start = async () => await host.StartAsync();
        await start.Should().ThrowAsync<Exception>();
        sentinel.Opened.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Oversized_profile_is_refused_before_replacing_previous_bytes(bool active)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-size-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            var old = new ProfileDto("Saved", new SceneDto([]));
            if (active) store.SaveActive(old); else store.Save(old);
            var path = Path.Combine(dir, active ? "active.json" : "Saved.json");
            var before = File.ReadAllBytes(path);
            var huge = old with { Scene = new SceneDto([new([new("virtual:test", 0)], "StaticColor", ["#FF0000"], GroupName: new string('界', (int)ProfileStore.MaxFileBytes))]) };
            _ = DtoMapper.FromDto(huge.Scene);
            var write = () => { if (active) store.SaveActive(huge); else store.Save(huge); };
            write.Should().Throw<ArgumentException>();
            File.ReadAllBytes(path).Should().Equal(before);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Rgb_persistence_failure_keeps_the_live_plan_and_profile_name()
    {
        await using var host = await TestHost.StartAsync();
        var before = host.Engine.Loop.Scene;
        var name = host.Engine.ActiveProfileName;
        host.Get<ProfileStore>().BeforeActiveCommit = () => throw new IOException("injected active save failure");
        var apply = () => host.Engine.SetScene(before);
        apply.Should().Throw<IOException>();
        host.Engine.Loop.Scene.Should().BeSameAs(before);
        host.Engine.ActiveProfileName.Should().Be(name);
    }

    [Fact]
    public async Task Joint_screen_write_failure_does_not_publish_rgb_or_screens()
    {
        await using var host = await TestHost.StartAsync();
        var engine = host.Engine;
        var before = engine.Loop.Scene;
        var target = Path.Combine(host.Options.ResolvedDataDirectory, "screens", "scene.json");
        Directory.CreateDirectory(target);
        var scene = DtoMapper.ToDto(before) with { Screens = new ScreenSceneDto([new("screen:turzx:test", Overlay: "none")]) };
        var apply = async () => await host.Get<IRgbService>().SetSceneAsync(scene);
        await apply.Should().ThrowAsync<IOException>();
        engine.Loop.Scene.Should().BeSameAs(before);
        (await host.Get<ScreenManager>().GetScreenSceneAsync()).Assignments.Should().BeEmpty();
    }

    [Fact]
    public async Task Joint_scene_restarts_with_the_same_rgb_screens_and_local_direction()
    {
        await using var host = await TestHost.StartAsync();
        var manager = host.Get<ScreenManager>();
        await manager.SetScreenDeviceSettingsAsync(new("screen:turzx:test", 90));
        var settingsPath = Path.Combine(host.Options.ResolvedDataDirectory, "screens", "device-settings.json");
        var settingsBefore = File.ReadAllBytes(settingsPath);
        var scene = DtoMapper.ToDto(host.Engine.Loop.Scene) with { Screens = new ScreenSceneDto([new("screen:turzx:test", BackgroundColor: "#123456", Overlay: "none")]) };
        await host.Get<IRgbService>().SetSceneAsync(scene);
        File.ReadAllBytes(settingsPath).Should().Equal(settingsBefore);
        File.Exists(Path.Combine(host.Options.ResolvedDataDirectory, SceneStateFiles.JournalName)).Should().BeFalse();
        await using var restored = await TestHost.StartAsync(seed: options =>
        {
            foreach (var relative in new[] { "profiles/active.json", "screens/scene.json", "screens/device-settings.json" })
            {
                var target = Path.Combine(options.ResolvedDataDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(host.Options.ResolvedDataDirectory, relative), target);
            }
        });
        var readBack = await restored.Get<IRgbService>().GetSceneAsync();
        readBack.Assignments.Should().BeEquivalentTo(scene.Assignments, o => o.WithStrictOrdering());
        readBack.Screens!.Assignments.Should().ContainSingle().Which.Rotation.Should().Be(90);
        readBack.Screens.Assignments[0].BackgroundColor.Should().Be("#123456");
        File.ReadAllBytes(Path.Combine(restored.Options.ResolvedDataDirectory, "screens", "device-settings.json")).Should().Equal(settingsBefore);
    }

    private sealed class HardwareSentinel : IHostedService
    {
        public bool Opened { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Opened = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
