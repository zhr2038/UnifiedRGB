using System.Buffers.Binary;
using System.Text.Json;
using UnifiedRgb.Firmware;
using UnifiedRgb.Firmware.ProtectedRuntime;

namespace UnifiedRgb.Firmware.ProtectedRuntime.Checks;

internal sealed class Fixture
{
    internal const string Staging = "E:/mutable/app/FirmwareWorker";
    internal FakeFileSystem Fs { get; } = new();
    internal CatalogEntry Release { get; private set; }
    internal TrustedCatalog Catalog { get; private set; }
    internal Fixture()
    {
        Fs.Put("C:/"); Fs.Put("C:/ProgramData"); Fs.Put(Staging);
        var runtime = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["worker.exe"] = [1, 2, 3], ["worker.dll"] = [4, 5, 6],
            ["worker.deps.json"] = [7], ["worker.runtimeconfig.json"] = [8],
            ["core-manifest.json"] = [9], ["runtimes/win/native/dependency.dll"] = [10],
        };
        var pins = runtime.ToDictionary(x => x.Key, x => FirmwareFiles.Sha(x.Value), StringComparer.Ordinal);
        var image = new byte[FirmwareFiles.ImageLength]; image[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4), 1);
        var imageHash = FirmwareFiles.Sha(image);
        var receipt = new RetainedBaselineReceipt(1, "UNIFIEDRGB_OWNER_CONFIRMED_RETAINED_BASELINE_V1", imageHash,
            "1C24", "001200000025000100", "d6a3f81e-a055-49f2-81eb-55b091a15638", "PCI#USB(1)",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Fake owner receipt, not live evidence");
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        var hash = FirmwareFiles.Sha([42]);
        Release = new("release-1", hash, hash, imageHash, hash, hash, hash,
            new("worker-1", "worker.exe", pins["worker.exe"], pins["core-manifest.json"], pins),
            new("Retained fake baseline", new("retained/image.bin", imageHash), new("retained/receipt.json", FirmwareFiles.Sha(receiptBytes))));
        Catalog = new(1, "UNIFIEDRGB_FIRMWARE_TRUST_CATALOG_V1", [Release]);
        Config(true);
        WriteCatalog();
        foreach (var (name, bytes) in runtime) Put(name, bytes);
        Put("retained/image.bin", image); Put("retained/receipt.json", receiptBytes);
        Put("ignored.txt", [99]); // Must never be copied.
    }
    internal void Config(bool enabled, string? catalog = FirmwareRuntimeResolver.CatalogFile) =>
        Put(FirmwareRuntimeResolver.ConfigurationFile, JsonSerializer.SerializeToUtf8Bytes(new InstalledWorkerConfiguration(1, enabled, catalog)));
    internal void ChangeRelease(Func<CatalogEntry, CatalogEntry> update)
    {
        Release = update(Release); Catalog = Catalog with { Releases = [Release] }; WriteCatalog();
    }
    internal void SetCatalog(TrustedCatalog catalog) { Catalog = catalog; WriteCatalog(); }
    private void WriteCatalog() => Put(FirmwareRuntimeResolver.CatalogFile, JsonSerializer.SerializeToUtf8Bytes(Catalog));
    internal void Put(string relative, byte[] bytes)
    {
        var path = Path.Combine(Staging, relative);
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent != Path.GetDirectoryName(Staging); parent = Path.GetDirectoryName(parent))
            if (!Fs.Nodes.ContainsKey(FakeFileSystem.Key(parent))) Fs.Put(parent);
        Fs.Put(path, bytes);
    }
    internal FirmwareInstalledOptions Resolve(bool useVirtual = false, Func<FirmwareInstalledOptions, FirmwareInstalledOptions>? guard = null) =>
        FirmwareRuntimeResolver.Resolve(Staging, useVirtual, guard ?? (options => options), Fs, Fs);
    internal void ExistingLive()
    {
        var result = Resolve();
        if (!result.HardwareEnabled) throw new InvalidOperationException("Fixture provision failed: " + result.FeatureError);
        Fs.Writes.Clear(); Fs.Reads.Clear();
    }
}
