using System.Text.Json;
using System.Text.RegularExpressions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Profiles as JSON files in <c>&lt;DataDirectory&gt;\profiles</c> (ARCHITECTURE §6). Every write is atomic: the bytes go to
/// a unique temp file in the same directory, are flushed to disk, and only then replace the target by rename, so a crash at
/// any point leaves either the old or the new file, never a torn one. Names are a strict allow-list (no paths).
/// </summary>
public sealed partial class ProfileStore
{
    public const string ActiveName = "active";
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions { WriteIndented = true });

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ActiveName, "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly object _gate = new();

    public ProfileStore(string directory) => Directory = directory ?? throw new ArgumentNullException(nameof(directory));

    public ProfileStore(UnifiedRgbOptions options)
        : this(Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).ResolvedDataDirectory, "profiles"))
    {
    }

    public string Directory { get; }

    /// <summary>Test seam: runs after the temp file is written and flushed, before the rename (simulates a crash when it throws).</summary>
    internal Action<string>? BeforeCommit { get; set; }
    internal Action<ProfileDto>? BeforeReferenceCommit { get; set; }

    public static bool IsValidName(string? name) =>
        name is not null && NamePattern().IsMatch(name) && !name.EndsWith('.') && !name.EndsWith(' ')
        && !Reserved.Contains(name.Split('.')[0].TrimEnd(' '));

    public IReadOnlyList<ProfileInfoDto> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return new DirectoryInfo(Directory).EnumerateFiles("*.json")
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTimeUtc))
            .Where(f => IsValidName(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ProfileInfoDto(f.Name, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToArray();
    }

    // Reference inspection must stop before materializing an arbitrarily large file-name list.
    internal List<string> ReferenceNames()
    {
        OwnedLibraryPaths.CheckAncestors(Directory);
        var names = new List<string>();
        if (!System.IO.Directory.Exists(Directory)) return names;
        var entries = 0;
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*.json"))
        {
            if (++entries > 1025) throw new InvalidOperationException("Saved-scene reference inspection exceeded1024 scenes; nothing was removed.");
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Saved-scene references contain a link; nothing was removed.");
            var name = Path.GetFileNameWithoutExtension(file.Name);
            if (IsValidName(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Throws KeyNotFoundException when absent, ArgumentException for a bad name, InvalidDataException when unreadable.</summary>
    public ProfileDto Load(string name)
    {
        RequireName(name);
        return Read(PathOf(name)) ?? throw new KeyNotFoundException($"Profile '{name}' does not exist.");
    }

    public void Save(ProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RequireName(profile.Name);
        Write(PathOf(profile.Name), profile);
    }

    public bool Delete(string name)
    {
        RequireName(name);
        return SceneStateFiles.Coordinate(StateRoot, () =>
        {
            OwnedLibraryPaths.CheckAncestors(Directory);
            var path = PathOf(name);
            if (!File.Exists(path)) return false;
            OwnedLibraryPaths.CheckAncestors(path);
            File.Delete(path);
            return true;
        });
    }

    internal ProfileDto? ReadActiveForReferences() => Read(PathOf(ActiveName));

    /// <summary>The scene to restore at start; null when there is none.</summary>
    public ProfileDto? LoadActive()
    {
        if (new DirectoryInfo(Directory).Name.Equals("profiles", StringComparison.OrdinalIgnoreCase)) SceneStateFiles.Recover(StateRoot);
        return Read(PathOf(ActiveName));
    }

    private string StateRoot => new DirectoryInfo(Directory).Parent!.FullName;

    internal Action? BeforeActiveCommit { get; set; }

    /// <summary>Commit the existing active.json and prepared screen files at one durable recovery point.</summary>
    internal void SaveActiveWith(ProfileDto profile, IReadOnlyList<SceneStateFile> additional)
    {
        var bytes = Serialize(profile);
        RequireSize(bytes);
        BeforeActiveCommit?.Invoke();
        if (!new DirectoryInfo(Directory).Name.Equals("profiles", StringComparison.OrdinalIgnoreCase))
        {
            if (additional.Count != 0) throw new InvalidOperationException("Joint scene state needs the standard profiles directory.");
            Write(PathOf(ActiveName), profile);
            return;
        }
        SceneStateFiles.Commit(StateRoot, [new SceneStateFile("profiles/active.json", bytes), .. additional]);
    }

    public void SaveActive(ProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SaveActiveWith(profile, []);
    }

    public static byte[] Serialize(ProfileDto profile) => JsonSerializer.SerializeToUtf8Bytes(profile, Json);

    private static void RequireName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("场景名称需以中文、字母或数字开头，最多 64 个字符，可包含空格、下划线、短横线和点；不能使用系统保留名称。", nameof(name));
        }
    }

    private static ProfileDto? Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException($"{info.Name} exceeds {MaxFileBytes} bytes.");
        }

        try
        {
            var p = JsonSerializer.Deserialize<ProfileDto>(File.ReadAllBytes(path), Json);
            if (p?.Scene?.Assignments is null || p.SchemaVersion is < 1 or > ProfileDto.CurrentSchema)
            {
                throw new InvalidDataException($"{info.Name} is not a supported profile.");
            }

            return p;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{info.Name} is not valid JSON: {ex.Message}", ex);
        }
    }

    private string PathOf(string name) => Path.Combine(Directory, name + ".json");

    private void Write(string path, ProfileDto profile)
    {
        var bytes = Serialize(profile);
        RequireSize(bytes);
        SceneStateFiles.Coordinate(StateRoot, () =>
        {
          lock (_gate)
          {
            BeforeReferenceCommit?.Invoke(profile);
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }

                BeforeCommit?.Invoke(tmp);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
          }
          return 0;
        });
    }

    internal static void RequireSize(byte[] bytes)
    {
        if (bytes.LongLength > MaxFileBytes)
        {
            throw new ArgumentException($"The serialized scene exceeds {MaxFileBytes} bytes; the previous file and running scene were kept.", nameof(bytes));
        }
    }

    [GeneratedRegex("^[\\p{L}\\p{Nd}][\\p{L}\\p{Nd} _.·\\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
