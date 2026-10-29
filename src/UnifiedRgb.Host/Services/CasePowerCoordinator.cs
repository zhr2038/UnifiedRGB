using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

public enum CasePowerIntent { Blackout, Restore, Toggle }

/// <summary>Changed = a scene was sent and SetScene returned: software acceptance by the Host, never physical confirmation.</summary>
public sealed record CasePowerIntentResult(bool Changed, string Message, CasePowerIntent? ResolvedIntent = null);

/// <summary>
/// Case-power intents for future explicitly enabled controls, on the same application semantics as the GUI buttons
/// (CasePowerActions): Blackout saves the running RGB scene as <see cref="BackupProfileName"/> and sends black to every zone of
/// the Active controllers, the Host's render loop (its SetScene refuses any other zone), so an asleep, offline or blocked
/// peripheral is left out and never claimed off; Restore sends that recovery point back only while the case is still blacked out. Both send Screens=null, so screen content
/// and screen device settings stay unchanged, and the package reference is carried over as is (no package service is used).
/// Intents are serialized within this instance only: other clients may still write between its reads and its write, and
/// IRgbService has no compare-and-set, so the re-read before each write narrows that window but cannot close it.
/// An exception before SetScene means no scene was sent (a Blackout may already have written the recovery point). An exception
/// from SetScene leaves the outcome unknown: nothing is retried or rolled back, the caller must read the state back.
/// At most <see cref="MaxOutstanding"/> requests are admitted (running or queued); further ones are rejected immediately.
/// Dispose rejects new and still-queued requests; a running request finishes under its own token.
/// </summary>
public sealed class CasePowerCoordinator(IRgbService service) : IDisposable
{
    public const string BackupProfileName = "Before Lights Off";
    private const int MaxOutstanding = 4;
    private readonly IRgbService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetime = new();
    private int _outstanding; // guarded by _lifetime
    private bool _closing; // guarded by _lifetime

    /// <summary>
    /// Runs one intent after any earlier ones. An unknown intent, a disposed coordinator or a full queue throw synchronously
    /// before any backend call.
    /// </summary>
    public Task<CasePowerIntentResult> ExecuteAsync(CasePowerIntent intent, CancellationToken ct = default)
    {
        Func<CancellationToken, Task<CasePowerIntentResult>> operation = intent switch
        {
            CasePowerIntent.Blackout => BlackoutAsync,
            CasePowerIntent.Restore => RestoreAsync,
            CasePowerIntent.Toggle => ToggleAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown case-power intent; nothing was requested."),
        };
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_outstanding == MaxOutstanding)
                throw new InvalidOperationException("Too many case-power requests are already pending; this one was not queued.");
            _outstanding++;
        }
        return SerializedAsync(operation, ct);
    }

    // Called only inside this instance's existing serialization gate. No cached On/Off state and no nested gate:
    // every distinct center press reads the current Host scene, then reuses the guarded existing paths.
    private async Task<CasePowerIntentResult> ToggleAsync(CancellationToken ct)
    {
        var current = await _service.GetSceneAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // GetScene is trusted engine data. Do not re-validate effect/theme/target semantics here (an empty target list can
        // represent a global assignment); only an absent/unnamed scene is not enough evidence to choose a toggle.
        if (current?.Assignments is not { } assignments || assignments.Any(a => a is null || string.IsNullOrWhiteSpace(a.EffectId)))
            throw new InvalidDataException("Current case scene is malformed/unknown; no toggle was selected or sent.");
        if (current.Assignments.Count == 0 && current.Package is null)
            throw new InvalidDataException("Current case scene is unknown/empty; no toggle was selected or sent.");
        var resolved = CaseStatusSummary.IsBlackout(current) ? CasePowerIntent.Restore : CasePowerIntent.Blackout;
        ct.ThrowIfCancellationRequested();
        var result = await (resolved == CasePowerIntent.Restore ? RestoreAsync(ct) : BlackoutAsync(ct)).ConfigureAwait(false);
        return result with { ResolvedIntent = resolved };
    }

    private async Task<CasePowerIntentResult> SerializedAsync(Func<CancellationToken, Task<CasePowerIntentResult>> operation,
        CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_lifetime) ObjectDisposedException.ThrowIf(_closing, this);
                return await operation(ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            lock (_lifetime)
            {
                if (--_outstanding == 0 && _closing) _gate.Dispose();
            }
        }
    }

    private async Task<CasePowerIntentResult> BlackoutAsync(CancellationToken ct)
    {
        var current = await _service.GetSceneAsync(ct).ConfigureAwait(false);
        if (CaseStatusSummary.IsBlackout(current))
            return new(false, "Lights are already off; the existing recovery point was kept and no scene was sent.");
        var controllers = await _service.ListControllersAsync(ct).ConfigureAwait(false);
        var targets = controllers.Where(c => c.State == ControllerStates.Active)
            .SelectMany(c => c.Zones.Select(z => new ZoneRefDto(c.Id, z.Id))).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("No active lighting zones to turn off; nothing was saved or sent.");
        ct.ThrowIfCancellationRequested();
        var recovery = new SceneDto(current.Assignments, Screens: null, Package: current.Package);
        await _service.SaveProfileAsync(new(BackupProfileName, recovery), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // A scene another client set meanwhile would be blacked out without being in the recovery point.
        if (!SameRgb(await _service.GetSceneAsync(ct).ConfigureAwait(false), recovery))
            throw new InvalidOperationException(
                "The RGB scene changed while lights-off was prepared; no scene was sent. The recovery point holds the scene read first.");
        ct.ThrowIfCancellationRequested();
        await _service.SetSceneAsync(new([new(targets, "StaticColor", ["#000000"], Brightness: 0, Strategy: "Vendor")]), ct)
            .ConfigureAwait(false);
        return new(true, $"Saved the RGB scene as '{BackupProfileName}' and the Host accepted the lights-off scene.");
    }

    private async Task<CasePowerIntentResult> RestoreAsync(CancellationToken ct)
    {
        if (!CaseStatusSummary.IsBlackout(await _service.GetSceneAsync(ct).ConfigureAwait(false)))
            return new(false, "Lights are not off; the running scene was kept and nothing was restored.");
        var backup = await _service.LoadProfileAsync(BackupProfileName, ct).ConfigureAwait(false);
        if (backup?.Scene is null) throw new InvalidOperationException($"No '{BackupProfileName}' recovery point; nothing was sent.");
        ct.ThrowIfCancellationRequested();
        if (!CaseStatusSummary.IsBlackout(await _service.GetSceneAsync(ct).ConfigureAwait(false)))
            return new(false, "A newer scene started while the recovery point was loading; it was kept and nothing was restored.");
        ct.ThrowIfCancellationRequested();
        await _service.SetSceneAsync(new(backup.Scene.Assignments, Screens: null, Package: backup.Scene.Package), ct)
            .ConfigureAwait(false);
        return new(true, $"The Host accepted the '{BackupProfileName}' scene.");
    }

    // Wire-form comparison of the RGB part: every reply is a fresh copy and record equality compares lists by reference.
    private static bool SameRgb(SceneDto latest, SceneDto saved) =>
        JsonSerializer.SerializeToUtf8Bytes(latest with { Screens = null }, ContractsJsonContext.Default.SceneDto).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(saved, ContractsJsonContext.Default.SceneDto));

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_closing) return;
            _closing = true;
            if (_outstanding == 0) _gate.Dispose();
        }
    }
}
