using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class WindowsCodexActivationTests
{
    [Fact]
    public void Desktop_lookup_arguments_match_documented_CSIdl_location_and_reserved_empty_root()
    {
        // Pure managed ABI-boundary values: no desktop lookup, COM construction or activation.
        var (location, root) = CodexDesktopAutomation.DesktopLookupArguments();
        location.Should().BeOfType<int>().Which.Should().Be(0); // CSIDL_DESKTOP -> VT_I4
        root.Should().BeNull(); // managed null -> VT_EMPTY
    }

    private const string Explorer = @"C:\Windows\explorer.exe";
    private static CodexProcessIdentity User(bool elevated = true) => new(50, "S-1-5-21-fake-user", 2,
        elevated ? 0x3000u : 0x2000u, elevated, false, true, @"C:\fake\Host.exe");
    private sealed class Platform : ICodexActivationPlatform
    {
        public CodexProcessIdentity? Current = User();
        public Desktop? Desktop { get; set; } = new();
        public int DirectCalls, DesktopCalls;
        public string? Fault;
        public string ExplorerPath => Explorer;
        public CodexProcessIdentity? Caller() => Fault == "caller-query" ? throw new IOException("query failed") : Current;
        public ICodexDesktopLease? AcquireDesktop()
        { DesktopCalls++; return Fault == "desktop-query" ? throw new IOException("query failed") : Desktop; }
        public CodexActivationResult ActivateApplication(string aumid, string? arguments, uint options)
        {
            DirectCalls++; aumid.Should().Be("OpenAI.Codex_2p2nqsd0c76g0!App"); arguments.Should().BeNull(); options.Should().Be(2);
            return new("Requested", 0, 42);
        }
    }
    private sealed class Desktop : ICodexDesktopLease
    {
        public CodexProcessIdentity Identity { get; set; } = User(false) with { ProcessId = 100, Image = Explorer };
        public Automation Automation = new();
        public bool Valid = true, Disposed;
        public string? Fault;
        public bool OwnsWindow(nint window) => window is 101 or 102;
        public bool Revalidate() => Fault == "revalidate-query" ? throw new IOException("query failed") : Valid;
        public ICodexDesktopAutomation OpenAutomation()
        {
            if (Fault == "automation-query") throw new IOException("desktop automation unavailable");
            if (Fault == "identity-replaced") Identity = Identity with { Sid = "foreign-replacement" };
            return Automation;
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class Automation : ICodexDesktopAutomation
    {
        public nint BrowserWindow { get; set; } = 101;
        public nint ViewWindow { get; set; } = 102;
        public int Calls;
        public bool Disposed;
        public string Status = "Requested";
        public bool Throw;
        public CodexActivationResult ShellExecute(string file, string arguments, string directory, string verb, int show)
        {
            Calls++; file.Should().Be(Explorer); arguments.Should().Be(@"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App");
            directory.Should().BeEmpty(); verb.Should().BeEmpty(); show.Should().Be(1);
            if (Throw) throw new IOException("unknown broker outcome");
            return new(Status, 0, 100); // even a mistaken broker PID must be stripped
        }
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData("Requested")] [InlineData("Failed")] [InlineData("Unknown")]
    public async Task Elevated_host_uses_only_existing_verified_medium_desktop_with_fixed_payload_and_no_Codex_PID(string status)
    {
        var platform = new Platform(); platform.Desktop!.Automation.Status = status;
        var result = await new WindowsCodexWindowActivator(platform).RequestAsync();
        result.Status.Should().Be(status); result.ProcessId.Should().BeNull();
        platform.DirectCalls.Should().Be(0); platform.Desktop.Automation.Calls.Should().Be(1);
        platform.Desktop.Disposed.Should().BeTrue(); platform.Desktop.Automation.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Unelevated_medium_interactive_process_uses_exact_direct_activation_contract()
    {
        var platform = new Platform { Current = User(false) };
        var result = await new WindowsCodexWindowActivator(platform).RequestAsync();
        result.Status.Should().Be("Requested"); result.ProcessId.Should().Be(42);
        platform.DesktopCalls.Should().Be(0); platform.DirectCalls.Should().Be(1);
        WindowsCodexWindowActivator.ClassId.Should().Be(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
        typeof(WindowsCodexWindowActivator.IApplicationActivationManager).GUID.Should().Be(new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"));
    }

    [Theory]
    [InlineData("absent")] [InlineData("replaced")] [InlineData("sid")] [InlineData("session")]
    [InlineData("zero-session")] [InlineData("system")] [InlineData("high")] [InlineData("low")]
    [InlineData("elevated")] [InlineData("image")] [InlineData("browser")] [InlineData("view")] [InlineData("no-pid")]
    [InlineData("identity-replaced")]
    public async Task Ineligible_or_stale_desktop_never_activates_and_never_falls_back_high(string fault)
    {
        var platform = new Platform(); var desktop = platform.Desktop!;
        desktop.Identity = fault switch
        {
            "sid" => desktop.Identity with { Sid = "foreign" }, "session" => desktop.Identity with { Session = 3 },
            "zero-session" => desktop.Identity with { Session = 0 }, "system" => desktop.Identity with { System = true },
            "high" => desktop.Identity with { Integrity = 0x3000 }, "low" => desktop.Identity with { Integrity = 0x1000 },
            "elevated" => desktop.Identity with { Elevated = true }, "image" => desktop.Identity with { Image = @"C:\fake\explorer.exe" },
            "no-pid" => desktop.Identity with { ProcessId = 0 }, _ => desktop.Identity,
        };
        if (fault == "absent") platform.Desktop = null;
        if (fault == "replaced") desktop.Valid = false;
        if (fault == "browser") desktop.Automation.BrowserWindow = 999;
        if (fault == "view") desktop.Automation.ViewWindow = 999;
        if (fault == "identity-replaced") desktop.Fault = fault;
        (await new WindowsCodexWindowActivator(platform).RequestAsync()).Status.Should().Be("Unsupported");
        platform.DirectCalls.Should().Be(0); desktop.Automation.Calls.Should().Be(0);
        if (fault != "absent") desktop.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("null")] [InlineData("noninteractive")] [InlineData("session0")]
    [InlineData("system")] [InlineData("low")] [InlineData("system-integrity")] [InlineData("high-not-elevated")]
    public async Task Ineligible_caller_never_queries_desktop_or_invokes_any_activation(string fault)
    {
        var platform = new Platform(); platform.Current = fault switch
        {
            "null" => null, "noninteractive" => User() with { Interactive = false }, "session0" => User() with { Session = 0 },
            "system" => User() with { System = true }, "low" => User() with { Integrity = 0x1000 },
            "system-integrity" => User() with { Integrity = 0x4000 }, _ => User() with { Elevated = false },
        };
        (await new WindowsCodexWindowActivator(platform).RequestAsync()).Status.Should().Be("Unsupported");
        platform.DirectCalls.Should().Be(0); platform.DesktopCalls.Should().Be(0);
    }

    [Fact]
    public async Task Broker_exception_is_unknown_not_retry_or_visible_and_all_leases_are_released()
    {
        var platform = new Platform(); platform.Desktop!.Automation.Throw = true;
        (await new WindowsCodexWindowActivator(platform).RequestAsync()).Status.Should().Be("Unknown");
        platform.Desktop.Automation.Calls.Should().Be(1); platform.DirectCalls.Should().Be(0);
        platform.Desktop.Automation.Disposed.Should().BeTrue(); platform.Desktop.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("caller-query")] [InlineData("desktop-query")] [InlineData("automation-query")] [InlineData("revalidate-query")]
    public async Task Query_failure_is_closed_unknown_and_never_uses_a_high_fallback(string fault)
    {
        var platform = new Platform { Fault = fault }; platform.Desktop!.Fault = fault;
        (await new WindowsCodexWindowActivator(platform).RequestAsync()).Status.Should().Be("Unknown");
        platform.DirectCalls.Should().Be(0); platform.Desktop.Automation.Calls.Should().Be(0);
        if (fault is "automation-query" or "revalidate-query") platform.Desktop.Disposed.Should().BeTrue();
        if (fault == "revalidate-query") platform.Desktop.Automation.Disposed.Should().BeTrue();
    }
}
