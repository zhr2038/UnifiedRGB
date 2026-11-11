namespace UnifiedRgb.Firmware;

/// <summary>Already bounded, locked-read bytes; no path reopening during startup admission.</summary>
public interface IInstalledRuntimeSnapshot
{
    IEnumerable<string> FileNames { get; }
    string Hash(string relativePath);
}
