using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Telemetry;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>The OS seams of the screen subsystem (fakes in tests).</summary>
public sealed record ScreenPlatform(
    IScreenDeviceEnumerator Enumerator,
    IScreenUsbOpener Opener,
    IScreenContentionProbe Contention,
    ICpuMemoryReader Os,
    Func<(IGpuReader? Reader, string? Error)>? Gpu,
    TimeProvider Time)
{
    /// <summary>SetupAPI discovery, WinUSB output, read-only vendor process probe, OS counters and (optionally) System32 NVML.</summary>
    public static ScreenPlatform Windows(ScreensOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Func<(IGpuReader?, string?)>? gpu = options.NvmlTelemetry
            ? () =>
            {
                var reader = NvmlGpuReader.TryCreate(out var error);
                return (reader, error);
            }
            : null;
        return new ScreenPlatform(new SetupApiScreenEnumerator(), new WinUsbScreenOpener(), new VendorProcessProbe(),
            new WindowsCpuMemoryReader(), gpu, TimeProvider.System);
    }
}

/// <summary>How the RGB scene/profile RPCs carry the optional <see cref="SceneDto.Screens"/> (validate first, apply last).</summary>
public interface IScreenSceneBinding
{
    /// <summary>The current screen scene for GetScene: null when it has no assignment (so the RGB JSON stays byte-identical).</summary>
    ScreenSceneDto? CurrentForScene();

    /// <summary>
    /// Whole-candidate validation, assets included; throws ArgumentException and changes nothing. Not serialized: it is for
    /// SaveProfile, whose inactive profile may later reference a deleted asset (it then fails validation on apply).
    /// </summary>
    void Validate(ScreenSceneDto scene);

    /// <summary>
    /// Starts a serialized screen-scene change: waits (asynchronously, cancellable) for the screen mutation gate, then validates
    /// the whole candidate, assets included, while holding it. Until the transaction is committed or disposed no asset delete or
    /// other scene change can interleave, so the validated media is still there at commit. Invalid → ArgumentException with
    /// the gate already released.
    /// </summary>
    Task<IScreenSceneTransaction> BeginSceneAsync(ScreenSceneDto scene, CancellationToken ct = default);

    /// <summary>
    /// The candidate shown in each screen's saved LOCAL rotation (a per-device setting, never scene content); screens without a
    /// saved setting are unchanged. BeginSceneAsync normalizes again under the mutation gate; this lets a caller (scene packages)
    /// build the candidate it reports. The default, for implementations without local settings, returns the candidate unchanged.
    /// </summary>
    ScreenSceneDto NormalizeToLocal(ScreenSceneDto scene) => scene;
}

/// <summary>
/// A validated screen scene that holds the screen mutation gate. <see cref="Commit"/> activates it once (persist, clear a Stop
/// pause, update every worker; never throws for I/O) and releases the gate; disposing an uncommitted transaction releases the
/// gate and changes nothing. Always dispose it (a <c>using</c> covers validation/RGB failure, cancellation and exceptions).
/// </summary>
public interface IScreenSceneTransaction : IDisposable
{
    void Commit();
}

/// <summary>Production transactions expose prepared state bytes; a coordinator persists them with RGB before Publish.</summary>
public interface IPreparedScreenSceneTransaction : IScreenSceneTransaction
{
    IReadOnlyList<SceneStateFile> Files { get; }
    void Coordinate(Action commit);
    void Publish();
}

/// <summary>
/// The screen subsystem: discovery/hotplug, one worker per present display whose output gate is open, the asset store,
/// preview, telemetry and the persisted screen scene. Independent of the RGB engine and writers. With the master gate closed
/// (the default) no worker, thread or loop exists until an RPC needs one, and no device handle is ever opened.
/// </summary>
public sealed partial class ScreenManager : IScreenService, IScreenSceneBinding, IAsyncDisposable, IDisposable
{
    private const int MaxNameLength = 64;
    private const long PreviewCacheBytes = 32L * 1024 * 1024;

    private readonly ScreensOptions _options;
    private readonly string _stateRoot;
    private readonly ScreenPlatform _platform;
    private readonly ILogger _log;
    private readonly AssetStore _assets;
    private readonly ScreenSceneStore _sceneStore;
    private readonly ScreenDeviceSettingsStore _settingsStore;
    private readonly HashSet<string> _indexed8Verified;
    private readonly PcTelemetrySampler _sampler;
    private readonly ScreenWorkerContext _workerContext;
    private readonly Lazy<StaThread> _previewThread = new(() => new StaThread("screen preview"));
    private readonly Lazy<StaThread> _importThread = new(() => new StaThread("screen import"));
    private readonly CancellationTokenSource _cts = new();

    // Serializes asset-lifetime decisions: scene transactions (validate → [RGB] → commit) and asset deletes. Async, never held
    // by workers, preview or RGB render threads; _gate (a Monitor) only guards short in-memory sections and is never awaited in.
    // Deliberately not disposed: a transaction handed out before disposal still releases it.
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly object _gate = new();
    private readonly object _publicationGate = new();
    private readonly object _stopGate = new();
    private SceneTransaction? _sceneTransaction;
    private readonly Dictionary<string, ScreenWorker> _workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerRecovery> _workerRecovery = new(StringComparer.Ordinal);
    private int _mutationWaiters;
    private ScreenCompositor? _previewCompositor;
    private ScreenSceneDto? _scene;
    private bool _paused;
    private IReadOnlyList<ScreenDeviceInfo> _present = [];
    private long _presentAt;
    private bool _enumerated;
    private Task? _discoveryLoop;
    private Task? _telemetryLoop;
    private bool _disposed;
    private bool _stopped;
    private Task? _stopTask;

    /// <param name="lcdRecovery">Optional RF side of <see cref="RestartFanLcdAsync"/> (the Host's Lian Li adapter); null refuses it.</param>
    public ScreenManager(ScreensOptions options, string dataDirectory, ScreenPlatform platform, ILoggerFactory? loggers = null,
        IFanLcdGroupRecovery? lcdRecovery = null, Func<bool>? wholeCaseBlackout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(platform);
        loggers ??= NullLoggerFactory.Instance;
        _options = options;
        _stateRoot = Path.GetFullPath(dataDirectory);
        _platform = platform;
        _lcdRecovery = lcdRecovery;
        _log = loggers.CreateLogger<ScreenManager>();
        var root = Path.Combine(dataDirectory, "screens");
        _assets = new AssetStore(Path.Combine(root, "assets"), options.AssetStoreMaxBytes, options.AssetStoreMaxCount, _log);
        _sceneStore = new ScreenSceneStore(Path.Combine(root, "scene.json"), _log);
        _settingsStore = new ScreenDeviceSettingsStore(Path.Combine(root, "device-settings.json"), _log);
        _indexed8Verified = Indexed8Ids(options, _log);
        _sampler = new PcTelemetrySampler(platform.Os, platform.Gpu, platform.Time);
        _workerContext = new ScreenWorkerContext(options, platform.Opener, platform.Contention, _assets, () => _sampler.Latest, platform.Time,
            loggers.CreateLogger<ScreenWorker>())
        {
            Timeline = () => Volatile.Read(ref _timeline),
            EncodingOf = EncodingOf,
            Encode = Encode,
            TrialOf = TrialOf,
            AbortTrial = AbortTrialOf,
            WholeCaseBlackout = wholeCaseBlackout ?? (static () => false),
            BacklightStateDirectory = Path.Combine(root, "backlight"),
        };
        if (!HeadlessRendering.Enabled)
        {
            LogHeadlessSwitchMissing(_log, HeadlessRendering.SwitchName);
        }
    }

    /// <summary>Host start: with the master gate open, restores the saved scene and starts discovery and telemetry.</summary>
    public void Start()
    {
        if (!_options.Enabled)
        {
            LogDisabled(_log);
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureScene();
            _discoveryLoop ??= Task.Run(() => DiscoveryLoopAsync(_cts.Token));
        }

        StartTelemetryLoop();
    }

    // ---- IScreenService ----

    public Task<IReadOnlyList<ScreenDeviceDto>> ListScreensAsync(CancellationToken ct = default)
    {
        var present = Present(TimeSpan.FromSeconds(1));
        if (_options.Enabled)
        {
            Reconcile(present);
        }

        var list = new List<ScreenDeviceDto>();
        lock (_gate)
        {
            EnsureScene();
            foreach (var d in present)
            {
                list.Add(_workers.TryGetValue(d.Id, out var w) ? WorkerSnapshot(d.Id, w) : Idle(d.Family, d.Id, d.Instance,
                    !_options.OutputAllowed(d.Family.Family) ? ScreenStates.Disabled
                    : _quiesced?.Contains(d.Id) == true ? ScreenStates.Stopped : ScreenStates.Idle));
            }

            foreach (var a in _scene!.Assignments)
            {
                if (list.TrueForAll(s => s.Id != a.ScreenId) && ScreenIds.TryParse(a.ScreenId, out var family, out var instance))
                {
                    list.Add(Idle(family, a.ScreenId, instance, ScreenStates.Disconnected));
                }
            }
        }

        IReadOnlyList<ScreenDeviceDto> ordered = [.. list.OrderBy(s => Enum.Parse<ScreenFamily>(s.Family)).ThenBy(s => s.Id, StringComparer.Ordinal)];
        return Task.FromResult(ordered);
    }

    private ScreenDeviceDto WorkerSnapshot(string id, ScreenWorker worker)
    {
        var snapshot = worker.Snapshot();
        return worker.HasExited && _workerRecovery.TryGetValue(id, out var recovery) && recovery.Attempts >= 3
            ? snapshot with { LastError = snapshot.LastError + " · 自动恢复3次已用尽，请检查原因后重新应用内容。" }
            : snapshot;
    }

    public async Task<PcTelemetryDto> GetPcTelemetryAsync(CancellationToken ct = default)
    {
        await EnsureTelemetryAsync(ct).ConfigureAwait(false);
        return _sampler.Latest!;
    }

    public Task<IReadOnlyList<ScreenAssetDto>> ListScreenAssetsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ScreenAssetDto> list = [.. _assets.List().Select(a => a.ToDto())];
        return Task.FromResult(list);
    }

    public async Task<ScreenAssetDto> UploadScreenAssetAsync(ScreenAssetUploadDto asset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var name = asset.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException($"An asset name must be 1..{MaxNameLength} printable characters.", nameof(asset));
        }

        var limits = ImportLimits.Default;
        var maxBase64 = ((limits.MaxBytes + 2) / 3 * 4) + 4;
        if (asset.Base64 is null || asset.Base64.Length == 0 || asset.Base64.Length > maxBase64)
        {
            throw new ArgumentException($"The upload must be base64 of 1..{limits.MaxBytes} bytes.", nameof(asset));
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(asset.Base64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The upload is not valid base64.", nameof(asset), ex);
        }

        return (await ImportAssetAsync(name, data, ct).ConfigureAwait(false)).ToDto();
    }

    /// <summary>
    /// Deletes an asset under the mutation gate, so it cannot interleave with a scene transaction that validated it: either the
    /// transaction committed first (the asset is in use: refused) or the delete finished first (the transaction's validation
    /// fails). Saved inactive profiles may still reference it.
    /// </summary>
    public async Task DeleteScreenAssetAsync(string assetId, CancellationToken ct = default)
    {
        if (!AssetStore.IsValidId(assetId))
        {
            throw new ArgumentException($"'{assetId}' is not an asset id.", nameof(assetId));
        }

        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped || _disposed, this);
                EnsureScene();
                if (_scene!.Assignments.Any(a => a.BackgroundAssetId == assetId))
                {
                    throw new InvalidOperationException($"Asset {assetId} is used by the current screen scene; change the scene first.");
                }
            }

            if (AssetPinned?.Invoke(assetId) == true)
            {
                // OPUS-GENERIC-SCENE-PACKAGES: package media must stay available (saved package profiles, restart)
                throw new InvalidOperationException($"Asset {assetId} belongs to an installed scene package; it cannot be deleted separately.");
            }

            if (BeforeAssetDeleteForTest is { } barrier)
            {
                await barrier(ct).ConfigureAwait(false);
            }

            if (!_assets.Delete(assetId)) // file I/O outside _gate; the scene cannot change while the mutation gate is held
            {
                throw new ArgumentException($"Asset {assetId} does not exist.", nameof(assetId));
            }
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task<ScreenPreviewDto> PreviewScreenAsync(ScreenAssignmentDto assignment, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ScreenSceneValidator.ValidateAssignment(assignment, id => _assets.Find(id) is not null);
        if (!ScreenIds.TryParse(assignment.ScreenId, out var family, out _))
        {
            throw new ArgumentException($"'{assignment.ScreenId}' is not a screen id.", nameof(assignment));
        }

        // what this screen would show: its saved local rotation (unchanged without one) and its saved encoder policy
        ScreenAssignmentDto shownAssignment;
        lock (_gate)
        {
            EnsureScene();
            shownAssignment = NormalizeHeld(new ScreenSceneDto([assignment])).Assignments[0];
        }

        var encoding = EncodingOf(assignment.ScreenId);
        var present = Present(TimeSpan.FromSeconds(1)).Any(d => d.Id == assignment.ScreenId);
        if (assignment.Overlay != "none")
        {
            await EnsureTelemetryAsync(ct).ConfigureAwait(false);
        }

        var telemetry = _sampler.Latest;
        var now = _platform.Time.GetUtcNow();
        var rotation = _options.TurzxLandscapeRotation;
        var (png, note) = await _previewThread.Value.InvokeAsync(() =>
        {
            _previewCompositor ??= new ScreenCompositor(_assets, PreviewCacheBytes);
            _previewCompositor.ClearCache();
            var composed = _previewCompositor.Compose(family, shownAssignment, 0, telemetry, now);
            EncodedFrame encoded;
            try
            {
                encoded = Encode(family, composed.Canvas, rotation, encoding.Quality, encoding.Indexed8Verified);
            }
            catch (ScreenEncodeException ex)
            {
                // no pretend preview: an unsendable frame is an explicit failure (the caller clears any stale preview)
                throw new InvalidOperationException("NOT SENDABLE: " + ex.Message, ex);
            }

            var note = encoded.Note;
            if (composed.Warning is { } w)
            {
                note += "; " + w;
            }

            return (WpfCodec.EncodePng(DecodeSent(family, encoded, rotation)), note);
        }, ct).ConfigureAwait(false);

        if (shownAssignment.Rotation != assignment.Rotation)
        {
            note += string.Create(CultureInfo.InvariantCulture, $"; rotation {shownAssignment.Rotation} from the local device settings");
        }

        if (!present)
        {
            note += "; device not present";
        }

        if (!_options.OutputAllowed(family.Family))
        {
            note += "; output disabled";
        }

        return new ScreenPreviewDto(Convert.ToBase64String(png), family.Width, family.Height, note);
    }

    public Task<ScreenSceneDto> GetScreenSceneAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            EnsureScene();
            return Task.FromResult(_scene!);
        }
    }

    /// <summary>
    /// The direct screen-scene API. Like every commit it shows saved local rotations; for a screen WITHOUT a saved local setting a
    /// nonzero Rotation here is an explicit choice and establishes that setting (see ScreenManager.DeviceSettings).
    /// </summary>
    public async Task SetScreenSceneAsync(ScreenSceneDto scene, CancellationToken ct = default)
    {
        using var transaction = await BeginSceneCoreAsync(scene, establish: true, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public Task StopScreensAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            EnsureScene();
            _paused = true;
            foreach (var (id, w) in _workers)
            {
                w.Post(AssignmentFor(id), paused: true);
            }
        }

        LogPaused(_log);
        return Task.CompletedTask;
    }

    // ---- IScreenSceneBinding ----

    public ScreenSceneDto? CurrentForScene()
    {
        lock (_gate)
        {
            EnsureScene();
            return _scene!.Assignments.Count > 0 ? _scene : null;
        }
    }

    public void Validate(ScreenSceneDto scene) => ScreenSceneValidator.Validate(scene, id => _assets.Find(id) is not null);

    /// <summary>
    /// RGB scene/profile and scene-package commits. Validates the candidate as given, then shows it in each screen's saved local
    /// rotation (normalized here, under the mutation gate, so a concurrent settings save is never lost); never writes a setting.
    /// </summary>
    public Task<IScreenSceneTransaction> BeginSceneAsync(ScreenSceneDto scene, CancellationToken ct = default) =>
        BeginSceneCoreAsync(scene, establish: false, ct);

    private async Task<IScreenSceneTransaction> BeginSceneCoreAsync(ScreenSceneDto scene, bool establish, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scene);
        await EnterMutationAsync(ct).ConfigureAwait(false);
        try
        {
            Validate(scene);
            ScreenSceneDto normalized;
            Dictionary<string, ScreenDeviceSetting>? established = null;
            lock (_gate)
            {
                EnsureScene(); // the settings (and their one-time migration from the PERSISTED scene) are loaded with it
                normalized = NormalizeHeld(new ScreenSceneDto([.. scene.Assignments]));
                if (establish)
                {
                    established = EstablishedHeld(normalized);
                }
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped || _disposed, this);
                return _sceneTransaction = new SceneTransaction(this, normalized, established);
            }
        }
        catch
        {
            _mutation.Release();
            throw;
        }
    }

    // ---- test seams (null/ignored in production) ----

    /// <summary>Callers currently waiting for the mutation gate.</summary>
    internal int MutationWaiters => Volatile.Read(ref _mutationWaiters);

    /// <summary>True while a scene transaction or an asset delete holds the mutation gate.</summary>
    internal bool MutationHeld => _mutation.CurrentCount == 0;

    /// <summary>Awaited by <see cref="DeleteScreenAssetAsync"/> holding the gate, after the in-use check, before the files go.</summary>
    internal Func<CancellationToken, Task>? BeforeAssetDeleteForTest { get; set; }

    /// <summary>H2 PNG-layer clears written by the screen's current worker (0 without one); not part of the public DTO.</summary>
    internal long LayerBootstrapsOf(string screenId)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(screenId, out var w) ? w.LayerBootstraps : 0;
        }
    }

    // ---- lifetime ----

    /// <summary>Host stop: stops loops and workers (each aborts its pending transfer and closes its handle).</summary>
    public Task StopAsync()
    {
        lock (_stopGate) { return _stopTask ??= StopCoreAsync(); }
    }

    private async Task StopCoreAsync()
    {
        Task?[] loops;
        ScreenWorker[] workers;
        lock (_publicationGate)
        {
            lock (_gate)
            {
                _stopped = true;
                _sceneTransaction?.Dispose(); // cancel an uncommitted lease; do not wait forever for an abandoned transaction
                loops = [_discoveryLoop, _telemetryLoop];
                workers = [.. _workers.Values];
                _workers.Clear();
            }
        }
        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var loop in loops)
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
        }

        await Task.Run(() => Parallel.ForEach(workers, w => w.Dispose())).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _sampler.Dispose();
        if (_previewThread.IsValueCreated)
        {
            _previewThread.Value.Dispose();
        }

        if (_importThread.IsValueCreated)
        {
            _importThread.Value.Dispose();
        }

        _cts.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ---- internals ----

    /// <summary>Waits for the mutation gate; the caller releases it (or hands it to a <see cref="SceneTransaction"/>).</summary>
    private async Task EnterMutationAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _mutationWaiters);
        try
        {
            await _mutation.WaitAsync(ct).ConfigureAwait(false);
            if (_disposed || _stopped)
            {
                _mutation.Release();
                throw new ObjectDisposedException(nameof(ScreenManager));
            }
        }
        finally
        {
            Interlocked.Decrement(ref _mutationWaiters);
        }
    }

    /// <summary>
    /// Activates a validated, normalized scene (and any local settings a legacy SetScreenScene established); the mutation gate is
    /// held. The file writes happen outside _gate (but still in order) and are logged on failure.
    /// </summary>
    private void PublishHeld(ScreenSceneDto scene, IReadOnlyDictionary<string, ScreenDeviceSetting>? established)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _rateTrial, null); // any explicit new scene ends the temporary lab override
            if (established is not null)
            {
                Volatile.Write(ref _deviceSettings, established);
            }

            _scene = scene;
            _paused = false;
            _workerRecovery.Clear(); // explicit apply allows a new bounded recovery burst; polling never replenishes it
            foreach (var (id, w) in _workers)
            {
                w.Post(AssignmentFor(id), paused: false);
            }
        }

        LogApplied(_log, scene.Assignments.Count);
    }

    /// <summary>
    /// STA. What the screen receives, decoded from the EXACT encoded image bytes, in authoring orientation: a TURZX raster is turned
    /// back by the landscape rotation the encoder applied (the user rotation was already composed into the canvas once).
    /// </summary>
    private static PixelFrame DecodeSent(ScreenFamilyInfo family, EncodedFrame encoded, int turzxLandscapeRotation)
    {
        var format = WpfCodec.Sniff(encoded.Image) is { } f and ("jpeg" or "png") ? f
            : throw new InvalidOperationException("NOT SENDABLE: the encoded frame is neither JPEG nor PNG.");
        PixelFrame decoded;
        using (var ms = new MemoryStream(encoded.Image, writable: false))
        {
            decoded = WpfCodec.ToPixels(WpfCodec.Decoder(format, ms).Frames[0]);
        }

        var native = family.Codec == ScreenCodec.TurzxPng;
        var (w, h) = native ? (family.Height, family.Width) : (family.Width, family.Height);
        if (decoded.Width != w || decoded.Height != h)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"NOT SENDABLE: the encoded frame is {decoded.Width}x{decoded.Height}, not the {w}x{h} {family.Token} raster."));
        }

        return native ? PixelOps.Rotate(decoded, (360 - turzxLandscapeRotation) % 360) : decoded;
    }

    /// <summary>Owns one acquisition of the mutation gate: released exactly once, by Commit or by Dispose.</summary>
    private sealed class SceneTransaction(ScreenManager owner, ScreenSceneDto scene, IReadOnlyDictionary<string, ScreenDeviceSetting>? established)
        : IPreparedScreenSceneTransaction
    {
        private const int Open = 0;
        private const int Done = 1;
        private int _state;
        public IReadOnlyList<SceneStateFile> Files { get; } = established is null
            ? [new("screens/scene.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(scene, ContractsJsonContext.Default.ScreenSceneDto))]
            : [new("screens/scene.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(scene, ContractsJsonContext.Default.ScreenSceneDto)),
                new("screens/device-settings.json", ScreenDeviceSettingsStore.Serialize(established))];

        public void Commit()
        {
            Coordinate(() => { SceneStateFiles.Commit(owner._stateRoot, Files); Publish(); });
        }

        public void Coordinate(Action commit)
        {
            ArgumentNullException.ThrowIfNull(commit);
            lock (owner._publicationGate)
            {
                ObjectDisposedException.ThrowIf(owner._stopped || owner._disposed, owner);
                if (Volatile.Read(ref _state) != Open) throw new InvalidOperationException("This screen scene transaction is already committed or disposed.");
                commit(); // synchronous prepare/persist/publish: stop cannot cut through its durable commit point
            }
        }

        public void Publish()
        {
            lock (owner._publicationGate)
            {
            if (Interlocked.CompareExchange(ref _state, Done, Open) != Open)
            {
                throw new InvalidOperationException("This screen scene transaction is already committed or disposed.");
            }

            try
            {
                owner.PublishHeld(scene, established);
            }
            finally
            {
                lock (owner._gate) { if (ReferenceEquals(owner._sceneTransaction, this)) owner._sceneTransaction = null; }
                owner._mutation.Release();
            }
            }
        }

        public void Dispose()
        {
            lock (owner._publicationGate)
            {
            if (Interlocked.CompareExchange(ref _state, Done, Open) == Open)
            {
                lock (owner._gate) { if (ReferenceEquals(owner._sceneTransaction, this)) owner._sceneTransaction = null; }
                owner._mutation.Release();
            }
            }
        }
    }

    private ScreenAssignmentDto? AssignmentFor(string id) => _scene?.Assignments.FirstOrDefault(a => a.ScreenId == id);

    /// <summary>
    /// _gate held. Loads the persisted scene once, with the local settings (first run: migrated from that persisted scene), and
    /// shows it in the saved local rotations (the startup restore).
    /// </summary>
    private void EnsureScene()
    {
        if (_scene is not null)
        {
            return;
        }

        SceneStateFiles.Recover(_stateRoot);
        var persisted = _sceneStore.Load();
        LoadDeviceSettingsHeld(persisted);
        _scene = NormalizeHeld(persisted);
    }

    private IReadOnlyList<ScreenDeviceInfo> Present(TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (_enumerated && _platform.Time.GetElapsedTime(_presentAt) <= maxAge)
            {
                return _present;
            }
        }

        IReadOnlyList<ScreenDeviceInfo> found;
        try
        {
            found = _platform.Enumerator.Enumerate();
        }
#pragma warning disable CA1031 // discovery failure only empties the list; it must never fail an RPC or the Host
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDiscoveryFailed(_log, ex.Message);
            found = [];
        }

        lock (_gate)
        {
            _present = found;
            _presentAt = _platform.Time.GetTimestamp();
            _enumerated = true;
        }

        return found;
    }

    /// <summary>Creates workers for present displays whose gate is open and removes those that disappeared.</summary>
    private void Reconcile(IReadOnlyList<ScreenDeviceInfo> present)
    {
        var gone = new List<ScreenWorker>();
        lock (_gate)
        {
            if (_disposed || _stopped || _cts.IsCancellationRequested)
            {
                return;
            }

            EnsureScene();
            foreach (var d in present)
            {
                if (_workers.TryGetValue(d.Id, out var existing) && existing.HasExited && !_paused
                    && _options.OutputAllowed(d.Family.Family) && _quiesced?.Contains(d.Id) != true)
                {
                    var now = _platform.Time.GetTimestamp();
                    if (!_workerRecovery.TryGetValue(d.Id, out var recovery))
                    {
                        recovery = new WorkerRecovery();
                        _workerRecovery[d.Id] = recovery;
                        recovery.NotBefore = now + _platform.Time.TimestampFrequency;
                    }
                    if (recovery.Attempts < 3 && now >= recovery.NotBefore)
                    {
                        var previous = existing.Snapshot();
                        gone.Add(existing); // already exited: no native operation can overlap its replacement
                        recovery.Attempts++;
                        recovery.NotBefore = now + (1L << recovery.Attempts) * _platform.Time.TimestampFrequency;
                        _workers[d.Id] = new ScreenWorker(d, _workerContext, new WorkerCommand(AssignmentFor(d.Id), _paused), previous);
                        recovery.StartedAt = now;
                    }
                }
                else if (_workers.TryGetValue(d.Id, out existing) && _workerRecovery.TryGetValue(d.Id, out var recovered)
                    && existing.Snapshot().State == ScreenStates.Running
                    && _platform.Time.GetElapsedTime(recovered.StartedAt) >= TimeSpan.FromSeconds(30))
                {
                    _workerRecovery.Remove(d.Id); // a healthy interval ends the burst; a single successful frame does not
                }
                // a screen quiesced by RestartFanLcdAsync gets no worker (not even a hotplug-fresh one) until the restart restores it
                if (_options.OutputAllowed(d.Family.Family) && !_workers.ContainsKey(d.Id) && _quiesced?.Contains(d.Id) != true)
                {
                    _workers[d.Id] = new ScreenWorker(d, _workerContext, new WorkerCommand(AssignmentFor(d.Id), _paused));
                    LogAttached(_log, d.Id);
                }
            }

            foreach (var id in _workers.Keys.Where(id => present.All(d => d.Id != id)).ToList())
            {
                gone.Add(_workers[id]);
                _workers.Remove(id);
                _workerRecovery.Remove(id);
                LogDetached(_log, id);
            }
        }

        foreach (var w in gone)
        {
            w.Dispose();
        }
    }

    private sealed class WorkerRecovery
    {
        public int Attempts { get; set; }
        public long NotBefore { get; set; }
        public long StartedAt { get; set; }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.DiscoveryIntervalMs, 250, 60_000));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Reconcile(Present(TimeSpan.Zero));
                await Task.Delay(interval, _platform.Time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void StartTelemetryLoop()
    {
        lock (_gate)
        {
            if (_telemetryLoop is null && !_disposed && !_cts.IsCancellationRequested)
            {
                _telemetryLoop = Task.Run(() => TelemetryLoopAsync(_cts.Token));
            }
        }
    }

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _sampler.Sample();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), _platform.Time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>Starts the 1 Hz sampler on first demand; the very first call takes two samples 250 ms apart so CPU % exists.</summary>
    private async Task EnsureTelemetryAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var latest = _sampler.Latest;
        if (latest is null)
        {
            await Task.Run(_sampler.Sample, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(250), _platform.Time, ct).ConfigureAwait(false);
            await Task.Run(_sampler.Sample, ct).ConfigureAwait(false);
        }
        else if (_platform.Time.GetUtcNow() - latest.Timestamp > TimeSpan.FromSeconds(2))
        {
            await Task.Run(_sampler.Sample, ct).ConfigureAwait(false);
        }

        StartTelemetryLoop();
    }

    private ScreenDeviceDto Idle(ScreenFamilyInfo family, string id, string instance, string state) =>
        new(id, ScreenStates.Name(family, instance), family.Family.ToString(), family.Width, family.Height, state, null,
            _options.For(family.Family).ControlVerified, 0, 0, null, 0);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screens output disabled (UnifiedRgb:Screens:Enabled=false): discovery and preview only")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen scene applied ({Count} assignments)")]
    private static partial void LogApplied(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screens paused by StopScreens")]
    private static partial void LogPaused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen scene could not be saved to {Path}: {Reason}")]
    private static partial void LogSaveFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen discovery failed: {Reason}")]
    private static partial void LogDiscoveryFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen {Id} attached")]
    private static partial void LogAttached(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen {Id} detached")]
    private static partial void LogDetached(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen asset {Id} stored: {Kind} {Width}x{Height}, {Frames} frames, {Bytes} B")]
    private static partial void LogUploaded(ILogger logger, string id, string kind, int width, int height, int frames, long bytes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Switch} is not set for this process: telemetry overlays render nothing while no display device is available")]
    private static partial void LogHeadlessSwitchMissing(ILogger logger, string @switch);
}
