using System.Text.Json;
using System.Diagnostics;
using AppOnlyWriter;
using UnifiedRgb.Contracts;
using UnifiedRgb.Firmware.Worker;

namespace UnifiedRgb.Firmware.Checks;

internal sealed class FakePhysical(string directory) : IPhysicalObservationSource
{
    public int Calls;
    public int NormalGetterCalls, PassiveNormalCalls;
    public bool HoldBootEntry;
    public bool WrongHeader, WrongBoot, AlreadyBoot;
    public static readonly string Container = new Guid("65393e54-a029-4804-83e2-6e2aba7d7315").ToString("D");
    public const string Location = "PCIROOT(0)#USBROOT(0)#USB(3)";
    public Task<NormalObservation> ReadCurrentNormalUsbAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Calls++; NormalGetterCalls++;
        if (AlreadyBoot) throw new InvalidDataException("Reconnect normal first");
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        var header = WrongHeader ? "001200000024000100" : "001200000025000100";
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        FirmwareFiles.CreateNew(path, JsonSerializer.SerializeToUtf8Bytes(new { at, Header = header, Container, Location }));
        return Task.FromResult(new NormalObservation("1C24", header, Container, Location, at, path));
    }
    public Task<NormalObservation> ObserveCurrentNormalUsbAsync(RetainedBaselineReceipt? retained, CancellationToken ct)
    {
        if (retained is null) return ReadCurrentNormalUsbAsync(ct);
        ct.ThrowIfCancellationRequested(); Calls++; PassiveNormalCalls++;
        if (AlreadyBoot) throw new InvalidDataException("Reconnect normal first");
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        FirmwareFiles.CreateNew(path, JsonSerializer.SerializeToUtf8Bytes(new { at, Container, Location, HeaderOrigin = RetainedBaselinePolicy.Origin }));
        return Task.FromResult(new NormalObservation("1C24", retained.NormalFirmwareHeader, Container, Location, at, path, RetainedBaselinePolicy.Origin));
    }
    public async Task<BootObservation> WatchNormalRemovalAndMatchingBootAsync(NormalObservation normal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Calls++;
        if (HoldBootEntry) await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        var at = DateTimeOffset.UtcNow;
        var boot = new BootObservation("\\\\?\\hid#vid_0b05&pid_1c26&mi_01#fake", "HID\\VID_0B05&PID_1C26&MI_01\\fake", "USB\\VID_0B05&PID_1C26\\fake",
            "29498fe8-cf5f-11f1-95fe-60ff9efff29b", WrongBoot ? Location + "#USB(99)" : Location, new string('a', 64), "02000200", at, [], true, true);
        return boot with { InventoryJson = JsonSerializer.SerializeToUtf8Bytes(new { at, boot.HidPath, boot.HidInstanceId, boot.UsbAncestor, boot.ContainerId, boot.PhysicalLocation, boot.CapsSha256 }) };
    }
    public Task<bool> ObserveFreshNormalReplugAsync(PhysicalBinding binding, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(true); }
}

internal sealed class FakeMaintenance : IFirmwareMaintenance
{
    public int Acquires, Finishes;
    public Guid? Owner;
    public Task AcquireAsync(Guid jobId, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (Owner is not null) throw new InvalidOperationException("owned"); Owner = jobId; Acquires++; return Task.CompletedTask; }
    public Task FinishAsync(Guid jobId, bool knownWireClosed, bool freshNormal, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (Owner != jobId || !knownWireClosed || !freshNormal) throw new InvalidOperationException("unsafe"); Finishes++; Owner = null; return Task.CompletedTask; }
}
internal sealed class MemoryRunner(byte[] original) : IFirmwareWorkerRunner
{
    public byte[] Flash = original.ToArray();
    public int Runs, Opens, Erases;
    public bool WrongBootVersion;
    public Action? BeforeOpen;
    public FakeBoot? LastBoot;
    private bool _cancel;
    public Task<FirmwareWorkerReceipt> RunAsync(CatalogEntry catalog, FirmwareWorkerRequest request, Action<FirmwareWorkerReceipt> progress)
    {
        Runs++; FakeBoot? io = null;
        var result = FunctionalTransaction.Run(ReleasePackage.Load(request.BundlePath), request, _ =>
        { BeforeOpen?.Invoke(); Opens++; return LastBoot = io = new FakeBoot(Flash) { DifferentDevice = WrongBootVersion }; }, progress, () => _cancel, catalog);
        if (io is not null) { Flash = io.Flash; Erases += io.EraseRequests; }
        return Task.FromResult(result);
    }
    public bool RequestCancel(string operationDirectory) { _cancel = true; return true; }
}
internal sealed class UiService(FirmwareJobStatus status) : IFirmwareUpdateService
{
    public FirmwareJobStatus Status = status;
    public int Writes, Reads;
    public FirmwareRequest? LastRequest;
    public bool Unsupported, TimeoutMutation;
    public Task<FirmwareJobStatus?> GetFirmwareJobAsync(Guid? jobId = null, CancellationToken ct = default)
    { Reads++; if (Unsupported) throw new NotSupportedException("Old host"); return Task.FromResult<FirmwareJobStatus?>(Status); }
    public Task<FirmwareJobStatus> LoadFirmwarePackageAsync(string path, CancellationToken ct = default) => Change();
    public Task<FirmwareJobStatus> PrepareFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) { LastRequest = request; return Change(); }
    public Task<FirmwareJobStatus> UseRetainedFirmwareBaselineAsync(FirmwareRequest request, CancellationToken ct = default) => Change();
    public Task<FirmwareJobStatus> BackupFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Change();
    public Task<FirmwareJobStatus> InstallFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Change();
    public Task<FirmwareJobStatus> RestoreFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Change();
    public Task<FirmwareJobStatus> CancelFirmwareIfSafeAsync(FirmwareRequest request, CancellationToken ct = default) => Change();
    public Task<FirmwareJobStatus> FinishFirmwareAsync(FirmwareRequest request, CancellationToken ct = default) => Change();
    private Task<FirmwareJobStatus> Change()
    { Writes++; return TimeoutMutation ? new TaskCompletionSource<FirmwareJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously).Task : Task.FromResult(Status); }
}

internal sealed class PendingChild : IFirmwareChildProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HasExited => _exit.Task.IsCompleted;
    public int ExitCode => 0;
    public TextReader StandardOutput { get; } = new CountingReader(100000);
    public TextReader StandardError { get; } = new CountingReader(100000);
    public int Waits, Disposals;
    public bool DisposedBeforeExit;
    public Task WaitForExitAsync() { Waits++; return _exit.Task; }
    public void Complete() => _exit.TrySetResult();
    public void Dispose() { DisposedBeforeExit |= !HasExited; Disposals++; StandardOutput.Dispose(); StandardError.Dispose(); }
}
internal sealed class CountingReader(int length) : TextReader
{
    public int ReadCount { get; private set; }
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = Math.Min(buffer.Length, length - ReadCount); buffer.Span[..count].Fill('x'); ReadCount += count;
        return ValueTask.FromResult(count);
    }
}
internal sealed class InjectedProcessFactory(PendingChild child) : IFirmwareProcessFactory
{
    public int Starts;
    public ProcessStartInfo? StartInfo;
    public IFirmwareChildProcess Start(ProcessStartInfo start) { Starts++; StartInfo = start; return child; }
}
internal sealed class FailingOutputStorage : IFirmwareOutputStorage
{
    public int Attempts;
    public Stream CreateNew(string path) { Attempts++; throw new IOException("Injected log disk full"); }
}
internal sealed class ThrowingProgressRunner(FirmwareWorkerProcess runner) : IFirmwareWorkerRunner
{
    public Task<FirmwareWorkerReceipt> RunAsync(CatalogEntry catalog, FirmwareWorkerRequest request, Action<FirmwareWorkerReceipt> progress)
    {
        FirmwareFiles.CreateNew(Path.Combine(request.Directory, "progress-0000.json"), JsonSerializer.SerializeToUtf8Bytes(
            new FirmwareWorkerReceipt(request.JobId, request.RunId, FirmwarePhase.BackingUp, "fake pending", false, false, 0, null, Path.Combine(request.Directory, "ledger.jsonl"))));
        return runner.RunAsync(catalog, request, _ => throw new IOException("Injected UI observer failure"));
    }
    public bool RequestCancel(string operationDirectory) => runner.RequestCancel(operationDirectory);
}
