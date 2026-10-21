using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusAura;
using UnifiedRgb.Devices.EneDram;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Devices.OpenRgbSdk;
using UnifiedRgb.Devices.Virtual;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.ScenePackages;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Host.Timing;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host;

/// <summary>DI wiring shared by Program and the Host tests.</summary>
public static class HostComposition
{
    public static IServiceCollection AddUnifiedRgb(this IServiceCollection services, UnifiedRgbOptions options, bool enableIpc = true)
    {
        services.AddSingleton(options);
        services.AddSingleton<ISystemSnapshot, WindowsSystemSnapshot>();
        services.AddSingleton(sp => new ProcessScanner(sp.GetRequiredService<ISystemSnapshot>()));
        services.AddSingleton<IServiceControl, WindowsServiceControl>();
        services.AddSingleton(sp => new TakeoverStore(sp.GetRequiredService<UnifiedRgbOptions>()));
        services.AddSingleton<IWriterGuard>(sp => new WriterGuard(
            sp.GetRequiredService<ProcessScanner>(), sp.GetRequiredService<IServiceControl>(), sp.GetRequiredService<TakeoverStore>()));
        services.AddSingleton(sp => new ProfileStore(sp.GetRequiredService<UnifiedRgbOptions>()));
        services.AddSingleton(sp => new MountingStore(sp.GetRequiredService<UnifiedRgbOptions>()));
        services.AddSingleton(sp => new NativeMapStore(sp.GetRequiredService<UnifiedRgbOptions>(), sp.GetService<ILogger<NativeMapStore>>()));
        services.AddSingleton<INativeMapSource>(sp => sp.GetRequiredService<NativeMapStore>());
        services.AddSingleton<RgbNotifier>();

        // OPUS-GENERIC-SCENE-PACKAGES: the durable package frame source exists before LightingEngine applies active.json
        services.AddSingleton(sp => new ScenePackageStore(sp.GetRequiredService<UnifiedRgbOptions>(), sp.GetService<ILogger<ScenePackageStore>>()));
        services.AddSingleton(sp => new ScenePackageBindingStore(sp.GetRequiredService<ScenePackageStore>()));
        services.AddSingleton(sp => new ScenePackageFrameSource(sp.GetRequiredService<ScenePackageStore>(), sp.GetRequiredService<ScenePackageBindingStore>()));
        services.AddSingleton<IFrameSetSource>(sp => sp.GetRequiredService<ScenePackageFrameSource>());

        if (options.UseVirtualAdapter)
        {
            services.AddSingleton<IDeviceAdapterFactory, VirtualAdapterFactory>();
        }

        if (options.EnableMsiGpu)
        {
            // overnight addendum §4: the hold-black option lives under Bringup and is copied, never configured per class
            options.MsiGpu = options.MsiGpu with { HoldBlackOnRelease = options.Bringup.HoldBlackOnRelease };
            services.AddSingleton(options.MsiGpu);
            services.AddSingleton<IDeviceAdapterFactory, MsiGpuAdapterFactory>();
        }

        if (options.EnableAsusAura)
        {
            // Aura's release is unchanged under hold-black (nothing is sent); the copy only restricts markers to black
            options.AsusAura = options.AsusAura with { HoldBlackOnRelease = options.Bringup.HoldBlackOnRelease };
            services.AddSingleton(options.AsusAura);
            services.AddSingleton<IDeviceAdapterFactory, AsusAuraAdapterFactory>();
        }

        if (options.EnableEneDram)
        {
            // under hold-black ENE skips direct off at release/close/stop and accepts black markers only
            options.EneDram = options.EneDram with { HoldBlackOnRelease = options.Bringup.HoldBlackOnRelease };
            services.AddSingleton(options.EneDram);
            services.AddSingleton<IDeviceAdapterFactory, EneDramAdapterFactory>();
        }

        if (options.EnableOpenRgb)
        {
            services.AddSingleton(options.OpenRgb);
            services.AddSingleton<IDeviceAdapterFactory, OpenRgbSdkAdapterFactory>();
        }

        if (options.EnableLianLi)
        {
            services.AddSingleton(options.LianLi);
            services.AddSingleton<IDeviceAdapterFactory, LianLiAdapterFactory>();
        }

        services.AddSingleton(new Func<IPreciseSleeper>(WaitableTimerSleeper.Create));
        services.AddSingleton<IClock>(sp => new MasterClock(sp.GetRequiredService<Func<IPreciseSleeper>>()()));
        services.AddSingleton(EffectRegistry.Default);
        services.AddSingleton<FrameBus>();
        services.AddSingleton(sp => new AdapterHost(
            sp.GetServices<IDeviceAdapterFactory>(),
            sp,
            sp.GetRequiredService<IWriterGuard>(),
            sp.GetRequiredService<FrameBus>(),
            sp.GetRequiredService<Func<IPreciseSleeper>>(),
            sp.GetRequiredService<ILogger<AdapterHost>>()));
        if (enableIpc)
        {
            services.AddHostedService<PipeReservation>();
        }

        services.AddSingleton<LightingEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<LightingEngine>());
        services.AddSingleton<GuardMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<GuardMonitor>());
        services.AddSingleton<StatusWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<StatusWriter>());

        // screens: an independent subsystem (no RGB engine/writer dependency); output stays off unless configured
        services.AddSingleton(sp => ScreenPlatform.Windows(sp.GetRequiredService<UnifiedRgbOptions>().Screens));
        services.AddSingleton<IFanLcdGroupRecovery>(sp => new LianLiFanLcdRecovery(sp.GetRequiredService<AdapterHost>()));
        services.AddSingleton(sp => new ScreenManager(
            options.Screens, options.ResolvedDataDirectory, sp.GetRequiredService<ScreenPlatform>(), sp.GetService<ILoggerFactory>(),
            sp.GetService<IFanLcdGroupRecovery>(), () => WholeCaseBlackout.IsRequested(sp.GetRequiredService<LightingEngine>().Loop.Scene)));
        services.AddSingleton<IScreenService>(sp => sp.GetRequiredService<ScreenManager>());
        services.AddSingleton<IScreenSceneBinding>(sp => sp.GetRequiredService<ScreenManager>());
        services.AddSingleton<IScreenPackageMedia>(sp => sp.GetRequiredService<ScreenManager>());
        services.AddSingleton<IScreenDeviceSettingsService>(sp => sp.GetRequiredService<ScreenManager>());
        services.AddSingleton<IScreenPerformanceService>(sp => sp.GetRequiredService<ScreenManager>());
        services.AddSingleton<IScreenVideoService>(sp => sp.GetRequiredService<ScreenManager>());

        services.AddSingleton(sp =>
        {
            var engine = sp.GetRequiredService<LightingEngine>();
            return new ScenePackageTimeline(sp.GetRequiredService<ScenePackageFrameSource>(), id => engine.Loop.Timeline(id));
        });
        services.AddSingleton(sp => new ScenePackageService(
            sp.GetRequiredService<ScenePackageFrameSource>(),
            sp.GetRequiredService<ScenePackageTimeline>(),
            sp.GetRequiredService<LightingEngine>(),
            sp.GetRequiredService<ILogger<ScenePackageService>>(),
            sp.GetService<IScreenPackageMedia>(),
            sp.GetService<IScreenService>(),
            sp.GetService<IScreenSceneBinding>(), sp.GetRequiredService<ProfileStore>()));
        services.AddSingleton<IScenePackageService>(sp => sp.GetRequiredService<ScenePackageService>());
        services.AddSingleton<ISceneLibraryService>(sp => sp.GetRequiredService<ScenePackageService>());
        // ORDER: after the engine restored its scene, BEFORE the screen workers start: active package media is re-verified and
        // re-installed (bounded wait) so no package screen sends a frame before its immutable media and timeline are ready
        services.AddHostedService(sp => sp.GetRequiredService<ScenePackageService>());
        services.AddHostedService(sp => new ScreenHostedService(sp.GetRequiredService<ScreenManager>()));

        services.AddSingleton<IRgbService>(sp => new RgbService(
            sp.GetRequiredService<LightingEngine>(),
            sp.GetRequiredService<ProfileStore>(),
            sp.GetRequiredService<IWriterGuard>(),
            sp.GetRequiredService<RgbNotifier>(),
            RgbService.LocalOwner,
            sp.GetService<IScreenSceneBinding>()));
        if (enableIpc)
        {
            services.AddSingleton<PipeServer>();
            services.AddHostedService(sp => sp.GetRequiredService<PipeServer>());
        }

        return services;
    }
}
