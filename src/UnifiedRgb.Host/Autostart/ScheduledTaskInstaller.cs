using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace UnifiedRgb.Host.Autostart;

/// <summary>
/// ARCHITECTURE §9: scheduled task "UnifiedRgb Host" — at logon of the installing user, highest privileges, 20 s delay,
/// restart on failure 3× one minute apart, no execution time limit, ignore a second instance. Registered with
/// <c>schtasks /Create /XML</c> so the definition is a reviewable, unit-tested document.
/// </summary>
public static class ScheduledTaskInstaller
{
    public const string TaskName = "UnifiedRgb Host";
    public const string LogonDelay = "PT20S";
    public const string RestartInterval = "PT1M";
    public const int RestartCount = 3;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static XDocument BuildDefinition(string hostExe, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!Path.IsPathFullyQualified(hostExe))
        {
            throw new ArgumentException("Host path must be absolute.", nameof(hostExe));
        }

        _ = new SecurityIdentifier(userSid); // validates the SID format

        return new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "Unified RGB Controller lighting host (elevated; owns the RGB devices).")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userSid),
                        new XElement(Ns + "Delay", LogonDelay))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userSid),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Ns + "Priority", "4"),
                    new XElement(Ns + "RestartOnFailure",
                        new XElement(Ns + "Interval", RestartInterval),
                        new XElement(Ns + "Count", RestartCount))),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", hostExe),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(hostExe))))));
    }

    /// <summary>schtasks requires the XML file in UTF-16.</summary>
    public static void WriteDefinition(XDocument definition, string path)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var w = new StreamWriter(path, false, Encoding.Unicode);
        definition.Save(w);
    }

    public static ProcessStartInfo CreateCommand(string xmlPath) => Schtasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");

    public static ProcessStartInfo DeleteCommand() => Schtasks("/Delete", "/TN", TaskName, "/F");

    /// <summary>Registers the task for the current user. Returns schtasks' exit code.</summary>
    public static int Register(string hostExe)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID.");
        var xml = Path.Combine(Path.GetTempPath(), $"UnifiedRgb-task-{Guid.NewGuid():N}.xml");
        try
        {
            WriteDefinition(BuildDefinition(hostExe, sid), xml);
            return Run(CreateCommand(xml));
        }
        finally
        {
            File.Delete(xml);
        }
    }

    public static int Unregister() => Run(DeleteCommand());

    private static ProcessStartInfo Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private static int Run(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks did not start.");
        p.WaitForExit();
        return p.ExitCode;
    }
}
