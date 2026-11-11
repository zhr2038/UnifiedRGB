namespace UnifiedRgb.Firmware;

/// <summary>Constructed only by installed Host composition, never from RPC or a package.</summary>
public sealed record FirmwareInstalledOptions(string PrivateDirectory, string? CatalogPath = null, bool HardwareEnabled = false, string? FeatureError = null);
public sealed record InstalledWorkerConfiguration(int Schema, bool HardwareEnabled, string? CatalogFile)
{
    public static FirmwareInstalledOptions Read(string privateDirectory)
    {
        try { return ReadStrict(privateDirectory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return new(privateDirectory, FeatureError: "固件功能配置无效，已禁用并保留原文件：" + ex.GetType().Name); }
    }
    private static FirmwareInstalledOptions ReadStrict(string privateDirectory)
    {
        var path = Path.Combine(privateDirectory, "firmware-installed.json");
        FirmwareFiles.NoReparse(path);
        if (!File.Exists(path)) return new(privateDirectory);
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Installed configuration bound");
        var config = FirmwareFiles.Parse<InstalledWorkerConfiguration>(File.ReadAllBytes(path));
        if (config.Schema != 1 || config.CatalogFile is not null && !FirmwareFiles.Identifier(config.CatalogFile)) throw new InvalidDataException("Installed configuration invalid");
        return new(privateDirectory, config.CatalogFile is null ? null : Path.Combine(privateDirectory, config.CatalogFile), config.HardwareEnabled);
    }
}

public sealed class InstalledTrust(FirmwareInstalledOptions options, IInstalledRuntimeSnapshot? snapshot)
{
    public InstalledTrust(FirmwareInstalledOptions options) : this(options, null) { }

    public CatalogEntry? Review(ReleasePackage package)
    {
        if (options.CatalogPath is null || !File.Exists(options.CatalogPath)) return null;
        FirmwareFiles.NoReparse(options.CatalogPath);
        if (!Path.IsPathFullyQualified(options.CatalogPath) || new FileInfo(options.CatalogPath).Length > 1024 * 1024) throw new InvalidDataException("Installed catalog bound/path");
        var catalog = FirmwareFiles.Parse<TrustedCatalog>(FirmwareFiles.ReadBounded(options.CatalogPath, 1024 * 1024));
        if (catalog.Schema != 1 || catalog.Classification != "UNIFIEDRGB_FIRMWARE_TRUST_CATALOG_V1" || catalog.Releases.Length > 128 ||
            catalog.Releases.Any(x => x is null) ||
            catalog.Releases.GroupBy(x => x.ReleaseId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1)) throw new InvalidDataException("Invalid installed catalog");
        var r = package.Release;
        var entry = catalog.Releases.SingleOrDefault(x => x.ReleaseId == r.ReleaseId && x.ReleaseManifestSha256 == package.ManifestSha256 &&
            x.CandidateSha256 == r.Candidate.Sha256 && x.RollbackSha256 == r.Rollback.Sha256 && x.ImageManifestSha256 == r.ImageManifest.Sha256 &&
            x.WireContractSha256 == r.WireContract.Sha256 && x.SoftwareReviewSha256 == r.SoftwareReview.Sha256);
        if (entry is not null) ValidateWorker(entry.Worker);
        return entry;
    }
    public string ValidateWorker(WorkerPin worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (worker.RuntimeFiles is null) throw new InvalidDataException("Missing private runtime pins");
        if (!FirmwareFiles.Identifier(worker.WorkerId) || !FirmwareFiles.Identifier(worker.Executable) || !worker.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            worker.RuntimeFiles.Count is < 5 or > 32 || !worker.RuntimeFiles.TryGetValue(worker.Executable, out var executableHash) || executableHash != worker.Sha256 ||
            !worker.RuntimeFiles.TryGetValue("core-manifest.json", out var coreHash) || coreHash != worker.CoreManifestSha256)
            throw new InvalidDataException("Incomplete installed worker/runtime/core pins");
        var stem = Path.GetFileNameWithoutExtension(worker.Executable);
        foreach (var required in new[] { stem + ".dll", stem + ".deps.json", stem + ".runtimeconfig.json" })
            if (!worker.RuntimeFiles.ContainsKey(required)) throw new InvalidDataException("Missing worker managed runtime pin");
        if (snapshot is null) FirmwareFiles.NoReparse(options.PrivateDirectory);
        foreach (var runtimeFile in snapshot?.FileNames ?? EnumeratePrivateFiles(options.PrivateDirectory).Select(x => Path.GetRelativePath(options.PrivateDirectory, x).Replace('\\', '/')))
            if ((runtimeFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || runtimeFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                runtimeFile.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || runtimeFile.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) &&
                !worker.RuntimeFiles.ContainsKey(runtimeFile))
                throw new InvalidDataException("Unpinned private runtime file");
        foreach (var (name, hash) in worker.RuntimeFiles)
        {
            FirmwareFiles.Relative(name);
            FirmwareFiles.Hash(hash);
            if (snapshot is not null)
            {
                if (snapshot.Hash(name) != hash) throw new InvalidDataException("Installed worker runtime hash mismatch");
            }
            else
            {
                var path = Path.Combine(options.PrivateDirectory, name); FirmwareFiles.NoReparse(path);
                if (!File.Exists(path) || FirmwareFiles.HashFile(path) != hash) throw new InvalidDataException("Installed worker runtime hash mismatch");
            }
        }
        return Path.Combine(options.PrivateDirectory, worker.Executable);
    }
    private static IEnumerable<string> EnumeratePrivateFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.Count != 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > 256) throw new InvalidDataException("Private runtime directory enumeration bound");
                FirmwareFiles.NoReparse(entry);
                if (Directory.Exists(entry)) pending.Push(entry); else yield return entry;
            }
    }
}
