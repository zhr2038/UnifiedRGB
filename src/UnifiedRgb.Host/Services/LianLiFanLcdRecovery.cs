using UnifiedRgb.Devices;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// The Host's RF side of the screen subsystem's fan-LCD restart: the configured controller must be a Lian Li controller our
/// writer actively owns (guard verdict Clear, no bring-up lease), and the adapter's own checks then pin wire-format, sender/
/// receiver binding and the live LCD group (<see cref="LianLiAdapter.ResetLcdGroupAsync"/>). No process, privilege, driver or
/// vendor runtime is involved; the command travels the adapter's existing firewalled sender chain.
/// </summary>
public sealed class LianLiFanLcdRecovery(AdapterHost adapters) : IFanLcdGroupRecovery
{
    public void Validate(string controllerId, int lcdFans) => Resolve(controllerId, lcdFans);

    public async Task<FanLcdGroupResetResult> ResetAsync(string controllerId, int lcdFans, Action beforeFirstSend, CancellationToken ct)
    {
        var (adapter, id) = Resolve(controllerId, lcdFans);
        var r = await adapter.ResetLcdGroupAsync(id, lcdFans, beforeFirstSend, ct).ConfigureAwait(false);
        return new FanLcdGroupResetResult(r.Accepted, r.Attempts,
            $"{r.Detail}; rfList index {r.SlaveIndex}, sequence {r.PreviousSequence}->{r.Sequence}, PWM before {r.PwmBefore}, after {r.PwmAfter ?? "unknown"}");
    }

    private (LianLiAdapter Adapter, ControllerId Id) Resolve(string controllerId, int lcdFans)
    {
        ArgumentException.ThrowIfNullOrEmpty(controllerId);
        var entry = adapters.Controllers.FirstOrDefault(e => e.Descriptor.Id.Value == controllerId)
            ?? throw new InvalidOperationException($"{controllerId} is not a discovered controller.");
        if (entry.Adapter is not LianLiAdapter lianLi)
        {
            throw new ArgumentException($"{controllerId} is not a Lian Li RF controller.", nameof(controllerId));
        }

        if (entry.Verdict.State != GuardState.Clear || !entry.IsActive || entry.BringupLease is not null)
        {
            throw new InvalidOperationException(
                $"{controllerId} is not actively owned by this Host (verdict {entry.Verdict.State}{(entry.Verdict.Writer is { } w ? " " + w : string.Empty)}, writer {(entry.IsActive ? "active" : "none")}{(entry.BringupLease is { } l ? ", bring-up " + l : string.Empty)}).");
        }

        lianLi.ValidateLcdGroupReset(entry.Descriptor.Id, lcdFans);
        return (lianLi, entry.Descriptor.Id);
    }
}
