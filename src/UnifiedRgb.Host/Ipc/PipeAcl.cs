using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UnifiedRgb.Host.Ipc;

/// <summary>
/// Pipe DACL (ARCHITECTURE §1/§8): Administrators, SYSTEM and the Host's own account get full control; interactively
/// logged-on users get read/write; network logons are explicitly denied. Service, batch, anonymous and every other
/// non-interactive logon get no ACE and are therefore denied by the DACL.
/// </summary>
public static class PipeAcl
{
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    public static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    /// <summary>Rights an interactive client needs to open the pipe in/out (GENERIC_READ | GENERIC_WRITE).</summary>
    public const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <param name="hostAccount">Account the Host runs as (default: current).</param>
    /// <param name="elevated">Whether the Host token is elevated (default: detected). An elevated Host makes Administrators the
    /// pipe owner so clients can tell it from a squatter (<see cref="Contracts.PipeServerIdentity"/>); SYSTEM owns it by default.</param>
    public static PipeSecurity Create(SecurityIdentifier? hostAccount = null, bool? elevated = null)
    {
        var ps = new PipeSecurity();
        if (elevated ?? IsElevated())
        {
            ps.SetOwner(Administrators);
        }

        ps.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        ps.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(Interactive, ClientRights, AccessControlType.Allow));
        var self = hostAccount ?? WindowsIdentity.GetCurrent().User;
        if (self is not null)
        {
            ps.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return ps;
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User == LocalSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Evaluates the DACL for a caller whose token holds <paramref name="tokenSids"/> (canonical order: any matching
    /// deny wins). Used by tests and by diagnostics; the kernel performs the real check.
    /// </summary>
    public static PipeAccessRights EffectiveRights(PipeSecurity security, IReadOnlyCollection<SecurityIdentifier> tokenSids)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(tokenSids);
        PipeAccessRights allow = 0, deny = 0;
        foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (!tokenSids.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Deny)
            {
                deny |= rule.PipeAccessRights;
            }
            else
            {
                allow |= rule.PipeAccessRights;
            }
        }

        return allow & ~deny;
    }
}
