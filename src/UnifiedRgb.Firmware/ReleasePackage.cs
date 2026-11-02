using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Firmware;

public sealed record ReleaseFile(string Path, string Sha256);
public sealed record FirmwareRelease(int Schema, string Classification, string ReleaseId, string Title,
    string Version, string Notes, string NormalModel, string NormalFirmwareHeader, string BootModel,
    string BootRawVersion, int AppStart, int AppEnd, string PagePolicy, ReleaseFile Candidate,
    ReleaseFile Rollback, ReleaseFile ImageManifest, ReleaseFile WireContract, ReleaseFile SoftwareReview);
public sealed record ImageRange(int Start, int End);
public sealed record FunctionalImageManifest(int Schema, string Classification, int AppStart, int AppEnd,
    int ImageLength, string CandidateSha256, string RollbackSha256, string PagePolicy, ImageRange[] AllowedChanges);
public sealed record ReleaseWireContract(int Schema, string Classification, int AppStart, int AppEnd,
    int PageSize, int PageCount, string PagePolicy, string BootRawVersion);
public sealed record SoftwareReleaseReview(int Schema, string Classification, string ReleaseId,
    string CandidateSha256, string RollbackSha256, string ImageManifestSha256, string WireContractSha256);
public sealed record WorkerPin(string WorkerId, string Executable, string Sha256, string CoreManifestSha256,
    Dictionary<string, string> RuntimeFiles);
public sealed record CatalogEntry(string ReleaseId, string ReleaseManifestSha256, string CandidateSha256,
    string RollbackSha256, string ImageManifestSha256, string WireContractSha256, string SoftwareReviewSha256,
    WorkerPin Worker, RetainedBaselineReview? RetainedBaseline = null);
public sealed record TrustedCatalog(int Schema, string Classification, CatalogEntry[] Releases);

public static partial class FirmwareFiles
{
    private static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
    public const int ImageLength = 225280;
    public const string PagePolicy = "OFFICIAL_4K_ALL55_V1";
    public static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    public static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
    public static bool Identifier(string? value) => value is not null && IdentifierRegex().IsMatch(value);
    public static void Hash(string? hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new InvalidDataException("Expected canonical lower-case SHA-256");
    }
    public static void Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 180 || path.Contains('\\') || path.Contains(':') || path.Contains('%') ||
            path.Split('/').Any(s => !Identifier(s) || s is "." or ".." || s.EndsWith('.') ||
            ReservedNames.Contains(s.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Noncanonical relative package path");
    }
    public static void NoReparse(string path)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse paths forbidden");
    }
    public static T Parse<T>(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        void Unique(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Null) throw new InvalidDataException("Explicit null JSON fields/elements are not permitted");
            if (e.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON property"); Unique(p.Value); }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) Unique(v);
        }
        // Nullable optional record fields are used by internal receipts/configuration. Their nulls
        // are permitted; closed release/catalog data records must never contain a null.
        if (typeof(T) == typeof(FirmwareRelease) || typeof(T) == typeof(FunctionalImageManifest) ||
            typeof(T) == typeof(ReleaseWireContract) || typeof(T) == typeof(SoftwareReleaseReview)) Unique(doc.RootElement);
        else ValidateUnique(doc.RootElement);
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing JSON object"); }
        catch (JsonException ex) { throw new InvalidDataException("Missing, null or invalid JSON field: " + ex.Message, ex); }
    }
    private static void ValidateUnique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in e.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property"); ValidateUnique(property.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var value in e.EnumerateArray()) ValidateUnique(value);
    }
    public static byte[] ReadBounded(string path, int maximum)
    {
        NoReparse(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Local file exceeds its bounded read policy");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    public static void CheckImage(byte[] image)
    {
        if (image.Length != ImageLength || !image.AsSpan(0, 32).ContainsAnyExcept((byte)0xFF)) throw new InvalidDataException("Image length/vector mismatch");
        uint sum = 0;
        for (var i = 0; i < image.Length - 4; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i, 4)));
        if (sum != BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(image.Length - 4))) throw new InvalidDataException("Image checkword mismatch");
    }
    public static void CreateNew(string path, ReadOnlySpan<byte> bytes)
    {
        NoReparse(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(bytes); file.Flush(true);
    }
}

/// <summary>In-memory bounded inspection; never extracts executable files or touches devices.</summary>
public sealed class ReleasePackage
{
    private readonly Dictionary<string, byte[]> _files;
    private readonly byte[] _rawZip;
    public FirmwareRelease Release { get; }
    public string BundleSha256 { get; }
    public string ManifestSha256 { get; }
    private ReleasePackage(FirmwareRelease release, byte[] rawZip, Dictionary<string, byte[]> files)
    { Release = release; _rawZip = rawZip; BundleSha256 = FirmwareFiles.Sha(rawZip); _files = files; ManifestSha256 = FirmwareFiles.Sha(files["release.json"]); }
    public void MaterializeSnapshot(string path) => FirmwareFiles.CreateNew(path, _rawZip);
    public byte[] Read(ReleaseFile file) => _files[file.Path].ToArray();
    public static ReleasePackage Load(string path)
    {
        FirmwareFiles.NoReparse(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("Package too large");
        var raw = new byte[checked((int)input.Length)]; input.ReadExactly(raw);
        using var zip = new ZipArchive(new MemoryStream(raw), ZipArchiveMode.Read);
        if (zip.Entries.Count is < 6 or > 16) throw new InvalidDataException("Package entry bound");
        long total = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            FirmwareFiles.Relative(entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000 or 0x4000 || (entry.ExternalAttributes & 0x410) != 0)
                throw new InvalidDataException("Links/directories/reparse entries forbidden");
            if (entry.Length is < 1 or > 1024 * 1024 || (total += entry.Length) > 4 * 1024 * 1024 ||
                entry.Length > Math.Max(1, entry.CompressedLength) * 256 || files.ContainsKey(entry.FullName))
                throw new InvalidDataException("Duplicate/size/compression bound");
            using var stream = entry.Open();
            var bytes = new byte[checked((int)entry.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Entry size differs");
            files.Add(entry.FullName, bytes);
        }
        if (!files.Keys.Contains("release.json", StringComparer.Ordinal) || !files.TryGetValue("release.json", out var releaseBytes) || releaseBytes.Length > 32768) throw new InvalidDataException("Missing canonical release.json");
        var r = FirmwareFiles.Parse<FirmwareRelease>(releaseBytes);
        if (r.Schema != 1 || r.Classification != "UNIFIEDRGB_KEYBOARD_APP_RELEASE_V1" || !FirmwareFiles.Identifier(r.ReleaseId) ||
            r.Title.Length is < 1 or > 160 || r.Version.Length is < 1 or > 80 || r.Notes.Length > 8192 ||
            r.NormalModel != "1C24" || r.NormalFirmwareHeader != "001200000025000100" || r.BootModel != "1C26" ||
            r.BootRawVersion != "02000200" || r.AppStart != 114688 || r.AppEnd != 339968 || r.PagePolicy != FirmwareFiles.PagePolicy)
            throw new InvalidDataException("Unsupported release schema/target/range");
        var refs = new[] { r.Candidate, r.Rollback, r.ImageManifest, r.WireContract, r.SoftwareReview };
        if (!r.Candidate.Path.EndsWith(".bin", StringComparison.Ordinal) || !r.Rollback.Path.EndsWith(".bin", StringComparison.Ordinal) ||
            !r.ImageManifest.Path.EndsWith(".json", StringComparison.Ordinal) || !r.WireContract.Path.EndsWith(".json", StringComparison.Ordinal) || !r.SoftwareReview.Path.EndsWith(".json", StringComparison.Ordinal))
            throw new InvalidDataException("Only BIN images and JSON data manifests permitted");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "release.json" };
        foreach (var f in refs)
        {
            FirmwareFiles.Relative(f.Path); FirmwareFiles.Hash(f.Sha256);
            if (!allowed.Add(f.Path) || !files.Keys.Contains(f.Path, StringComparer.Ordinal) || !files.TryGetValue(f.Path, out var bytes) || FirmwareFiles.Sha(bytes) != f.Sha256)
                throw new InvalidDataException("Missing/aliased/hash-mismatched release data");
        }
        if (files.Keys.Any(k => !allowed.Contains(k))) throw new InvalidDataException("Unreferenced entry; catalogs/approvals/runners forbidden");
        FirmwareFiles.CheckImage(files[r.Candidate.Path]); FirmwareFiles.CheckImage(files[r.Rollback.Path]);
        var m = FirmwareFiles.Parse<FunctionalImageManifest>(files[r.ImageManifest.Path]);
        if (m.Schema != 1 || m.Classification != "UNIFIEDRGB_FUNCTIONAL_IMAGE_MANIFEST_V1" || m.AppStart != r.AppStart || m.AppEnd != r.AppEnd ||
            m.ImageLength != FirmwareFiles.ImageLength || m.CandidateSha256 != r.Candidate.Sha256 || m.RollbackSha256 != r.Rollback.Sha256 ||
            m.PagePolicy != r.PagePolicy || m.AllowedChanges.Length is < 1 or > 256)
            throw new InvalidDataException("Image manifest mismatch");
        var last = r.AppStart;
        foreach (var range in m.AllowedChanges)
        { if (range.Start < last || range.End <= range.Start || range.End > r.AppEnd) throw new InvalidDataException("Invalid manifest change range"); last = range.End; }
        var candidate = files[r.Candidate.Path]; var rollback = files[r.Rollback.Path];
        for (var i = 0; i < candidate.Length; i++)
            if (candidate[i] != rollback[i] && !m.AllowedChanges.Any(a => i + r.AppStart >= a.Start && i + r.AppStart < a.End))
                throw new InvalidDataException("Changed byte outside reviewed manifest range");
        var w = FirmwareFiles.Parse<ReleaseWireContract>(files[r.WireContract.Path]);
        if (w != new ReleaseWireContract(1, "UNIFIEDRGB_R6_WIRE_CONTRACT_V1", r.AppStart, r.AppEnd, 4096, 55, r.PagePolicy, r.BootRawVersion))
            throw new InvalidDataException("Unsupported wire contract");
        var review = FirmwareFiles.Parse<SoftwareReleaseReview>(files[r.SoftwareReview.Path]);
        if (review != new SoftwareReleaseReview(1, "UNIFIEDRGB_SOFTWARE_RELEASE_REVIEW_V1", r.ReleaseId, r.Candidate.Sha256, r.Rollback.Sha256, r.ImageManifest.Sha256, r.WireContract.Sha256))
            throw new InvalidDataException("Software review mismatch (not physical authorization)");
        return new(r, raw, files);
    }
}
