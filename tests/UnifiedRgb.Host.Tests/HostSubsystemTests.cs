using System.Reflection.PortableExecutable;

namespace UnifiedRgb.Host.Tests;

public sealed class HostSubsystemTests
{
    [Fact]
    public void Actual_host_apphost_and_entry_assembly_do_not_request_a_console_but_the_CLI_still_does()
    {
        var root = FindRoot();
        var bin = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent;
        var hostDir = bin is { Name: "bin" } && bin.Parent?.Parent?.FullName == root
            ? Path.Combine(bin.FullName, "UnifiedRgb.Host", new DirectoryInfo(AppContext.BaseDirectory).Name)
            : Path.Combine(root, "src", "UnifiedRgb.Host", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0-windows");
        SubsystemOf(Path.Combine(hostDir, "UnifiedRgb.Host.exe")).Should().Be(Subsystem.WindowsGui);
        SubsystemOf(typeof(HostLogging).Assembly.Location).Should().Be(Subsystem.WindowsGui);
        SubsystemOf(typeof(UnifiedRgb.Cli.CliApp).Assembly.Location).Should().Be(Subsystem.WindowsCui);
    }

    private static Subsystem SubsystemOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        return pe.PEHeaders.PEHeader?.Subsystem ?? throw new InvalidDataException("Missing PE optional header: " + path);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
