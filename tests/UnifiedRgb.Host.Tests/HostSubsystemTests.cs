using System.Reflection.PortableExecutable;

namespace UnifiedRgb.Host.Tests;

public sealed class HostSubsystemTests
{
    [Fact]
    public void Actual_host_apphost_and_entry_assembly_do_not_request_a_console_but_the_CLI_still_does()
    {
        var apphost = Path.Combine(AppContext.BaseDirectory, "UnifiedRgb.Host.exe");
        if (!File.Exists(apphost)) apphost = Path.ChangeExtension(typeof(HostLogging).Assembly.Location, ".exe");
        SubsystemOf(apphost).Should().Be(Subsystem.WindowsGui);
        SubsystemOf(typeof(HostLogging).Assembly.Location).Should().Be(Subsystem.WindowsGui);
        SubsystemOf(typeof(UnifiedRgb.Cli.CliApp).Assembly.Location).Should().Be(Subsystem.WindowsCui);
    }

    private static Subsystem SubsystemOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        return pe.PEHeaders.PEHeader?.Subsystem ?? throw new InvalidDataException("Missing PE optional header: " + path);
    }

}
