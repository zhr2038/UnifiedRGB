using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace UnifiedRgb.Safety.Tests;

/// <summary>
/// ARCHITECTURE §1/§2 from the built assemblies' reference tables: dependencies point down only, Core and the device
/// model have no third-party dependencies, adapters never see the engine, the GUI never touches a device.
/// </summary>
public class LayeringTests
{
    private static readonly string[] Adapters =
        ["UnifiedRgb.Devices.LianLi", "UnifiedRgb.Devices.MsiGpu", "UnifiedRgb.Devices.OpenRgbSdk", "UnifiedRgb.Devices.Virtual"];

    public static TheoryData<string, string[]> Allowed() => new()
    {
        // assembly → the only non-BCL assemblies it may reference
        { "UnifiedRgb.Devices.Abstractions", [] },
        { "UnifiedRgb.Contracts", [] },
        { "UnifiedRgb.Core", ["UnifiedRgb.Devices.Abstractions"] },
        { "UnifiedRgb.Transports.Win", ["UnifiedRgb.Devices.Abstractions", "Microsoft.Extensions.Logging.Abstractions", "System.ServiceProcess.ServiceController"] },
        { "UnifiedRgb.Devices.Virtual", ["UnifiedRgb.Devices.Abstractions"] },
        { "UnifiedRgb.Devices.LianLi", ["UnifiedRgb.Devices.Abstractions", "UnifiedRgb.Transports.Win", "Microsoft.Extensions.Logging.Abstractions"] },
        { "UnifiedRgb.Devices.MsiGpu", ["UnifiedRgb.Devices.Abstractions", "UnifiedRgb.Transports.Win", "Microsoft.Extensions.Logging.Abstractions"] },
        { "UnifiedRgb.Devices.OpenRgbSdk", ["UnifiedRgb.Devices.Abstractions", "UnifiedRgb.Transports.Win", "Microsoft.Extensions.Logging.Abstractions"] },
        { "UnifiedRgb.App", ["UnifiedRgb.Contracts", "UnifiedRgb.Core", "UnifiedRgb.Devices.Abstractions", "StreamJsonRpc", "Serilog", "Serilog.Sinks.File", "PresentationCore", "PresentationFramework", "WindowsBase", "System.Windows.Forms", "System.Drawing.Common", "System.Drawing.Primitives", "System.Xaml", "Microsoft.Win32.Registry" /* HKCU Run autostart */] },
        { "urgb", ["UnifiedRgb.Contracts", "StreamJsonRpc"] },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Assembly_references_only_what_its_layer_allows(string assembly, string[] allowed)
    {
        var extra = References(assembly).Where(r => !IsBcl(r) && !allowed.Contains(r)).ToArray();
        extra.Should().BeEmpty($"{assembly} may only reference the BCL and [{string.Join(", ", allowed)}]");
    }

    [Fact]
    public void Adapters_never_see_the_engine_and_the_gui_never_touches_a_device()
    {
        foreach (var a in Adapters)
        {
            References(a).Should().NotContain(["UnifiedRgb.Core", "UnifiedRgb.Host", "UnifiedRgb.Contracts"], a);
        }

        References("UnifiedRgb.App").Should().NotContain(["UnifiedRgb.Host", "UnifiedRgb.Transports.Win", .. Adapters]);
        References("urgb").Should().NotContain(["UnifiedRgb.Host", "UnifiedRgb.Transports.Win", "UnifiedRgb.Core", .. Adapters]);
    }

    [Fact]
    public void Core_uses_no_windows_specific_api()
    {
        var refs = References("UnifiedRgb.Core");
        refs.Should().NotContain(r => r.Contains("Win32", StringComparison.Ordinal) || r.StartsWith("System.Windows", StringComparison.Ordinal)
            || r == "System.ServiceProcess.ServiceController" || r == "Microsoft.Win32.Registry");
    }

    private static bool IsBcl(string name) =>
        name is "System.Runtime" or "netstandard" or "mscorlib" or "System.Private.CoreLib"
        || name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("System.Windows", StringComparison.Ordinal)
            && name is not "System.ServiceProcess.ServiceController" and not "System.Drawing.Common"
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) && name != "Microsoft.Win32.Registry";

    private static string[] References(string assembly)
    {
        var path = Directory.EnumerateDirectories(Path.Combine(ForbiddenIdentifierScanTests.RepoRoot(), "src"))
            .Where(d => Directory.Exists(Path.Combine(d, "bin")))
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(d, "bin"), assembly + ".dll", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new FileNotFoundException(assembly);
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToArray();
    }
}
