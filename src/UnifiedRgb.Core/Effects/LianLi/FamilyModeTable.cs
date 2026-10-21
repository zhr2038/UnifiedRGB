using System.Text.Json;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects.LianLi;

/// <summary>
/// One per-family compatibility mode resolved to an application generator and preset. Historical metadata
/// (M10: TL, H2): <c>VendorMode</c> is the vendor's enum value, <c>VendorFrames</c> the frame count L-Connect generates for
/// 1..4 fans (one value = independent of the fan count) and <c>VendorIntervalBaseMs</c> its per-frame base (× SpeedType).
/// <c>VendorName</c> is the vendor enum name (WP-12: H2 from <c>slv3.models.H2Effects</c>; enum value = <c>Index</c>, body =
/// <c>VendorMode</c>, e.g. H2 9 Voice → H2Mode10). <c>VendorSelected</c> now selects the procedural uploaded-loop route,
/// not manufacturer pixel content. Historical frame counts describe the reference only, never current encoded output.
/// </summary>
public sealed record FamilyMode(string Family, int Index, string EffectId, EffectParams Preset, LayoutKind LayoutHint,
    int? VendorMode = null, IReadOnlyList<int>? VendorFrames = null, double? VendorIntervalBaseMs = null,
    string? VendorName = null, bool VendorSelected = false)
{
    /// <summary>GUI text: the effect, or "Voice (generic Breathing)" when the vendor name differs and no body is selected.</summary>
    public string Label => VendorName is { } n && !string.Equals(n, EffectId, StringComparison.Ordinal)
        ? $"{n} ({(VendorSelected ? EffectId : $"generic {EffectId}")})"
        : EffectId;

    /// <summary>The vendor's frame count for a chain of <paramref name="fans"/> fans, or null when not pinned.</summary>
    public int? VendorFramesFor(int fans) => VendorFrames switch
    {
        null or { Count: 0 } => null,
        { Count: 1 } f => f[0],
        var f when fans >= 1 && fans <= f.Count => f[fans - 1],
        _ => null,
    };
}

/// <summary>
/// Maps every &lt;Family&gt;ModeN to (EffectId, preset params, layout hint) from the embedded lianli-modes.json
/// (ARCHITECTURE §5.1). The GUI shows the per-family list; the generator is shared.
/// </summary>
public sealed class FamilyModeTable
{
    private readonly Dictionary<string, FamilyMode[]> _families;

    private FamilyModeTable(Dictionary<string, FamilyMode[]> families, string hardwareNote)
    {
        _families = families;
        Note = hardwareNote;
    }

    public static FamilyModeTable Default { get; } = LoadEmbedded();

    public string Note { get; }

    public IReadOnlyCollection<string> Families => _families.Keys;

    public IReadOnlyList<FamilyMode> Modes(string family) =>
        _families.TryGetValue(family, out var m) ? m : throw new KeyNotFoundException($"Unknown Lian Li family '{family}'.");

    /// <summary>modeIndex is 1-based, matching the vendor's &lt;Family&gt;ModeN naming.</summary>
    public FamilyMode Resolve(string family, int modeIndex)
    {
        var modes = Modes(family);
        if (modeIndex < 1 || modeIndex > modes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(modeIndex), $"{family} has modes 1..{modes.Count}.");
        }

        return modes[modeIndex - 1];
    }

    public static FamilyModeTable Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var families = new Dictionary<string, FamilyMode[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var fam in root.GetProperty("families").EnumerateArray())
        {
            var name = fam.GetProperty("family").GetString()!;
            var hint = Enum.Parse<LayoutKind>(fam.GetProperty("layoutHint").GetString()!);
            var list = new List<FamilyMode>();
            foreach (var m in fam.GetProperty("modes").EnumerateArray())
            {
                var preset = new EffectParams(
                    Array.Empty<Rgb>(),
                    m.TryGetProperty("speed", out var sp) ? sp.GetSingle() : 0.5f,
                    1f,
                    m.TryGetProperty("direction", out var d) ? Enum.Parse<Direction>(d.GetString()!) : Direction.Forward);
                list.Add(new FamilyMode(
                    name,
                    m.GetProperty("index").GetInt32(),
                    m.GetProperty("effect").GetString()!,
                    preset,
                    hint,
                    m.TryGetProperty("vendorMode", out var vm) ? vm.GetInt32() : null,
                    m.TryGetProperty("vendorFrames", out var vf) ? vf.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null,
                    m.TryGetProperty("vendorIntervalBaseMs", out var vi) ? vi.GetDouble() : null,
                    m.TryGetProperty("vendorName", out var vn) ? vn.GetString() : null,
                    m.TryGetProperty("vendorSelected", out var vs) && vs.GetBoolean()));
            }

            if (list.Select((x, i) => x.Index == i + 1).Any(ok => !ok))
            {
                throw new InvalidDataException($"Family {name}: mode indices must be 1..N in order.");
            }

            families[name] = [.. list];
        }

        return new FamilyModeTable(families, root.TryGetProperty("note", out var n) ? n.GetString() ?? string.Empty : string.Empty);
    }

    private static FamilyModeTable LoadEmbedded()
    {
        using var s = typeof(FamilyModeTable).Assembly.GetManifestResourceStream("UnifiedRgb.Core.lianli-modes.json")
            ?? throw new InvalidOperationException("lianli-modes.json resource missing");
        return Parse(s);
    }
}
