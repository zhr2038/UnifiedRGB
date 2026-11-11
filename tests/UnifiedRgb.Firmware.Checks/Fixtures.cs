using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using UnifiedRgb.Firmware;

namespace UnifiedRgb.Firmware.Checks;

internal static class Fixtures
{
    public static byte[] Image(int seed)
    {
        var image = new byte[FirmwareFiles.ImageLength]; new Random(seed).NextBytes(image);
        uint sum = 0;
        for (var i = 0; i < image.Length - 4; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i, 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4), sum); return image;
    }
    public static Dictionary<string, byte[]> Files(byte[] original, byte[] candidate, ImageRange[]? allowedChanges = null, int pageCount = 55)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["data/original.bin"] = original, ["data/candidate.bin"] = candidate };
        files["data/manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new FunctionalImageManifest(1, "UNIFIEDRGB_FUNCTIONAL_IMAGE_MANIFEST_V1", 114688, 339968, 225280,
            FirmwareFiles.Sha(candidate), FirmwareFiles.Sha(original), FirmwareFiles.PagePolicy, allowedChanges ?? [new(114688, 339968)]));
        files["data/contract.json"] = JsonSerializer.SerializeToUtf8Bytes(new ReleaseWireContract(1, "UNIFIEDRGB_R6_WIRE_CONTRACT_V1", 114688, 339968, 4096, pageCount, FirmwareFiles.PagePolicy, "02000200"));
        files["data/review.json"] = JsonSerializer.SerializeToUtf8Bytes(new SoftwareReleaseReview(1, "UNIFIEDRGB_SOFTWARE_RELEASE_REVIEW_V1", "FAKE-ONLY-1", FirmwareFiles.Sha(candidate), FirmwareFiles.Sha(original), FirmwareFiles.Sha(files["data/manifest.json"]), FirmwareFiles.Sha(files["data/contract.json"])));
        ReleaseFile Ref(string path) => new(path, FirmwareFiles.Sha(files[path]));
        files["release.json"] = JsonSerializer.SerializeToUtf8Bytes(new FirmwareRelease(1, "UNIFIEDRGB_KEYBOARD_APP_RELEASE_V1", "FAKE-ONLY-1", "纯假设备测试", "fake-1", "Not installable on real devices", "1C24", "001200000025000100", "1C26", "02000200", 114688, 339968,
            FirmwareFiles.PagePolicy, Ref("data/candidate.bin"), Ref("data/original.bin"), Ref("data/manifest.json"), Ref("data/contract.json"), Ref("data/review.json")));
        return files;
    }
    public static string Zip(string directory, Dictionary<string, byte[]> files, Action<ZipArchive>? extra = null)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".urgbfw");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files)
        { using var output = archive.CreateEntry(name, CompressionLevel.NoCompression).Open(); output.Write(bytes); }
        extra?.Invoke(archive); return path;
    }
    public static FirmwareInstalledOptions Trust(string directory, ReleasePackage package)
    {
        var privateDirectory = Path.Combine(directory, "private"); Directory.CreateDirectory(privateDirectory);
        var runtime = new Dictionary<string, string>();
        foreach (var name in new[] { "UnifiedRgb.Firmware.Worker.exe", "UnifiedRgb.Firmware.Worker.dll", "UnifiedRgb.Firmware.Worker.deps.json", "UnifiedRgb.Firmware.Worker.runtimeconfig.json", "core-manifest.json" })
        {
            var path = Path.Combine(privateDirectory, name); if (!File.Exists(path)) File.WriteAllText(path, "FAKE-NOT-EXECUTABLE-" + name);
            runtime[name] = FirmwareFiles.HashFile(path);
        }
        var worker = new WorkerPin("FAKE-NEVER-EXECUTE", "UnifiedRgb.Firmware.Worker.exe", runtime["UnifiedRgb.Firmware.Worker.exe"], runtime["core-manifest.json"], runtime);
        var r = package.Release;
        var entry = new CatalogEntry(r.ReleaseId, package.ManifestSha256, r.Candidate.Sha256, r.Rollback.Sha256, r.ImageManifest.Sha256, r.WireContract.Sha256, r.SoftwareReview.Sha256, worker);
        var catalog = Path.Combine(privateDirectory, "reviewed-catalog.json");
        File.WriteAllBytes(catalog, JsonSerializer.SerializeToUtf8Bytes(new TrustedCatalog(1, "UNIFIEDRGB_FIRMWARE_TRUST_CATALOG_V1", [entry])));
        return new(privateDirectory, catalog, true);
    }
}
