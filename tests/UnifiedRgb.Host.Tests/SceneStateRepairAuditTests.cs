using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class SceneStateRepairAuditTests
{
    [Fact]
    public async Task Corrupt_journal_starts_a_diagnostic_only_host_and_requires_a_complete_scene_before_restart()
    {
        byte[]? previous = null;
        var tripwire = new TripwireFactory();
        await using var host = await TestHost.StartAsync(configure: services => services.AddSingleton<IDeviceAdapterFactory>(tripwire), seed: options =>
        {
            Directory.CreateDirectory(Path.Combine(options.ResolvedDataDirectory, "profiles"));
            var path = Path.Combine(options.ResolvedDataDirectory, "profiles", "active.json");
            previous = "{ partially published rgb"u8.ToArray();
            File.WriteAllBytes(path, previous);
            File.WriteAllText(Path.Combine(options.ResolvedDataDirectory, SceneStateFiles.JournalName), "{ corrupt journal");
        });
        host.Engine.Adapters.Controllers.Should().BeEmpty("even virtual startup output is withheld in repair mode");
        tripwire.Created.Should().BeFalse("repair mode must not even construct a native transport factory product");
        host.Engine.BuildStatus(false).BlockedContent.Should().Contain(SceneStateFiles.RecoveryWarning);
        (await host.Get<ScreenManager>().GetScreenSceneAsync()).Assignments.Should().BeEmpty();
        File.ReadAllBytes(Path.Combine(host.Options.ResolvedDataDirectory, "profiles", "active.json")).Should().Equal(previous!);
        File.Exists(Path.Combine(host.Options.ResolvedDataDirectory, "screens", "device-settings.json")).Should().BeFalse("startup repair does not seed or reset local device settings");
        var saved = new ProfileDto("Recovery choice", new SceneDto([], Screens: new ScreenSceneDto([])));
        host.Get<ProfileStore>().Save(saved);
        await host.Get<IRgbService>().ApplyProfileAsync(saved.Name);
        File.Exists(Path.Combine(host.Options.ResolvedDataDirectory, SceneStateFiles.RecoveryRequiredName)).Should().BeFalse();
        host.Options.Screens.OutputAllowed(UnifiedRgb.Screens.ScreenFamily.Turzx).Should().BeFalse();
        host.Engine.BuildStatus(false).BlockedContent.Should().Contain(SceneStateFiles.RecoveryWarning, "a repaired process remains output-disabled until a deliberate restart");
    }

    private sealed class TripwireFactory : IDeviceAdapterFactory
    {
        public bool Created { get; private set; }
        public DeviceClass Class => DeviceClass.Virtual;
        public IDeviceAdapter Create(IServiceProvider services) { Created = true; throw new InvalidOperationException("No adapter may be constructed during repair."); }
    }
}
