using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UnifiedRgb.App.Diagnostics;

/// <summary>
/// "Export diagnostics" (ARCHITECTURE §10): a zip of Host/App logs, status.json and the active profile. Every text entry is
/// redacted — Lian Li RF MAC addresses become stable short hashes and user profile paths lose the user name — so the zip can
/// be attached to a bug report. Files the Host is still writing are read with FileShare.ReadWrite.
/// </summary>
public static partial class DiagnosticsExporter
{
    public const long MaxEntryBytes = 20 * 1024 * 1024;

    /// <param name="extra">Additional generated entries (e.g. "packets.json" from GetStatus(diagnostics: true)); redacted too.</param>
    public static IReadOnlyList<string> Export(string hostDataDirectory, string appLogDirectory, string zipPath, IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var sources = new List<(string Entry, string Path)>();
        AddFiles(sources, Path.Combine(hostDataDirectory, "logs"), "*.log", "logs/host");
        AddFiles(sources, appLogDirectory, "*.log", "logs/app");
        AddFile(sources, Path.Combine(hostDataDirectory, "status.json"), "status.json");
        AddFile(sources, Path.Combine(hostDataDirectory, "profiles", "active.json"), "active-profile.json");

        var tmp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var (entry, path) in sources)
            {
                var text = ReadShared(path);
                var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(Redact(text));
            }

            foreach (var (entry, text) in extra ?? new Dictionary<string, string>())
            {
                var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(Redact(text));
            }
        }

        File.Move(tmp, zipPath, overwrite: true);
        return sources.Select(s => s.Entry).Concat(extra?.Keys ?? []).ToArray();
    }

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = MacPattern().Replace(text, m => "MAC-" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Normalize(m.Value))))[..8]);
        return UserPathPattern().Replace(text, m => m.Groups[1].Value + "<user>");
    }

    // "AA:BB:…" and "aa-bb-…" are the same radio: hash the bare upper-case hex so redacted logs stay correlatable.
    private static string Normalize(string mac) => mac.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static void AddFiles(List<(string, string)> list, string dir, string pattern, string prefix)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(($"{prefix}/{Path.GetFileName(f)}", f));
        }
    }

    private static void AddFile(List<(string, string)> list, string path, string entry)
    {
        if (File.Exists(path))
        {
            list.Add((entry, path));
        }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxEntryBytes)
        {
            fs.Seek(-MaxEntryBytes, SeekOrigin.End); // keep the newest part of a huge log
        }

        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }

    [GeneratedRegex("\\b[0-9A-Fa-f]{2}(?:[-:][0-9A-Fa-f]{2}){5}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();

    [GeneratedRegex("((?:[A-Za-z]:)?(?:\\\\\\\\|\\\\|/)Users(?:\\\\\\\\|\\\\|/))[^\\\\/\"\\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UserPathPattern();
}
