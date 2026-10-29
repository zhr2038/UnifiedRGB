using System.Text.Json;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Services;

/// <summary>One browse step. Only these two values exist; any other numeric value is rejected before any state change.</summary>
public enum SceneCycleMove { Previous, Next }

/// <summary>
/// What the standalone scene cycle (<see cref="SceneCycleCoordinator"/>) is doing or what its latest attempt ended with. These are
/// software states of this coordinator only: nothing here is a physical observation, and no value is shared with any DTO or
/// with <see cref="CasePowerIntent"/>.
/// </summary>
public enum SceneCyclePhase
{
    /// <summary>No valid list is loaded (absent, rejected or not refreshed yet); nothing is selectable or sent.</summary>
    NoList,

    /// <summary>A list is loaded and nothing is pending.</summary>
    Idle,

    /// <summary>The selection moved (or an explicit apply was requested) and waits for the worker; nothing was sent for it yet.</summary>
    Selecting,

    /// <summary>The worker is preparing or sending exactly one request.</summary>
    Applying,

    /// <summary>The case is dark: the selection stays pending (held) and nothing was sent. Only an explicit restore applies it.</summary>
    LightsOff,

    /// <summary>The Host answered the checked transactional package apply with Applied=true for the requested content and binding.</summary>
    Applied,

    /// <summary>ApplyProfile returned without an error: software acceptance by the Host only, no transactional or physical proof.</summary>
    Accepted,

    /// <summary>
    /// The package apply got no usable reply, and the current readback shows exactly the requested content, binding and playback
    /// contract. This describes the current state only; it is not proof that this request performed the change.
    /// </summary>
    ReadbackMatches,

    /// <summary>The Host answered Applied=false: per its contract nothing changed. Not retried without a new explicit action.</summary>
    Refused,

    /// <summary>A check before any write failed (not installed, not ready, not directly applicable, unreadable): nothing was sent.</summary>
    Unavailable,

    /// <summary>A newer scene choice (another client or control) was seen before the write: nothing was sent, the newer scene stays.</summary>
    Conflict,

    /// <summary>The outcome of a sent request is unknown. Never retried or rolled back; automatic apply halts until a new explicit action.</summary>
    Unknown,

    /// <summary>
    /// The outcome of a sent package request is unknown and the current readback shows a different scene. This does not mean the
    /// request never ran. Never retried or rolled back; automatic apply halts until a new explicit action.
    /// </summary>
    UnknownObservedDifferent,

    /// <summary>The coordinator was disposed; nothing more is sent.</summary>
    Closed,
}

/// <summary>
/// One entry of the user-defined ordered list: a scene package by its immutable <see cref="ContentId"/> (its binding is read fresh
/// before every apply, never stored here) or a saved scene (profile) by <see cref="Name"/>.
/// </summary>
public sealed record SceneCycleEntry(string Kind, string? ContentId, string? Name)
{
    public const string PackageKind = "package";
    public const string ProfileKind = "profile";

    public static SceneCycleEntry Package(string contentId) => new(PackageKind, contentId, null);

    public static SceneCycleEntry Profile(string name) => new(ProfileKind, null, name);

    public bool IsPackage => string.Equals(Kind, PackageKind, StringComparison.Ordinal);
}

/// <summary>A validated list: version 1, 1..64 entries in user order. Use <see cref="SceneCycleListFile"/> to read or validate one.</summary>
public sealed record SceneCycleList(int Version, IReadOnlyList<SceneCycleEntry> Entries);

/// <summary>Worker options. SettleDelay (0..10 s, default 300 ms) absorbs fast browsing before the newest selection is sent.</summary>
public sealed record SceneCycleOptions(TimeSpan SettleDelay)
{
    public SceneCycleOptions()
        : this(DefaultSettleDelay)
    {
    }

    public static TimeSpan DefaultSettleDelay => TimeSpan.FromMilliseconds(300);

    public static TimeSpan MaxSettleDelay => TimeSpan.FromSeconds(10);
}

/// <summary>One list position: 1-based index, display name, kind, the selection generation and the last refresh note (if any).</summary>
public sealed record SceneCycleSelection(int Index, string Name, string Kind, long Generation, string? Note = null);

/// <summary>The settled result of one attempt for <see cref="Target"/> (the selection as it was when the attempt started).</summary>
public sealed record SceneCycleOutcome(SceneCyclePhase Phase, SceneCycleSelection Target, string Detail);

/// <summary>
/// A software status snapshot. <see cref="Selection"/> always follows the newest press (selection state); <see cref="Acknowledged"/>
/// is the last attempt this coordinator saw acknowledged (Applied, Accepted or ReadbackMatches) and is history, not a claim about the
/// scene running now. <see cref="LastOutcome"/> may belong to an older selection than <see cref="Selection"/>. Revision increases
/// with every change, so a consumer can drop snapshots that arrive out of order. <see cref="Held"/>: the selection was chosen
/// explicitly but not sent because the case was dark or a center action took precedence; it waits for an explicit restore or a
/// new press and is never sent automatically. The keyboard bridge renders these facts as the OLED caption, never more.
/// </summary>
public sealed record SceneCycleStatus(
    long Revision,
    int Count,
    long Generation,
    SceneCycleSelection? Selection,
    SceneCyclePhase Phase,
    string Detail,
    SceneCycleSelection? InFlight,
    SceneCycleOutcome? LastOutcome,
    SceneCycleOutcome? Acknowledged,
    bool AutoApplyHalted,
    bool Held = false);

/// <summary>
/// The optional user-ordered list format. Nothing creates it: without the file the keyboard bridge builds the list automatically
/// from installed packages whose binding is Ready and usable saved RGB scenes (<see cref="KeyboardSceneCycleBridge"/>); with it, its order is kept:
/// <c>{ "version": 1, "entries": [ { "kind": "package", "contentId": "&lt;64 lowercase hex&gt;" }, { "kind": "profile", "name": "Aurora" } ] }</c>.
/// Strict and fail-closed: any problem rejects the whole list with an ArgumentException naming the entry. Nothing in a file is
/// auto-included, and no name, theme or content heuristic is used.
/// </summary>
public static class SceneCycleListFile
{
    public const int CurrentVersion = 1;
    public const int MaxEntries = 64;
    public const int MaxBytes = 64 * 1024;

    // Names produced by existing code for internal scenes: the lights-off recovery point, the engine's label for direct edits and
    // the mirrored running scene. A saved scene with one of these names is never a cycle entry.
    private static readonly string[] ReservedNames = [CasePowerCoordinator.BackupProfileName, LightingEngine.CustomProfileName, ProfileStore.ActiveName];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 8,
    };

    private static ReadOnlySpan<byte> Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>True for a name reserved by an existing producer (compared OrdinalIgnoreCase, like the protected recovery point).</summary>
    public static bool IsReservedProfileName(string? name) =>
        name is not null && Array.Exists(ReservedNames, r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads at most <see cref="MaxBytes"/> from <paramref name="path"/> and parses it. Never creates or changes a file.</summary>
    public static SceneCycleList Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The scene cycle list does not exist; nothing was loaded.", path);
        }

        if (info.Length > MaxBytes)
        {
            throw new ArgumentException($"The scene cycle list exceeds {MaxBytes} bytes; nothing was loaded.", nameof(path));
        }

        return Parse(File.ReadAllBytes(path));
    }

    public static SceneCycleList Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw new ArgumentException($"The scene cycle list exceeds {MaxBytes} bytes; nothing was loaded.", nameof(utf8Json));
        }

        if (utf8Json.StartsWith(Utf8Bom))
        {
            utf8Json = utf8Json[Utf8Bom.Length..];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The scene cycle list is not valid JSON ({ex.Message}); nothing was loaded.", nameof(utf8Json), ex);
        }

        using (document)
        {
            return Validate(Read(document.RootElement));
        }
    }

    /// <summary>Validates a list built in code (or by a loader) and returns an immutable copy; throws ArgumentException otherwise.</summary>
    public static SceneCycleList Validate(SceneCycleList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.Version != CurrentVersion)
        {
            throw new ArgumentException($"Scene cycle list version {list.Version} is not supported (only {CurrentVersion}); nothing was loaded.", nameof(list));
        }

        if (list.Entries is null || list.Entries.Count is < 1 or > MaxEntries)
        {
            throw new ArgumentException($"A scene cycle list has 1 to {MaxEntries} entries; nothing was loaded.", nameof(list));
        }

        var packages = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // saved scenes are file names on Windows
        var copy = new SceneCycleEntry[list.Entries.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var entry = list.Entries[i] ?? throw Bad(i, "is empty");
            switch (entry.Kind)
            {
                case SceneCycleEntry.PackageKind:
                    if (entry.Name is not null)
                    {
                        throw Bad(i, "is a package entry and has only kind and contentId");
                    }

                    if (!ScenePackageFormat.IsSha256Hex(entry.ContentId))
                    {
                        throw Bad(i, "needs a contentId of exactly 64 lowercase hex characters");
                    }

                    if (!packages.Add(entry.ContentId!))
                    {
                        throw Bad(i, "is a duplicate of an earlier package entry");
                    }

                    copy[i] = SceneCycleEntry.Package(entry.ContentId!);
                    break;
                case SceneCycleEntry.ProfileKind:
                    if (entry.ContentId is not null)
                    {
                        throw Bad(i, "is a saved-scene entry and has only kind and name");
                    }

                    if (IsReservedProfileName(entry.Name))
                    {
                        throw Bad(i, "names a reserved internal scene (lights-off recovery point, direct-edit label or running-scene mirror)");
                    }

                    if (!ProfileStore.IsValidName(entry.Name))
                    {
                        throw Bad(i, "needs a valid saved-scene name");
                    }

                    if (!profiles.Add(entry.Name!))
                    {
                        throw Bad(i, "is a duplicate of an earlier saved-scene entry");
                    }

                    copy[i] = SceneCycleEntry.Profile(entry.Name!);
                    break;
                default:
                    throw Bad(i, $"has unknown kind '{Shown(entry.Kind)}' (only '{SceneCycleEntry.PackageKind}' or '{SceneCycleEntry.ProfileKind}')");
            }
        }

        return new SceneCycleList(CurrentVersion, copy);
    }

    private static SceneCycleList Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The scene cycle list must be a JSON object; nothing was loaded.");
        }

        int? version = null;
        JsonElement? entries = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "version":
                    if (version is not null)
                    {
                        throw new ArgumentException("The scene cycle list has 'version' more than once; nothing was loaded.");
                    }

                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var v))
                    {
                        throw new ArgumentException("The scene cycle list 'version' must be an integer; nothing was loaded.");
                    }

                    version = v;
                    break;
                case "entries":
                    if (entries is not null)
                    {
                        throw new ArgumentException("The scene cycle list has 'entries' more than once; nothing was loaded.");
                    }

                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw new ArgumentException("The scene cycle list 'entries' must be an array; nothing was loaded.");
                    }

                    entries = property.Value;
                    break;
                default:
                    throw new ArgumentException($"The scene cycle list has unknown property '{Shown(property.Name)}'; nothing was loaded.");
            }
        }

        if (version is null || entries is not { } array)
        {
            throw new ArgumentException("The scene cycle list needs 'version' and 'entries'; nothing was loaded.");
        }

        var count = array.GetArrayLength();
        if (count is < 1 or > MaxEntries)
        {
            throw new ArgumentException($"A scene cycle list has 1 to {MaxEntries} entries (found {count}); nothing was loaded.");
        }

        var list = new SceneCycleEntry[count];
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            list[index] = ReadEntry(element, index);
            index++;
        }

        return new SceneCycleList(version.Value, list);
    }

    private static SceneCycleEntry ReadEntry(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Bad(index, "must be a JSON object");
        }

        string? kind = null;
        string? contentId = null;
        string? name = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("kind" or "contentId" or "name"))
            {
                throw Bad(index, $"has unknown property '{Shown(property.Name)}'");
            }

            if (!seen.Add(property.Name))
            {
                throw Bad(index, $"has '{property.Name}' more than once");
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw Bad(index, $"'{property.Name}' must be a string");
            }

            var value = property.Value.GetString();
            switch (property.Name)
            {
                case "kind": kind = value; break;
                case "contentId": contentId = value; break;
                default: name = value; break;
            }
        }

        return kind is null ? throw Bad(index, "needs a 'kind'") : new SceneCycleEntry(kind, contentId, name);
    }

    private static ArgumentException Bad(int index, string problem) =>
        new($"Scene cycle list entry #{index + 1} (entries[{index}]) {problem}; the whole list was rejected.");

    private static string Shown(string? value)
    {
        if (value is null)
        {
            return "(none)";
        }

        var text = value.Length > 40 ? value[..40] + "..." : value;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }
}
