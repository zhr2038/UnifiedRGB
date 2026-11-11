using System.Diagnostics;
using System.Text.Json;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Firmware;

public enum FirmwareOperation { Backup, Install, Restore }
public sealed record FirmwareWorkerRequest(Guid JobId, Guid RunId, FirmwareOperation Operation, string BundlePath,
    string BundleSha256, string ReleaseId, string Directory, PhysicalBinding Binding,
    string? BackupPath, string? ParentLedgerPath, Guid? ParentRunId, string? ParentLedgerSha256,
    string? ParentLineageSha256, string? ParentBeforeImagePath, RetainedBaselineSelection? RetainedBaseline = null);
public sealed record FirmwareWorkerReceipt(Guid JobId, Guid RunId, FirmwarePhase Phase, string Detail,
    bool WireClosed, bool Destructive, int VerifiedPages, string? BackupPath, string LedgerPath,
    string? BeforeImageProvenancePath = null, string? ObserverDiagnostic = null);
public sealed record FirmwareBackupProvenance(int Schema, string Classification, Guid JobId, Guid RunId,
    PhysicalBinding Binding, DateTimeOffset CompletedAtUtc, bool TwoPassEqual, string ImageSha256,
    string ImagePath, string LedgerPath, string BundleSha256);
public interface IFirmwareWorkerRunner
{
    Task<FirmwareWorkerReceipt> RunAsync(CatalogEntry catalog, FirmwareWorkerRequest request, Action<FirmwareWorkerReceipt> progress);
    bool RequestCancel(string operationDirectory);
}

public interface IFirmwareChildProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    Task WaitForExitAsync();
}
public interface IFirmwareProcessFactory { IFirmwareChildProcess Start(ProcessStartInfo start); }
public interface IFirmwareOutputStorage { Stream CreateNew(string path); }

public sealed class FirmwareWorkerProcess(FirmwareInstalledOptions options,
    IFirmwareProcessFactory? processFactory = null, IFirmwareOutputStorage? outputStorage = null) : IFirmwareWorkerRunner
{
    public bool RequestCancel(string operationDirectory)
    {
        var path = Path.Combine(operationDirectory, "cancel-request");
        if (!File.Exists(path)) FirmwareFiles.CreateNew(path, "cancel-before-destructive-only"u8);
        return true; // acknowledgement is not a claim that the worker cancelled
    }
    public async Task<FirmwareWorkerReceipt> RunAsync(CatalogEntry catalog, FirmwareWorkerRequest request, Action<FirmwareWorkerReceipt> progress)
    {
        if (!options.HardwareEnabled || options.CatalogPath is null) throw new InvalidOperationException("Firmware execution is disabled");
        PhysicalWorkflow.RequireFresh(request.Binding); // before any process/inventory/open
        var executable = new InstalledTrust(options).ValidateWorker(catalog.Worker);
        var requestPath = Path.Combine(request.Directory, "request.json");
        FirmwareFiles.CreateNew(requestPath, JsonSerializer.SerializeToUtf8Bytes(request));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = options.PrivateDirectory,
        };
        start.ArgumentList.Add("--firmware-job"); start.ArgumentList.Add(requestPath);
        // No executable/arguments are ever read from the release. Configuration is private installed data.
        using var process = (processFactory ?? new NativeProcessFactory()).Start(start);
        var errors = new BoundedObserverErrors();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var last = -1;
        var lastPages = -1;
        var observe = true;
        void Observe()
        {
            if (!observe) return;
            try { ReadProgress(request, progress, ref last); ReadVerifiedPages(request, progress, ref lastPages); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { errors.Add("progress observer", ex); observe = false; }
        }
        try
        {
            try { stdout = DrainAsync(process.StandardOutput, Path.Combine(request.Directory, "stdout.txt"), errors); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("stdout attachment", ex); }
            try { stderr = DrainAsync(process.StandardError, Path.Combine(request.Directory, "stderr.txt"), errors); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("stderr attachment", ex); }
            while (!process.HasExited)
            {
                Observe(); // never abandon ownership or publish idle because a callback/observer failed
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
        finally
        {
            // Progress/local receipt failures MUST NOT abandon an owned process or make it safe to exit maintenance.
            // Even a local process-observation error may not turn an unknown child into idle.
            // Retry observation/join only (no device I/O, cancellation, reset, kill or release).
            var joined = false;
            while (!joined)
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); joined = process.HasExited; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("owned process join", ex); }
                if (!joined) await Task.Delay(200).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        Observe();
        var resultPath = Path.Combine(request.Directory, "result.json");
        if (!File.Exists(resultPath) || new FileInfo(resultPath).Length > 32768) throw new IOException("Worker outcome unknown; retain lease and ledger");
        var receipt = FirmwareFiles.Parse<FirmwareWorkerReceipt>(File.ReadAllBytes(resultPath));
        if (receipt.RunId != request.RunId || receipt.JobId != request.JobId || !receipt.WireClosed ||
            (process.ExitCode != 0 && receipt.Phase is not (FirmwarePhase.Failed or FirmwarePhase.NeedsReviewedRecovery or FirmwarePhase.Cancelled)))
            throw new IOException("Worker completion/ownership mismatch");
        return receipt with { ObserverDiagnostic = errors.Text };
    }
    private static void ReadVerifiedPages(FirmwareWorkerRequest request, Action<FirmwareWorkerReceipt> progress, ref int lastPages)
    {
        var ledger = Path.Combine(request.Directory, "ledger.jsonl");
        if (!File.Exists(ledger) || !File.Exists(Path.Combine(request.Directory, "destructive-barrier"))) return;
        if (new FileInfo(ledger).Length > 16 * 1024 * 1024) throw new InvalidDataException("Ledger progress bound");
        using var stream = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var snapshotLength = stream.Length;
        if (snapshotLength > 16 * 1024 * 1024) throw new InvalidDataException("Ledger progress bound");
        var snapshot = new byte[checked((int)snapshotLength)]; stream.ReadExactly(snapshot);
        var text = System.Text.Encoding.UTF8.GetString(snapshot);
        var complete = text.LastIndexOf('\n'); if (complete < 0) return;
        var pages = 0; long ordinal = 0;
        var confirmedPages = new HashSet<int>();
        JsonElement? previous = null;
        foreach (var line in text[..complete].Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var record = doc.RootElement;
            if (record.GetProperty("Ordinal").GetInt64() != ++ordinal) throw new InvalidDataException("Ledger ordinal progress mismatch");
            if (record.GetProperty("Kind").GetString() == "ProgramReadback")
            {
                var page = record.GetProperty("Page").GetInt32();
                if (record.GetProperty("UnknownOutcome").GetBoolean() || previous is not { } retired ||
                    retired.GetProperty("Kind").GetString() != "Retired" || retired.GetProperty("UnknownOutcome").GetBoolean() ||
                    retired.GetProperty("Operation").GetString() != "program-page" || record.GetProperty("Operation").GetString() != "program-page" ||
                    retired.GetProperty("Page").GetInt32() != page || retired.GetProperty("ExpectedSha256").GetString() != record.GetProperty("ExpectedSha256").GetString() ||
                    retired.GetProperty("EraseCycle").GetInt32() != record.GetProperty("EraseCycle").GetInt32() ||
                    page < 114688 || page >= 339968 || (page - 114688) % 4096 != 0 || !confirmedPages.Add(page))
                    throw new InvalidDataException("Unconfirmed/duplicate page progress record");
                pages++;
            }
            previous = record.Clone();
        }
        if (pages == lastPages) return;
        if (pages > 55) throw new InvalidDataException("Page progress outside official route");
        lastPages = pages;
        progress(new(request.JobId, request.RunId, request.Operation == FirmwareOperation.Restore ? FirmwarePhase.Restoring : FirmwarePhase.Installing,
            $"正在写入并逐页核验；已核验 {pages}/55 页，尚未完成最终完整镜像核验", false, true, pages, null, ledger));
    }
    private static void ReadProgress(FirmwareWorkerRequest request, Action<FirmwareWorkerReceipt> progress, ref int last)
    {
        // Only immutable completed files are visible; transient .pending files are ignored.
        var paths = Directory.EnumerateFiles(request.Directory, "progress-*.json").Take(257).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > 256) throw new InvalidDataException("Progress file count bound");
        for (var i = last + 1; i < paths.Length; i++)
        {
            if (new FileInfo(paths[i]).Length > 32768) throw new InvalidDataException("Progress bound");
            var receipt = FirmwareFiles.Parse<FirmwareWorkerReceipt>(File.ReadAllBytes(paths[i]));
            if (receipt.JobId != request.JobId || receipt.RunId != request.RunId) throw new InvalidDataException("Foreign progress");
            progress(receipt); last = i;
        }
    }
    private async Task DrainAsync(TextReader reader, string path, BoundedObserverErrors errors)
    {
        Stream? output = null;
        try { output = outputStorage?.CreateNew(path) ?? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("output retention open", ex); }
        var chars = new char[2048]; var retained = 0;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(chars).ConfigureAwait(false); if (read == 0) break;
                if (output is null || retained >= 65536) continue; // always drain/discard, including disk-full cases
                var bytes = System.Text.Encoding.UTF8.GetBytes(chars.AsSpan(0, read).ToArray());
                var count = Math.Min(bytes.Length, 65536 - retained);
                try { await output.WriteAsync(bytes.AsMemory(0, count)).ConfigureAwait(false); retained += count; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Add("output retention write", ex);
                    try { await output.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception disposeError) when (disposeError is not OutOfMemoryException) { errors.Add("output retention close", disposeError); }
                    output = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("output drain", ex); }
        finally
        {
            if (output is not null)
                try { await output.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("output retention close", ex); }
        }
    }
    private sealed class BoundedObserverErrors
    {
        private readonly List<string> _errors = [];
        public string? Text { get { lock (_errors) return _errors.Count == 0 ? null : string.Join("; ", _errors); } }
        public void Add(string category, Exception error)
        { lock (_errors) { if (_errors.Count < 8) _errors.Add(category + ": " + error.GetType().Name); } }
    }
    private sealed class NativeProcessFactory : IFirmwareProcessFactory
    {
        public IFirmwareChildProcess Start(ProcessStartInfo start) => new NativeChild(Process.Start(start) ?? throw new IOException("Worker did not start"));
    }
    private sealed class NativeChild(Process process) : IFirmwareChildProcess
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public Task WaitForExitAsync() => process.WaitForExitAsync();
        public void Dispose() => process.Dispose();
    }
}
