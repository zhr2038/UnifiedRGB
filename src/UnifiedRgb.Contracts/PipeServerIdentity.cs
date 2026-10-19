using System.IO.Pipes;
using System.Security.Principal;

namespace UnifiedRgb.Contracts;

/// <summary>Which pipe-server owners a client accepts.</summary>
public enum PipeTrust
{
    /// <summary>Production: the pipe must be owned by BUILTIN\Administrators or SYSTEM (the elevated Host).</summary>
    ElevatedOwner,

    /// <summary>Tests only (the test Host runs unelevated): additionally accept the current user. Not reachable from any command line.</summary>
    ElevatedOwnerOrCurrentUser,
}

/// <summary>
/// Client-side check against pipe squatting (ARCHITECTURE §8): an unelevated process that grabs the pipe name first can only
/// make itself (never Administrators or SYSTEM) the owner of its pipe, so the GUI and CLI refuse to talk to such a server.
/// The elevated Host sets the owner to Administrators explicitly (PipeAcl).
/// </summary>
public static class PipeServerIdentity
{
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    public static SecurityIdentifier? OwnerOf(PipeStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
    }

    public static bool IsTrusted(SecurityIdentifier? owner, PipeTrust trust, SecurityIdentifier? currentUser = null)
    {
        if (owner is null)
        {
            return false;
        }

        if (owner == Administrators || owner == LocalSystem)
        {
            return true;
        }

        return trust == PipeTrust.ElevatedOwnerOrCurrentUser && owner == (currentUser ?? WindowsIdentity.GetCurrent().User);
    }

    /// <summary>Throws UnauthorizedAccessException unless the connected pipe's owner is trusted.</summary>
    public static void Verify(PipeStream pipe, PipeTrust trust)
    {
        var owner = OwnerOf(pipe);
        if (!IsTrusted(owner, trust))
        {
            throw new UnauthorizedAccessException(
                $"The pipe server is owned by {owner?.Value ?? "nobody"}, not by Administrators/SYSTEM; refusing to talk to a possible impostor.");
        }
    }
}
