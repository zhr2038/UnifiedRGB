namespace UnifiedRgb.Devices.Tests;

/// <summary>Reports absent private prerequisites as real xUnit skips, never as successful measurements.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalCodecFactAttribute : FactAttribute
{
    public LocalCodecFactAttribute()
    {
        if (!File.Exists(YuzCodecTests.DllPath))
        {
            Skip = "Local codec unavailable. Set URGB_YUZ_DLL to a legally obtained x64 codec to run this offline check.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalOwnedCodecFactAttribute : FactAttribute
{
    public LocalOwnedCodecFactAttribute()
    {
        if (!File.Exists(Path.Combine(Fixture.RepoRoot(), YuzCodecTests.OwnedCodecRelative)))
        {
            Skip = "Pinned private codec unavailable; the recorded compressed-size measurement was not run.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalCaptureFactAttribute : FactAttribute
{
    public LocalCaptureFactAttribute()
    {
        if (!File.Exists(Path.Combine(Fixture.RepoRoot(), "analysis", "lconnect-config", "savedDevices.config")))
        {
            Skip = "Private vendor capture unavailable; public tests use synthetic topology instead.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalCaptureCodecFactAttribute : FactAttribute
{
    public LocalCaptureCodecFactAttribute()
    {
        var directory = Path.Combine(Fixture.RepoRoot(), "analysis", "lconnect-config");
        if (!File.Exists(YuzCodecTests.DllPath) || !File.Exists(Path.Combine(directory, "savedDevices.config"))
            || Directory.GetFiles(directory, "*.effect").Length != 4)
        {
            Skip = "Private codec and four original capture records are required for this provenance check.";
        }
    }
}
