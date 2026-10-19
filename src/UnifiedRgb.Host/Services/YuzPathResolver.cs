using Microsoft.Win32;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Default <c>LianLi:YuzDllPath</c> (ARCHITECTURE-D2 §3): the L-Connect 3 install directory, else the InstallLocation of its
/// Uninstall registry entry. Read-only: the dll is loaded in place by the adapter, never copied. Lives in the Host because
/// adapters may not use the registry (LayeringTests).
/// </summary>
public static class YuzPathResolver
{
    public const string DllName = "yuz.dll";

    public static string InstallSubdirectory => Path.Combine("Lian-Li", "L-Connect 3");

    /// <summary><paramref name="configured"/> when set; otherwise the first existing candidate; otherwise empty (→ Blocked).</summary>
    public static string Resolve(string? configured, Func<string, bool>? exists = null, Func<IEnumerable<string>>? registryLocations = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        exists ??= File.Exists;
        foreach (var c in Candidates(registryLocations ?? UninstallLocations))
        {
            if (exists(c))
            {
                return c;
            }
        }

        return string.Empty;
    }

    public static IEnumerable<string> Candidates(Func<IEnumerable<string>> registryLocations)
    {
        ArgumentNullException.ThrowIfNull(registryLocations);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
        {
            yield return Path.Combine(pf, InstallSubdirectory, DllName);
        }

        yield return Path.Combine(@"D:\Program Files", InstallSubdirectory, DllName);
        foreach (var dir in registryLocations())
        {
            yield return Path.Combine(dir, DllName);
        }
    }

    /// <summary>InstallLocation of every HKLM Uninstall entry whose DisplayName mentions L-Connect 3.</summary>
    public static IEnumerable<string> UninstallLocations()
    {
        var found = new List<string>();
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(name);
                    if (app?.GetValue("DisplayName") is string display && display.Contains("L-Connect 3", StringComparison.OrdinalIgnoreCase)
                        && app.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                    {
                        found.Add(loc.Trim('"'));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // unreadable hive: fall through to Blocked(yuz.dll missing)
            }
        }

        return found;
    }
}
