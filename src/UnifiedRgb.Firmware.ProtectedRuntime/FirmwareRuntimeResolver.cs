namespace UnifiedRgb.Firmware.ProtectedRuntime;

/// <summary>Finite filesystem-only startup admission. The app-local directory is STAGING, never execution authority.</summary>
public static class FirmwareRuntimeResolver
{
    public const string LiveDirectory = "C:/ProgramData/UnifiedRgbFirmwareRuntime";
    public const string ConfigurationFile = "firmware-installed.json";
    public const string CatalogFile = "trusted-catalog.json";
    public const int MaximumFiles = 256;
    public const int MaximumTreeEntries = 256;
    public const int MaximumRuntimeBytes = 64 * 1024 * 1024;
    public const int MaximumDataBytes = 1024 * 1024;
    public const long MaximumTotalBytes = 128L * 1024 * 1024;

    public static FirmwareInstalledOptions Resolve(string appStaging, bool useVirtualAdapter,
        Func<FirmwareInstalledOptions, FirmwareInstalledOptions> leaseGuard,
        IFirmwareRuntimeFileSystem? fileSystem = null, IFirmwareRuntimeElevation? elevation = null)
    {
        var disabled = new FirmwareInstalledOptions(LiveDirectory);
        if (useVirtualAdapter) return disabled; // Includes zero lease/identity/filesystem reads.
        try
        {
            ArgumentNullException.ThrowIfNull(leaseGuard);
            var guarded = leaseGuard(disabled with { HardwareEnabled = true });
            if (!guarded.HardwareEnabled) return disabled with { FeatureError = guarded.FeatureError ?? "Firmware maintenance state unknown" };
            var fs = fileSystem ?? new WindowsFirmwareRuntimeFileSystem();
            NoReparse(fs, LiveDirectory);
            if (fs.Inspect(LiveDirectory, false) is not null)
                return Validate(fs, LiveDirectory, true).Options;

            NoReparse(fs, appStaging);
            var configPath = Path.Combine(appStaging, ConfigurationFile);
            if (fs.Inspect(configPath, false) is null) return disabled;
            var configBytes = fs.ReadLockedSnapshot(configPath, 4096);
            var config = Configuration(configBytes);
            if (!config.HardwareEnabled) return disabled;
            if (!(elevation ?? new WindowsFirmwareRuntimeElevation()).IsAdministratorOrSystem)
                return disabled with { FeatureError = "Firmware first provisioning requires an already elevated Administrator/System Host; no elevation is requested" };

            var payload = Validate(fs, appStaging, false, configBytes);
            // Validate everything before the first write; create is atomic, exclusive, and never repairs an existing object.
            NoReparse(fs, LiveDirectory);
            fs.CreateProtectedDirectory(LiveDirectory);
            CheckProtected(fs, LiveDirectory, true);
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(LiveDirectory) };
            foreach (var (relative, bytes) in payload.Bytes)
            {
                var path = Path.Combine(LiveDirectory, relative);
                CreateParents(fs, Path.GetDirectoryName(path)!, created);
                fs.CreateProtectedFile(path, bytes);
                CheckProtected(fs, path, false);
            }
            var live = Validate(fs, LiveDirectory, true);
            if (live.Bytes.Count != payload.Bytes.Count || payload.Bytes.Any(x =>
                !live.Bytes.TryGetValue(x.Key, out var bytes) || !bytes.AsSpan().SequenceEqual(x.Value)))
                throw new InvalidDataException("Protected runtime post-copy snapshot differs");
            return live.Options;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or NotSupportedException or System.Text.Json.JsonException)
        {
            return disabled with { FeatureError = "Firmware protected runtime disabled; files preserved for reviewed maintenance: " + ex.Message };
        }
    }

    private static InstalledWorkerConfiguration Configuration(byte[] bytes)
    {
        if (bytes.Length > 4096) throw new InvalidDataException("Installed configuration bound");
        var config = FirmwareFiles.Parse<InstalledWorkerConfiguration>(bytes);
        if (config.Schema != 1 || config.CatalogFile is not null && config.CatalogFile != CatalogFile ||
            config.HardwareEnabled && config.CatalogFile != CatalogFile)
            throw new InvalidDataException("Protected configuration requires the fixed separate trusted-catalog.json");
        return config;
    }

    private static Payload Validate(IFirmwareRuntimeFileSystem fs, string root, bool live, byte[]? initialConfiguration = null)
    {
        NoReparse(fs, root);
        var rootEntry = fs.Inspect(root, live) ?? throw new InvalidDataException("Runtime root missing");
        if (!rootEntry.Directory) throw new InvalidDataException("Runtime root is not a directory");
        if (live) FirmwareRuntimeSecurity.Validate(rootEntry);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            foreach (var path in fs.EnumerateChildren(pending.Pop()))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                FirmwareFiles.Relative(relative);
                if (relative.Split('/').Length > 16 || names.Count >= MaximumTreeEntries || !names.TryAdd(relative, relative))
                    throw new InvalidDataException("Runtime tree bound/case alias");
                NoReparse(fs, path);
                var entry = fs.Inspect(path, live) ?? throw new InvalidDataException("Runtime tree changed");
                if (live) FirmwareRuntimeSecurity.Validate(entry);
                if (entry.Directory) pending.Push(path); else files.Add(relative);
            }
        }
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        byte[] Read(string relative, int maximum, byte[]? captured = null)
        {
            FirmwareFiles.Relative(relative);
            if (!files.Contains(relative)) throw new InvalidDataException("Missing canonical pinned file: " + relative);
            var remaining = checked((int)Math.Min(maximum, MaximumTotalBytes - total));
            var content = captured ?? fs.ReadLockedSnapshot(Path.Combine(root, relative), remaining);
            if (content.Length > maximum || bytes.Count >= MaximumFiles || (total += content.Length) > MaximumTotalBytes)
                throw new InvalidDataException("Runtime file/total byte bound");
            bytes.Add(relative, content);
            return content;
        }
        var config = Configuration(Read(ConfigurationFile, 4096, initialConfiguration));
        if (!config.HardwareEnabled) throw new InvalidDataException("Protected live configuration is disabled/incomplete; no automatic repair");
        var catalog = FirmwareFiles.Parse<TrustedCatalog>(Read(CatalogFile, MaximumDataBytes));
        ValidateCatalog(catalog);
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Pin(string name, string hash, bool runtime)
        {
            FirmwareFiles.Relative(name); FirmwareFiles.Hash(hash);
            if (names.TryGetValue(name, out var canonical) && name != canonical)
                throw new InvalidDataException("Pinned path case alias");
            if (name == ConfigurationFile || name == CatalogFile)
                throw new InvalidDataException("Configuration/catalog cannot also be payload pins");
            if (pins.TryGetValue(name, out var prior))
            {
                if (prior != hash || !bytes.ContainsKey(name)) throw new InvalidDataException("Conflicting pin hash/case");
                return;
            }
            pins.Add(name, hash);
            var maximum = runtime && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? MaximumRuntimeBytes : MaximumDataBytes;
            if (FirmwareFiles.Sha(Read(name, maximum)) != hash) throw new InvalidDataException("Pinned snapshot hash mismatch: " + name);
        }
        foreach (var release in catalog.Releases)
        {
            foreach (var (name, hash) in release.Worker.RuntimeFiles) Pin(name, hash, true);
            if (release.RetainedBaseline is { } retained)
            {
                if (!retained.Image.Path.EndsWith(".bin", StringComparison.Ordinal) || !retained.Receipt.Path.EndsWith(".json", StringComparison.Ordinal))
                    throw new InvalidDataException("Retained baseline requires BIN/JSON data only");
                Pin(retained.Image.Path, retained.Image.Sha256, false);
                Pin(retained.Receipt.Path, retained.Receipt.Sha256, false);
                ValidateRetained(release, retained, bytes);
            }
        }
        var options = new FirmwareInstalledOptions(LiveDirectory, Path.Combine(LiveDirectory, CatalogFile), true);
        var snapshot = new Snapshot(files, bytes);
        var trust = new InstalledTrust(options, snapshot);
        foreach (var release in catalog.Releases) trust.ValidateWorker(release.Worker);
        if (live && files.Any(x => !bytes.ContainsKey(x))) throw new InvalidDataException("Unlisted protected runtime file");
        return new(options, bytes);
    }

    private static void ValidateCatalog(TrustedCatalog catalog)
    {
        if (catalog.Schema != 1 || catalog.Classification != "UNIFIEDRGB_FIRMWARE_TRUST_CATALOG_V1" ||
            catalog.Releases.Length is < 1 or > 128 || catalog.Releases.Any(x => x is null) ||
            catalog.Releases.GroupBy(x => x.ReleaseId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new InvalidDataException("Invalid trusted catalog schema/unique releases");
        foreach (var release in catalog.Releases)
        {
            if (!FirmwareFiles.Identifier(release.ReleaseId)) throw new InvalidDataException("Invalid catalog release ID");
            foreach (var hash in new[] { release.ReleaseManifestSha256, release.CandidateSha256, release.RollbackSha256,
                release.ImageManifestSha256, release.WireContractSha256, release.SoftwareReviewSha256 }) FirmwareFiles.Hash(hash);
            if (release.Worker is null || release.Worker.RuntimeFiles is null) throw new InvalidDataException("Missing catalog worker");
            FirmwareFiles.Hash(release.Worker.Sha256); FirmwareFiles.Hash(release.Worker.CoreManifestSha256);
            if (release.Worker.RuntimeFiles.Count > 32) throw new InvalidDataException("Worker runtime pin bound");
        }
    }

    private static void ValidateRetained(CatalogEntry release, RetainedBaselineReview review, Dictionary<string, byte[]> bytes)
    {
        if (review.Label.Length is < 1 or > 160 || review.Image.Sha256 != release.RollbackSha256)
            throw new InvalidDataException("Retained baseline differs from reviewed rollback");
        FirmwareFiles.CheckImage(bytes[review.Image.Path]);
        var raw = bytes[review.Receipt.Path];
        if (raw.Length > 32768) throw new InvalidDataException("Retained receipt bound");
        var receipt = FirmwareFiles.Parse<RetainedBaselineReceipt>(raw);
        if (receipt.Schema != 1 || receipt.Classification != "UNIFIEDRGB_OWNER_CONFIRMED_RETAINED_BASELINE_V1" ||
            receipt.ImageSha256 != review.Image.Sha256 || receipt.NormalModel != "1C24" || receipt.NormalFirmwareHeader != "001200000025000100" ||
            !Guid.TryParse(receipt.ContainerId, out var container) || container == Guid.Empty ||
            !receipt.PhysicalLocation.Contains("#USB(", StringComparison.Ordinal) || receipt.OwnerConfirmedAtUtc > DateTimeOffset.UtcNow ||
            receipt.OwnerConfirmedAtUtc == default || receipt.EvidenceNote.Length is < 1 or > 4096)
            throw new InvalidDataException("Invalid owner-confirmed retained receipt identity/schema (not a fresh read)");
    }

    private static void CreateParents(IFirmwareRuntimeFileSystem fs, string path, HashSet<string> created)
    {
        path = Path.GetFullPath(path);
        if (created.Contains(path)) return;
        var relative = Path.GetRelativePath(LiveDirectory, path).Replace('\\', '/'); FirmwareFiles.Relative(relative);
        CreateParents(fs, Path.GetDirectoryName(path)!, created);
        fs.CreateProtectedDirectory(path); CheckProtected(fs, path, true); created.Add(path);
    }

    private static void CheckProtected(IFirmwareRuntimeFileSystem fs, string path, bool directory)
    {
        NoReparse(fs, path);
        var entry = fs.Inspect(path, true) ?? throw new InvalidDataException("Protected object missing");
        if (entry.Directory != directory) throw new InvalidDataException("Protected object type mismatch");
        FirmwareRuntimeSecurity.Validate(entry);
    }

    private static void NoReparse(IFirmwareRuntimeFileSystem fs, string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (fs.Inspect(current, false)?.Reparse == true) throw new InvalidDataException("Runtime reparse ancestor/root/file forbidden");
    }

    private sealed record Payload(FirmwareInstalledOptions Options, Dictionary<string, byte[]> Bytes);
    private sealed class Snapshot(IEnumerable<string> names, Dictionary<string, byte[]> bytes) : IInstalledRuntimeSnapshot
    {
        public IEnumerable<string> FileNames => names;
        public string Hash(string relativePath) => bytes.TryGetValue(relativePath, out var value)
            ? FirmwareFiles.Sha(value) : throw new InvalidDataException("Missing pinned runtime snapshot");
    }
}
