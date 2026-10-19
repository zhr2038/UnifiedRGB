using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// A per-connection view of a service (NATIVE design §6): the pipe server gives every client connection its own scope, so
/// state owned by a connection (bring-up sessions) is tied to it and ends when the scope is disposed.
/// </summary>
public interface IConnectionScopedService
{
    IConnectionScope ForConnection(string owner);
}

/// <summary>The service a connection talks to; disposing it is that connection's disconnect.</summary>
public interface IConnectionScope : IAsyncDisposable
{
    IRgbService Service { get; }
}

/// <summary>
/// JSON-RPC target (ARCHITECTURE §6). Thin: validation lives in DtoMapper, ProfileStore, RenderLoop and WriterGuard.
/// <paramref name="owner"/> identifies the connection for bring-up sessions; the DI singleton is <see cref="LocalOwner"/> and
/// the pipe server uses <see cref="ForConnection"/> for each client. <paramref name="screens"/> carries the optional
/// <see cref="SceneDto.Screens"/>: null there means "screens unchanged"; a non-null screen scene is validated together with
/// the RGB candidate before either is activated, and applied after the RGB scene.
/// </summary>
public sealed class RgbService(
    LightingEngine engine, ProfileStore profiles, IWriterGuard guard, RgbNotifier notifier, string owner, IScreenSceneBinding? screens = null)
    : IRgbService, IConnectionScopedService
{
    public const string LocalOwner = "local";

    public RgbService(LightingEngine engine, ProfileStore profiles, IWriterGuard guard, RgbNotifier notifier)
        : this(engine, profiles, guard, notifier, LocalOwner)
    {
    }

    public string Owner => owner;

    public event EventHandler<IReadOnlyList<ControllerDto>>? ControllersChanged
    {
        add => notifier.ControllersChanged += value;
        remove => notifier.ControllersChanged -= value;
    }

    public event EventHandler<StatusDto>? StatusChanged
    {
        add => notifier.StatusChanged += value;
        remove => notifier.StatusChanged -= value;
    }

    public event EventHandler<IReadOnlyList<TelemetryDto>>? TelemetryTick
    {
        add => notifier.TelemetryTick += value;
        remove => notifier.TelemetryTick -= value;
    }

    public Task<IReadOnlyList<ControllerDto>> ListControllersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ControllerDto>>(Controllers());

    public Task<IReadOnlyList<EffectDto>> ListEffectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EffectDto>>(engine.Effects.All.Select(DtoMapper.ToDto).ToArray());

    public Task<SceneDto> GetSceneAsync(CancellationToken ct = default)
    {
        var scene = DtoMapper.ToDto(engine.Loop.Scene);
        return Task.FromResult(screens?.CurrentForScene() is { } current ? scene with { Screens = current } : scene);
    }

    public Task SetSceneAsync(SceneDto scene, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Screens is not { } screenScene)
        {
            engine.SetScene(DtoMapper.FromDto(scene)); // RGB only: unchanged, never waits for the screen gate
            return Task.CompletedTask;
        }

        return SetSceneWithScreensAsync(scene, screenScene, ct);
    }

    public Task SetAssignmentAsync(AssignmentDto assignment, CancellationToken ct = default)
    {
        engine.SetAssignment(DtoMapper.FromDto(assignment));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProfileInfoDto>> ListProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult(profiles.List());

    public Task SaveProfileAsync(ProfileDto profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = DtoMapper.FromDto(profile.Scene); // reject malformed scenes before they reach disk
        ValidateScreens(profile.Scene?.Screens);
        profiles.Save(profile with { SchemaVersion = ProfileDto.CurrentSchema });
        return Task.CompletedTask;
    }

    public Task<ProfileDto> LoadProfileAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(profiles.Load(name));

    public Task ApplyProfileAsync(string name, CancellationToken ct = default)
    {
        var profile = profiles.Load(name);
        if (profile.Scene?.Screens is not { } screenScene)
        {
            engine.ApplyProfile(profile); // RGB only: unchanged, never waits for the screen gate
            return Task.CompletedTask;
        }

        return ApplyProfileWithScreensAsync(profile, screenScene, ct);
    }

    public Task<StatusDto> GetStatusAsync(bool diagnostics = false, CancellationToken ct = default) =>
        Task.FromResult(engine.BuildStatus(diagnostics));

    public Task<IReadOnlyList<TelemetryDto>> GetTelemetryAsync(CancellationToken ct = default) =>
        Task.FromResult(engine.ReadTelemetry());

    public async Task<TakeoverDto> SetTakeoverAsync(string deviceClass, bool enabled, CancellationToken ct = default)
    {
        var cls = ParseHardwareClass(deviceClass);
        var r = enabled
            ? await guard.SetTakeoverAsync(cls, true, ct).ConfigureAwait(false)
            : await engine.SetTakeoverOffAsync(cls, ct).ConfigureAwait(false); // sessions end and native writers close before any restore
        await engine.RecheckAsync(ct).ConfigureAwait(false);
        return new TakeoverDto(cls.ToString(), r.Enabled, r.StoppedServices, r.ProcessesToClose, r.Error);
    }

    public async Task<IReadOnlyList<ControllerDto>> RescanAsync(CancellationToken ct = default)
    {
        await engine.RescanAsync(ct).ConfigureAwait(false);
        return Controllers();
    }

    public Task<int> SaveLianLiConfigAsync(CancellationToken ct = default) => engine.SaveLianLiConfigAsync(ct);

    public Task<int> ResyncLianLiAsync(CancellationToken ct = default) => Task.FromResult(engine.ResyncLoops());

    public Task<IReadOnlyList<MountingDto>> ListMountingsAsync(CancellationToken ct = default) =>
        Task.FromResult(engine.ListMountings());

    public Task<MountingDto> SetMountingAsync(MountingDto mounting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mounting);
        return Task.FromResult(engine.SetMounting(mounting.ControllerId, mounting with { Fans = mounting.Fans ?? [] }));
    }

    public Task<MountingDto> ResetMountingAsync(string controllerId, CancellationToken ct = default) =>
        Task.FromResult(engine.SetMounting(controllerId, null));

    public Task<MountingDto> BeginRingCalibrationAsync(RingCalibrationDto calibration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return Task.FromResult(engine.BeginRingCalibration(calibration));
    }

    public Task<MountingDto> EndRingCalibrationAsync(string controllerId, bool apply, CancellationToken ct = default) =>
        Task.FromResult(engine.EndRingCalibration(controllerId, apply));

    public async Task<BringupSessionDto> BeginBringupAsync(string controllerId, int leaseSeconds, CancellationToken ct = default)
    {
        var s = await engine.Bringup.BeginAsync(owner, controllerId, leaseSeconds, ct).ConfigureAwait(false);
        notifier.PublishControllers(Controllers());
        return s;
    }

    public Task<ProbeResultDto> RunNativeProbeAsync(string controllerId, string probe, CancellationToken ct = default) =>
        AfterStepAsync(() => engine.Bringup.RunProbeAsync(owner, controllerId, probe, ct));

    public Task<ProbeResultDto> BeginMarkerAsync(MarkerDto marker, CancellationToken ct = default) =>
        AfterStepAsync(() => engine.Bringup.BeginMarkerAsync(owner, marker, ct));

    public Task<ProbeResultDto> EndMarkerAsync(string controllerId, CancellationToken ct = default) =>
        AfterStepAsync(() => engine.Bringup.EndMarkerAsync(owner, controllerId, ct));

    public Task<BringupSessionDto> RenewBringupAsync(string controllerId, CancellationToken ct = default) =>
        engine.Bringup.RenewAsync(owner, controllerId);

    public async Task<BringupEndDto> EndBringupAsync(string controllerId, CancellationToken ct = default)
    {
        var r = await engine.Bringup.EndAsync(owner, controllerId).ConfigureAwait(false);
        if (!r.AlreadyEnded)
        {
            notifier.PublishControllers(Controllers());
        }

        return r;
    }

    public Task<PlaybackCalibrationDto> GetPlaybackCalibrationAsync(CancellationToken ct = default) =>
        Task.FromResult(DtoMapper.ToDto(engine.PlaybackCalibration));

    public Task<PlaybackCalibrationDto> SetPlaybackCalibrationAsync(PlaybackCalibrationDto calibration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return Task.FromResult(DtoMapper.ToDto(engine.SetPlaybackCalibration(DtoMapper.FromDto(calibration))));
    }

    /// <summary>One scope per pipe connection; its disposal ends that connection's bring-up sessions only.</summary>
    public IConnectionScope ForConnection(string owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        return new ConnectionScope(new RgbService(engine, profiles, guard, notifier, owner, screens), () => engine.Bringup.DisconnectAsync(owner));
    }

    /// <summary>
    /// One serialized mixed change: the screen transaction validates the screen candidate (assets included) and holds the screen
    /// mutation gate across RGB validation/commit and the screen commit, so no asset delete can interleave. An RGB failure,
    /// cancellation or exception disposes the transaction uncommitted: the screen scene is unchanged and the gate released.
    /// </summary>
    private async Task SetSceneWithScreensAsync(SceneDto scene, ScreenSceneDto screenScene, CancellationToken ct)
    {
        using var transaction = await BeginScreensAsync(screenScene, ct).ConfigureAwait(false);
        engine.SetScene(DtoMapper.FromDto(scene));
        transaction.Commit();
    }

    /// <inheritdoc cref="SetSceneWithScreensAsync"/>
    private async Task ApplyProfileWithScreensAsync(ProfileDto profile, ScreenSceneDto screenScene, CancellationToken ct)
    {
        using var transaction = await BeginScreensAsync(screenScene, ct).ConfigureAwait(false);
        engine.ApplyProfile(profile);
        transaction.Commit();
    }

    private Task<IScreenSceneTransaction> BeginScreensAsync(ScreenSceneDto scene, CancellationToken ct) =>
        screens is null
            ? throw new InvalidOperationException("This Host has no screen subsystem; send Screens = null.")
            : screens.BeginSceneAsync(scene, ct);

    /// <summary>Validates a non-null screen scene (SaveProfile; not serialized, see IScreenSceneBinding.Validate); null passes.</summary>
    private ScreenSceneDto? ValidateScreens(ScreenSceneDto? scene)
    {
        if (scene is null)
        {
            return null;
        }

        if (screens is null)
        {
            throw new InvalidOperationException("This Host has no screen subsystem; send Screens = null.");
        }

        screens.Validate(scene);
        return scene;
    }

    /// <summary>Names only ("LianLi", "MsiGpu", "OpenRgb", "AsusAura", "EneDram"); numbers and Virtual are rejected.</summary>
    public static DeviceClass ParseHardwareClass(string deviceClass)
    {
        if (string.IsNullOrWhiteSpace(deviceClass) || !deviceClass.All(char.IsAsciiLetter)
            || !Enum.TryParse<DeviceClass>(deviceClass, ignoreCase: true, out var cls) || !Enum.IsDefined(cls) || cls == DeviceClass.Virtual)
        {
            throw new ArgumentException($"'{deviceClass}' is not a hardware device class.", nameof(deviceClass));
        }

        return cls;
    }

    private ControllerDto[] Controllers() => engine.Adapters.Controllers.Select(DtoMapper.ToDto).ToArray();

    private async Task<ProbeResultDto> AfterStepAsync(Func<Task<ProbeResultDto>> step)
    {
        var r = await step().ConfigureAwait(false);
        if (r.SessionEnded)
        {
            notifier.PublishControllers(Controllers());
        }

        return r;
    }

    private sealed class ConnectionScope(IRgbService service, Func<Task> disconnect) : IConnectionScope
    {
        private int _disposed;

        public IRgbService Service => service;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await disconnect().ConfigureAwait(false);
            }
        }
    }
}
