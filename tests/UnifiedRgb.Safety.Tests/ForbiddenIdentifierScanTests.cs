using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Safety.Tests;

/// <summary>
/// ARCHITECTURE §8: scans every built product assembly (metadata names, user strings and raw bytes)
/// for identifiers of firmware operations. Test assemblies are excluded because they contain the pattern.
/// </summary>
public partial class ForbiddenIdentifierScanTests
{
    private static readonly string[] RequiredAssemblies =
    [
        "UnifiedRgb.Core.dll", "UnifiedRgb.Devices.Abstractions.dll", "UnifiedRgb.Transports.Win.dll",
        "UnifiedRgb.Devices.LianLi.dll", "UnifiedRgb.Devices.MsiGpu.dll", "UnifiedRgb.Devices.OpenRgbSdk.dll",
        "UnifiedRgb.Devices.AsusAura.dll", "UnifiedRgb.Devices.EneDram.dll",
        "UnifiedRgb.Devices.Virtual.dll", "UnifiedRgb.Contracts.dll", "UnifiedRgb.Host.dll", "UnifiedRgb.App.dll", "urgb.dll",
        "UnifiedRgb.Screens.dll", "UnifiedRgb.Setup.Core.dll", "UnifiedRgb.Setup.dll",
    ];

    [GeneratedRegex("FwErase|FwUpload|FwVerify|FwReboot|UpdatePre|UpdateData|UpdateAio|UpdateFW|FWRescue")]
    private static partial Regex Forbidden();

    internal static string RepoRoot() => RepositoryPaths.Root();

    /// <summary>
    /// Output folders of the build under test: with an isolated <c>--artifacts-path</c> build (this assembly under
    /// <c>&lt;artifacts&gt;/bin/&lt;project&gt;/&lt;config&gt;/</c>) the product projects of that same build, including an artifacts
    /// directory outside the checkout. Default outputs likewise select only this test's configuration and target framework.
    /// </summary>
    internal static IReadOnlyList<string> ProductBinFolders()
    {
        var root = RepoRoot();
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var projects = Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Where(d => Directory.EnumerateFiles(d, "*.csproj").Any()).ToArray();
        if (output.Parent is { Name: "UnifiedRgb.Safety.Tests", Parent.Name: "bin" } testProject)
        {
            return projects.Select(project => Path.Combine(testProject.Parent.FullName, Path.GetFileName(project), output.Name))
                .Where(Directory.Exists)
                .ToList();
        }

        if (output.Parent is { Parent.Name: "bin", Parent.Parent.Name: "UnifiedRgb.Safety.Tests" } configuration)
            return projects.Select(project => Path.Combine(project, "bin", configuration.Name, output.Name))
                .Where(Directory.Exists).ToList();

        throw new DirectoryNotFoundException("Cannot identify the current Safety build output layout; stale outputs are not a fallback.");
    }

    private static List<string> ProductAssemblies()
    {
        return ProductBinFolders()
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll", SearchOption.AllDirectories))
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return (n.StartsWith("UnifiedRgb.", StringComparison.Ordinal) || n == "urgb.dll") && !n.Contains(".Tests", StringComparison.Ordinal);
            })
            .ToList();
    }

    internal static IEnumerable<string> FindHits(byte[] image)
    {
        var hits = new List<string>();
        foreach (var text in new[] { Encoding.Latin1.GetString(image), Encoding.Unicode.GetString(image), Encoding.Unicode.GetString(image, 1, image.Length - 1) })
        {
            hits.AddRange(Forbidden().Matches(text).Select(m => "raw:" + m.Value));
        }

        using var pe = new PEReader(new MemoryStream(image));
        try
        {
            if (!pe.HasMetadata)
            {
                return hits;
            }
        }
        catch (BadImageFormatException)
        {
            return hits; // raw byte scan only
        }

        var md = pe.GetMetadataReader();
        void Check(string kind, string s)
        {
            if (Forbidden().IsMatch(s))
            {
                hits.Add($"{kind}:{s}");
            }
        }

        foreach (var h in md.TypeDefinitions)
        {
            Check("type", md.GetString(md.GetTypeDefinition(h).Name));
        }

        foreach (var h in md.MethodDefinitions)
        {
            Check("method", md.GetString(md.GetMethodDefinition(h).Name));
        }

        foreach (var h in md.FieldDefinitions)
        {
            Check("field/enum member", md.GetString(md.GetFieldDefinition(h).Name));
        }

        foreach (var h in md.PropertyDefinitions)
        {
            Check("property", md.GetString(md.GetPropertyDefinition(h).Name));
        }

        foreach (var h in md.MemberReferences)
        {
            Check("memberref", md.GetString(md.GetMemberReference(h).Name));
        }

        var us = MetadataTokens.UserStringHandle(1);
        while (!us.IsNil)
        {
            Check("string literal", md.GetUserString(us));
            us = md.GetNextHandle(us);
        }

        return hits;
    }

    [Fact]
    public void All_product_assemblies_are_present()
    {
        var names = ProductAssemblies().Select(Path.GetFileName).ToHashSet();
        names.Should().Contain(RequiredAssemblies);
    }

    [Fact]
    public void No_product_assembly_contains_firmware_identifiers()
    {
        var violations = new List<string>();
        foreach (var file in ProductAssemblies())
        {
            violations.AddRange(FindHits(File.ReadAllBytes(file)).Select(h => $"{Path.GetFileName(file)} -> {h}"));
        }

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scanner_is_not_vacuous()
    {
        var bytes = Encoding.Unicode.GetBytes("xx Usb_FwErase yy").Concat(Encoding.UTF8.GetBytes("RF_UpdateAio")).ToArray();
        FindHits(bytes).Should().HaveCountGreaterOrEqualTo(2);

        // This very test assembly contains the pattern as a literal; the scanner must find it.
        var self = File.ReadAllBytes(typeof(ForbiddenIdentifierScanTests).Assembly.Location);
        FindHits(self).Should().Contain(h => h.StartsWith("string literal:", StringComparison.Ordinal));
    }
}
