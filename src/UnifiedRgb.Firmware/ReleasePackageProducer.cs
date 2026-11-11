using System.IO.Compression;

namespace UnifiedRgb.Firmware;

/// <summary>Offline packager only; produces no catalog, executable, device pin or physical authorization.</summary>
public static class ReleasePackageProducer
{
    public static void Create(string manifestPath, string outputPath)
    {
        FirmwareFiles.NoReparse(manifestPath); FirmwareFiles.NoReparse(outputPath);
        if (new FileInfo(manifestPath).Length > 32768) throw new InvalidDataException("Release manifest bound");
        var raw = File.ReadAllBytes(manifestPath);
        var release = FirmwareFiles.Parse<FirmwareRelease>(raw);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        using (var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Add(archive, "release.json", raw);
            foreach (var reference in new[] { release.Candidate, release.Rollback, release.ImageManifest, release.WireContract, release.SoftwareReview })
            {
                FirmwareFiles.Relative(reference.Path);
                var path = Path.Combine(directory, reference.Path); FirmwareFiles.NoReparse(path);
                if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Input bound");
                Add(archive, reference.Path, File.ReadAllBytes(path));
            }
        }
        _ = ReleasePackage.Load(outputPath); // reject but preserve invalid output for diagnosis
    }
    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open(); stream.Write(bytes);
    }
}
