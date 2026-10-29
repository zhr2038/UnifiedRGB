using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

public enum CasePowerRequestState { Idle, ApplyingOff, ApplyingRestore, OffRequested, RestoreRequested, Failed, Unconfirmed }
public sealed record CasePowerRequestStatus(CasePowerRequestState State, string Message);
public sealed record CasePowerActionResult(bool Changed, string Message);

/// <summary>
/// Shared application intents for the existing buttons and future explicitly enabled controls.
/// Requests are serialized within this client; this does not claim an atomic transaction across other clients.
/// Screens=null deliberately preserves current screen content and all local device settings.
/// Request status is not physical confirmation; a control-panel presenter must observe the Host separately.
/// A scene write whose answer never arrived (cancelled, timed out, session lost) is Unconfirmed, not Failed: it may have applied.
/// </summary>
public sealed class CasePowerActions(Func<IRgbService> service) : IDisposable
{
    public const string BackupProfileName = "Before Lights Off";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetime = new();
    private int _operations;
    private bool _closing;
    private bool _sceneSent; // guarded by _gate
    private CasePowerRequestStatus _status = new(CasePowerRequestState.Idle, "等待操作");
    public CasePowerRequestStatus Status => Volatile.Read(ref _status);

    public Task<CasePowerActionResult> OffAsync(CancellationToken ct = default) => SerializedAsync(async () =>
    {
        SetStatus(CasePowerRequestState.ApplyingOff, "正在熄灭机箱…");
        var host = service();
        var current = await host.GetSceneAsync(ct).ConfigureAwait(false);
        if (IsBlackout(current))
        {
            const string unchanged = "后台已处于熄灯模式，保留原有恢复点。屏幕休眠状态可在控制面板查看。";
            SetStatus(CasePowerRequestState.OffRequested, "熄灯模式");
            return new(false, unchanged);
        }
        var controllers = await host.ListControllersAsync(ct).ConfigureAwait(false);
        // Only active controllers are in the Host's render loop, whose SetScene refuses any other zone: an asleep, offline or
        // blocked peripheral is left out (and not claimed off) instead of stopping the whole lights-off.
        var active = controllers.Where(c => c.State == ControllerStates.Active).ToArray();
        var zones = active.SelectMany(c => c.Zones.Select(z => new ZoneRefDto(c.Id, z.Id))).ToArray();
        // Black on a per-key zone is per-key output: without admission it is left out (and not claimed off) the same way.
        var refused = PerKeyAdmission.Blocked(zones, active).Select(b => b.Target).ToHashSet();
        var targets = zones.Where(t => !refused.Contains(t)).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("没有可以熄灭的灯光分区。");
        ct.ThrowIfCancellationRequested();
        await host.SaveProfileAsync(new(BackupProfileName, new(current.Assignments, Package: current.Package)), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _sceneSent = true;
        await host.SetSceneAsync(new([new(targets, "StaticColor", ["#000000"], Brightness: 0, Strategy: "Vendor")]), ct).ConfigureAwait(false);
        SetStatus(CasePowerRequestState.OffRequested, "熄灯指令已应用");
        return new(true, "已保存熄灯前场景并关闭灯光；已接管的屏幕将关闭背光并停止发帧，点击恢复后唤醒。"
            + (refused.Count > 0 ? $"{refused.Count} 个逐键分区未准许逐键输出，未包含在熄灯指令中，保持原状。" : string.Empty));
    }, ct);

    /// <summary>Preserves the existing GUI Restore button's explicit recovery-point semantics.</summary>
    public Task<CasePowerActionResult> RestoreBackupAsync(CancellationToken ct = default) => RestoreAsync(false, ct);

    /// <summary>Idempotent future 'on' intent: never replace an already running scene with an old recovery point.</summary>
    public Task<CasePowerActionResult> EnsureOnAsync(CancellationToken ct = default) => RestoreAsync(true, ct);

    private Task<CasePowerActionResult> RestoreAsync(bool onlyIfBlackout, CancellationToken ct) => SerializedAsync(async () =>
    {
        SetStatus(CasePowerRequestState.ApplyingRestore, "正在恢复机箱…");
        var host = service();
        if (onlyIfBlackout && !IsBlackout(await host.GetSceneAsync(ct).ConfigureAwait(false)))
        {
            SetStatus(CasePowerRequestState.RestoreRequested, "保持当前场景");
            return new(false, "当前未处于熄灯模式，保留正在运行的场景。");
        }
        var backup = await host.LoadProfileAsync(BackupProfileName, ct).ConfigureAwait(false);
        // A recovery point is re-sent as a whole; a per-key target without admission refuses it before any write (kept for later).
        PerKeyDisplay.RequireAdmitted(backup.Scene.Assignments.SelectMany(a => a.Targets),
            await host.ListControllersAsync(ct).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();
        _sceneSent = true;
        await host.SetSceneAsync(new(backup.Scene.Assignments, Package: backup.Scene.Package), ct).ConfigureAwait(false);
        SetStatus(CasePowerRequestState.RestoreRequested, "恢复指令已应用");
        return new(true, "已恢复熄灯前保存的场景；屏幕在新画面就绪后恢复背光。");
    }, ct);

    private async Task<CasePowerActionResult> SerializedAsync(Func<Task<CasePowerActionResult>> operation, CancellationToken ct)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _operations++;
        }
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            _sceneSent = false;
            try { return await operation().ConfigureAwait(false); }
            catch (Exception ex)
            {
                // A Host error is an answer; only a missing answer after the write leaves the outcome to the next read-back.
                if (_sceneSent && ex is OperationCanceledException or TimeoutException or StreamJsonRpc.ConnectionLostException)
                    SetStatus(CasePowerRequestState.Unconfirmed, "未收到后台答复，结果以回读为准");
                else SetStatus(CasePowerRequestState.Failed, "操作未完成，请检查光域");
                throw;
            }
            finally { _gate.Release(); }
        }
        finally
        {
            lock (_lifetime)
            {
                _operations--;
                if (_closing && _operations == 0) _gate.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_closing) return;
            _closing = true;
            if (_operations == 0) _gate.Dispose();
        }
    }

    private void SetStatus(CasePowerRequestState state, string message) => Volatile.Write(ref _status, new(state, message));

    public static bool IsBlackout(SceneDto scene) => scene.Package is null && scene.Assignments.Count > 0
        && scene.Assignments.All(a => a.EffectId == "StaticColor" && a.FrameSetId is null
            && (a.Brightness == 0 || a.Colors.Count > 0 && a.Colors.All(c => c.Equals("#000000", StringComparison.OrdinalIgnoreCase))));
}
