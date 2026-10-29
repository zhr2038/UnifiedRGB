namespace UnifiedRgb.Devices.AsusRog;

/// <summary>A reference-derived key map with its provenance. It is a scan seed and review input, never a physical map.</summary>
public sealed record RogPerKeyCandidate(PerKeyMap Map, string Provenance);

/// <summary>
/// The ANSI Azoth row/LED table of the local compatibility reference (AuraKeyboardLayouts.cs "Azoth", SHA-256
/// 37f8ebdd2a3ccb5b62b0765a5627cbdc116c5bee21e440fe547776650894990f), as an EXPLICITLY UNVERIFIED candidate for the Azoth X.
/// Complete and Verified are always false and Evidence is null, so <see cref="PerKeyMaps.Evaluate"/> can never report it Ready:
/// it only gives the physical scan a key list to confirm or correct. Columns are key centres in eighth-key units from the
/// reference widths, so a projection of a confirmed map keeps the real stagger; rows are the six reference rows.
/// </summary>
public static class RogAzothCandidateMap
{
    public const string Model = "ROG Azoth X (ANSI candidate)";
    public const string Provenance =
        "Compatibility reference AuraKeyboardLayouts.cs 'Azoth' (SHA-256 37f8ebdd2a3ccb5b62b0765a5627cbdc116c5bee21e440fe547776650894990f); not a physical scan, not Azoth X optical proof.";

    // Name and width (in keys) per reference row; LED ids follow the reference LedIds order row by row.
    private static readonly (string Name, float Width)[][] Rows =
    [
        [("Esc", 1), ("F1", 1), ("F2", 1), ("F3", 1), ("F4", 1), ("F5", 1), ("F6", 1), ("F7", 1), ("F8", 1), ("F9", 1), ("F10", 1), ("F11", 1), ("F12", 1)],
        [("`", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1), ("6", 1), ("7", 1), ("8", 1), ("9", 1), ("0", 1), ("-", 1), ("=", 1), ("Bksp", 2), ("Ins", 1)],
        [("Tab", 1.5f), ("Q", 1), ("W", 1), ("E", 1), ("R", 1), ("T", 1), ("Y", 1), ("U", 1), ("I", 1), ("O", 1), ("P", 1), ("[", 1), ("]", 1), ("\\", 1.5f), ("Del", 1)],
        [("Caps", 1.75f), ("A", 1), ("S", 1), ("D", 1), ("F", 1), ("G", 1), ("H", 1), ("J", 1), ("K", 1), ("L", 1), (";", 1), ("'", 1), ("Enter", 2.25f), ("Pg Up", 1)],
        [("Shift", 2.25f), ("Z", 1), ("X", 1), ("C", 1), ("V", 1), ("B", 1), ("N", 1), ("M", 1), (",", 1), (".", 1), ("/", 1), ("Right Shift", 1.75f), ("↑", 1), ("Pg Dn", 1)],
        [("Ctrl", 1.25f), ("Win", 1.25f), ("Alt", 1.25f), ("Space", 6.25f), ("Right Alt", 1), ("Fn", 1), ("Right Ctrl", 1), ("←", 1), ("↓", 1), ("→", 1)],
    ];

    private static readonly byte[] LedIds =
    [
        0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60,
        0x01, 0x09, 0x11, 0x19, 0x21, 0x29, 0x31, 0x39, 0x41, 0x49, 0x51, 0x59, 0x61, 0x69, 0x79,
        0x02, 0x0A, 0x12, 0x1A, 0x22, 0x2A, 0x32, 0x3A, 0x42, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x7A,
        0x03, 0x0B, 0x13, 0x1B, 0x23, 0x2B, 0x33, 0x3B, 0x43, 0x4B, 0x53, 0x5B, 0x6B, 0x7B,
        0x04, 0x14, 0x1C, 0x24, 0x2C, 0x34, 0x3C, 0x44, 0x4C, 0x54, 0x5C, 0x64, 0x74, 0x7C,
        0x05, 0x0D, 0x15, 0x35, 0x55, 0x5D, 0x65, 0x6D, 0x75, 0x7D,
    ];

    /// <summary>The candidate for <paramref name="transport"/>; the cells are identical for every transport.</summary>
    public static RogPerKeyCandidate For(string transport)
    {
        ArgumentException.ThrowIfNullOrEmpty(transport);
        var keys = new List<PerKeyCell>(LedIds.Length);
        for (var row = 0; row < Rows.Length; row++)
        {
            var x = 0f;
            foreach (var (name, width) in Rows[row])
            {
                keys.Add(new(LedIds[keys.Count], name, row, (int)MathF.Round((x + width / 2) * 8)));
                x += width;
            }
        }
        if (keys.Count != LedIds.Length) throw new InvalidOperationException("Candidate rows and LED ids differ.");
        return new(new(Model, transport, keys.AsReadOnly(), Complete: false, Verified: false, Evidence: null), Provenance);
    }
}
