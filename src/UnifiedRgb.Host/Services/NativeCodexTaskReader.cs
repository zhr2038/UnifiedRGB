using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Host.Services;

internal interface ICodexTaskReader { Task<CodexTaskSnapshot> ReadAsync(CancellationToken ct); }
internal sealed record CodexTaskOwner(int Pid, DateTimeOffset CreatedAt);
internal sealed record CodexTaskMetadata(string Id, string Title, string RolloutPath, string TurnId, int OwnerPid, DateTimeOffset LoggedAt);
internal sealed record CodexTaskLifecycle(bool Running, string? TurnId, DateTimeOffset? StartedAt);
internal interface ICodexTaskMetadataStore { IReadOnlyList<CodexTaskMetadata> Read(IReadOnlyList<CodexTaskOwner> owners, CancellationToken ct); }
internal interface ICodexTaskOwnerSource { IReadOnlyList<CodexTaskOwner> Read(CancellationToken ct); }
internal interface ICodexTaskLifecycleSource { CodexTaskLifecycle Read(string path, string id, CancellationToken ct); }

/// <summary>Local lifecycle + live-owner metadata only. No app-server turn, credential or HID access.</summary>
internal sealed class NativeCodexTaskReader : ICodexTaskReader, IDisposable
{
    private readonly ICodexTaskMetadataStore _metadata;
    private readonly ICodexTaskOwnerSource _owners;
    private readonly ICodexTaskLifecycleSource _lifecycle;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NativeCodexTaskReader() : this(
        new CodexTaskSqliteStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")),
        new CodexTaskOwnerSource(),
        new CodexTaskLifecycleReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")), TimeProvider.System) { }

    internal NativeCodexTaskReader(ICodexTaskMetadataStore metadata, ICodexTaskOwnerSource owners,
        ICodexTaskLifecycleSource lifecycle, TimeProvider time)
    { _metadata = metadata; _owners = owners; _lifecycle = lifecycle; _time = time; }

    public async Task<CodexTaskSnapshot> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Read(ct), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();

    private CodexTaskSnapshot Read(CancellationToken ct)
    {
        try
        {
            var now = _time.GetUtcNow();
            var owners = _owners.Read(ct);
            if (owners.Count > 32) throw new InvalidDataException("Codex owner bound");
            if (owners.Count == 0) return new("Ready", Array.Empty<CodexRunningTask>(), now);
            var metadata = _metadata.Read(owners, ct);
            if (metadata.Count > 512 || metadata.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != metadata.Count)
                throw new InvalidDataException("Codex task metadata bound or duplicates");
            var tasks = new List<CodexRunningTask>();
            foreach (var item in metadata)
            {
                ct.ThrowIfCancellationRequested();
                var owner = owners.SingleOrDefault(x => x.Pid == item.OwnerPid);
                if (owner is null || owner.CreatedAt > item.LoggedAt) continue; // Dead owner or PID reuse is not a running task.
                if (item.LoggedAt > now + TimeSpan.FromSeconds(2)) throw new InvalidDataException("Future Codex metadata");
                var lifecycle = _lifecycle.Read(item.RolloutPath, item.Id, ct);
                if (!lifecycle.Running) continue;
                if (lifecycle.TurnId != item.TurnId || lifecycle.StartedAt is not { } start || start > now)
                    throw new InvalidDataException("Codex lifecycle/live owner disagreement");
                tasks.Add(new(item.Id, item.Title, start));
            }
            return new("Ready", tasks.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(), now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or
            KeyNotFoundException or TimeoutException or System.ComponentModel.Win32Exception or
            DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { return new("Unavailable", Array.Empty<CodexRunningTask>(), _time.GetUtcNow()); }
    }
}

internal sealed partial class CodexTaskSqliteStore(string root, ICodexTaskLifecycleSource? lifecycle = null) : ICodexTaskMetadataStore
{
    private readonly Dictionary<string, (long Id, string Turn, int Pid, DateTimeOffset At)> _latest = new(StringComparer.Ordinal);
    private readonly ICodexTaskLifecycleSource _lifecycle = lifecycle ?? new CodexTaskLifecycleReader(root);
    private string _ownerKey = "";
    private long _lastId;

    public IReadOnlyList<CodexTaskMetadata> Read(IReadOnlyList<CodexTaskOwner> owners, CancellationToken ct)
    {
        var key = string.Join(';', owners.OrderBy(x => x.Pid).Select(x => x.Pid.ToString(CultureInfo.InvariantCulture) + ":" + x.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)));
        if (_ownerKey != key) { _latest.Clear(); _lastId = 0; _ownerKey = key; }
        using var logs = new CodexReadOnlySqlite(Path.Combine(root, "logs_2.sqlite"), ct);
        var maximum = logs.Rows("SELECT coalesce(max(id),0) FROM logs", [], 1)[0][0];
        var maxId = long.Parse(maximum, CultureInfo.InvariantCulture);
        if (maxId < _lastId) { _latest.Clear(); _lastId = 0; }
        var filters = string.Join(" OR ", owners.Select((_, i) => "process_uuid LIKE ?" + (i + 3).ToString(CultureInfo.InvariantCulture)));
        var args = new List<string> { _lastId.ToString(CultureInfo.InvariantCulture), maxId.ToString(CultureInfo.InvariantCulture) };
        args.AddRange(owners.Select(x => "pid:" + x.Pid.ToString(CultureInfo.InvariantCulture) + ":%"));
        var rows = logs.Rows("SELECT id,thread_id,ts,ts_nanos,process_uuid,substr(feedback_log_body,1,4096) FROM logs WHERE id>?1 AND id<=?2 AND target='codex_core::session::turn' AND thread_id IS NOT NULL AND (" + filters + ") ORDER BY id", args, 65536);
        // The bounded log batch may contain many historical chats. First keep its latest row per chat,
        // then enforce the existing limit on current live candidates after trusted state/lifecycle filtering.
        // Do not publish a new cursor or partially mutate the retained cache if any validation fails.
        var latest = new Dictionary<string, (long Id, string Turn, int Pid, DateTimeOffset At)>(_latest, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var process = ProcessPattern().Match(row[4]);
            var turn = TurnPattern().Match(row[5]);
            if (!process.Success || !Guid.TryParse(row[1], out _)) throw new InvalidDataException("Codex metadata format changed");
            // This target also records setup/cleanup outside a submitted turn. Those rows have no turn span.
            if (!turn.Success) continue;
            var id = long.Parse(row[0], CultureInfo.InvariantCulture);
            var seconds = long.Parse(row[2], CultureInfo.InvariantCulture);
            var nanos = long.Parse(row[3], CultureInfo.InvariantCulture);
            if (nanos is < 0 or >= 1_000_000_000) throw new InvalidDataException("Codex metadata timestamp");
            latest[row[1]] = (id, turn.Groups[1].Value, int.Parse(process.Groups[1].Value, CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100));
        }
        if (latest.Count == 0) { _lastId = maxId; return Array.Empty<CodexTaskMetadata>(); }
        using var state = new CodexReadOnlySqlite(Path.Combine(root, "state_5.sqlite"), ct);
        var result = new List<CodexTaskMetadata>();
        var retained = new Dictionary<string, (long Id, string Turn, int Pid, DateTimeOffset At)>(StringComparer.Ordinal);
        foreach (var (id, log) in latest)
        {
            var owner = owners.SingleOrDefault(x => x.Pid == log.Pid);
            if (owner is null || owner.CreatedAt > log.At) continue;
            var items = state.Rows("SELECT id,substr(coalesce(nullif(name,''),nullif(title,''),'未命名任务'),1,512),rollout_path FROM threads WHERE id=?1 AND archived=0 AND (thread_source='user' OR thread_source IS NULL) AND source IN ('vscode','appServer','cli') AND agent_path IS NULL", [id], 1);
            if (items.Count == 0) continue; // Archived/subagent/noninteractive records are deliberately excluded.
            var item = items[0];
            var observed = _lifecycle.Read(item[2], id, ct);
            if (observed.TurnId != log.Turn) throw new InvalidDataException("Codex candidate lifecycle disagreement");
            if (!observed.Running) continue; // Only an exact terminal turn permits removal; unknown data never means idle.
            result.Add(new(item[0], item[1], item[2], log.Turn, log.Pid, log.At));
            retained.Add(id, log);
            if (result.Count > 512) throw new InvalidDataException("Codex candidate bound");
        }
        _latest.Clear();
        foreach (var entry in retained) _latest.Add(entry.Key, entry.Value);
        _lastId = maxId;
        return result;
    }

    [GeneratedRegex(@"^pid:([0-9]{1,10}):[a-fA-F0-9-]{36}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessPattern();
    [GeneratedRegex("(?<![A-Za-z0-9_])turn_id[=:\" ]+([a-f0-9-]{36})", RegexOptions.CultureInvariant)]
    private static partial Regex TurnPattern();
}

internal sealed class CodexReadOnlySqlite : IDisposable
{
    private nint _db;
    private readonly CancellationToken _ct;
    private readonly Progress _progress;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private const string Library = "winsqlite3.dll";
    public CodexReadOnlySqlite(string path, CancellationToken ct)
    {
        _ct = ct;
        CodexTaskLifecycleReader.NoReparse(path);
        if (!File.Exists(path)) throw new IOException("Codex metadata missing");
        _progress = () => ct.IsCancellationRequested || _elapsed.Elapsed > TimeSpan.FromSeconds(4) ? 1 : 0;
        try
        {
            Check(Open(path, out _db, 1 /* SQLITE_OPEN_READONLY */, 0));
            Check(BusyTimeout(_db, 500));
            SetProgress(_db, 2000, _progress, 0);
            var result = Exec(_db, "PRAGMA query_only=ON", 0, 0, out var error);
            if (error != 0) Free(error);
            Check(result);
        }
        catch { Dispose(); throw; }
    }
    public IReadOnlyList<string[]> Rows(string sql, IReadOnlyList<string> args, int maximum)
    {
        _ct.ThrowIfCancellationRequested();
        nint statement = 0;
        try
        {
            Check(Prepare(_db, sql, -1, out statement, 0));
            for (var i = 0; i < args.Count; i++) Check(BindText(statement, i + 1, args[i], -1, new nint(-1)));
            var result = new List<string[]>();
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                var step = Step(statement);
                if (step == 101) break;
                if (step != 100) { Check(step); throw new IOException("Codex metadata query failed"); }
                if (result.Count >= maximum) throw new InvalidDataException("Codex metadata row bound");
                var count = ColumnCount(statement);
                if (count is < 1 or > 8) throw new InvalidDataException("Codex metadata column bound");
                var row = new string[count];
                for (var i = 0; i < count; i++)
                {
                    var length = ColumnBytes(statement, i);
                    if (length is < 0 or > 16384) throw new InvalidDataException("Codex metadata text bound");
                    var pointer = ColumnText(statement, i);
                    row[i] = pointer == 0 ? "" : Marshal.PtrToStringUTF8(pointer, length) ?? "";
                }
                result.Add(row);
            }
            return result;
        }
        finally { if (statement != 0) _ = FinalizeStatement(statement); }
    }
    private void Check(int code)
    {
        _ct.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException("Read-only Codex SQLite operation failed (" + code.ToString(CultureInfo.InvariantCulture) + ")");
    }
    public void Dispose() { if (_db != 0) { _ = Close(_db); _db = 0; } }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress();
    [DllImport(Library, EntryPoint="sqlite3_open_v2", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DllImport(Library, EntryPoint="sqlite3_close_v2", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int Close(nint database);
    [DllImport(Library, EntryPoint="sqlite3_busy_timeout", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int BusyTimeout(nint database, int milliseconds);
    [DllImport(Library, EntryPoint="sqlite3_progress_handler", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void SetProgress(nint database, int count, Progress progress, nint context);
    [DllImport(Library, EntryPoint="sqlite3_exec", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int Exec(nint database,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,nint callback,nint context,out nint error);
    [DllImport(Library, EntryPoint="sqlite3_free", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void Free(nint value);
    [DllImport(Library, EntryPoint="sqlite3_prepare_v2", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int Prepare(nint database,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,int bytes,out nint statement,nint tail);
    [DllImport(Library, EntryPoint="sqlite3_bind_text", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int BindText(nint statement,int position,[MarshalAs(UnmanagedType.LPUTF8Str)] string value,int bytes,nint destructor);
    [DllImport(Library, EntryPoint="sqlite3_step", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int Step(nint statement);
    [DllImport(Library, EntryPoint="sqlite3_finalize", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int FinalizeStatement(nint statement);
    [DllImport(Library, EntryPoint="sqlite3_column_count", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int ColumnCount(nint statement);
    [DllImport(Library, EntryPoint="sqlite3_column_text", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint ColumnText(nint statement,int column);
    [DllImport(Library, EntryPoint="sqlite3_column_bytes", CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int ColumnBytes(nint statement,int column);
}

internal sealed class CodexTaskOwnerSource : ICodexTaskOwnerSource
{
    internal const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";

    public IReadOnlyList<CodexTaskOwner> Read(CancellationToken ct)
    {
        var result = new List<CodexTaskOwner>();
        var allowed = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin")) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var image = process.MainModule?.FileName;
                    if (image is null || !Path.GetFileName(image).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsAllowedImage(image, allowed, null, null))
                    {
                        var package = ReadPackage(process.Id);
                        if (!IsAllowedImage(image, allowed, package.Family, package.Root)) continue;
                    }
                    var command = CommandLine(process.Id);
                    if (!Regex.IsMatch(command, @"(?:^|\s)app-server(?:\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) continue;
                    result.Add(new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime())));
                }
                catch (InvalidOperationException) { /* Process exited while being inspected. */ }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 87 or 1168) { }
            }
        }
        return result;
    }

    internal static bool IsAllowedImage(string image, string legacyDirectory, string? packageFamily, string? packageRoot)
    {
        var path = Path.GetFullPath(image);
        if (!Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var legacy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDirectory)) + Path.DirectorySeparatorChar;
        if (path.StartsWith(legacy, StringComparison.OrdinalIgnoreCase)) return true;
        return packageFamily == PackageFamily && packageRoot is not null
            && path.Equals(Path.GetFullPath(Path.Combine(packageRoot, "app", "resources", "codex.exe")), StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Family, string? Root) ReadPackage(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var family = ReadPackageString((ref uint length, char[]? value) => GetPackageFamilyName(process, ref length, value));
        if (family != PackageFamily) return (family, null);
        var fullName = ReadPackageString((ref uint length, char[]? value) => GetPackageFullName(process, ref length, value))
            ?? throw new IOException("Codex package identity changed during inspection.");
        var root = ReadPackageString((ref uint length, char[]? value) => GetPackagePathByFullName(fullName, ref length, value))
            ?? throw new IOException("Codex package location is unavailable.");
        return (family, root);
    }

    private delegate int PackageString(ref uint length, char[]? value);

    private static string? ReadPackageString(PackageString query)
    {
        uint length = 0;
        var error = query(ref length, null);
        if (error == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE is normal for the legacy install.
        if (error != 122) throw new System.ComponentModel.Win32Exception(error);
        if (length is 0 or > 32768) throw new InvalidDataException("Codex package identity length.");
        var buffer = new char[length];
        error = query(ref length, buffer);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        if (length == 0 || length > buffer.Length || buffer[length - 1] != '\0')
            throw new InvalidDataException("Codex package identity termination.");
        return new string(buffer, 0, checked((int)length - 1));
    }
    private static string CommandLine(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            var status = QueryProcess(process, 60, buffer, 65536, out var returned);
            if (status != 0 || returned is < 16 or > 65536) throw new IOException("Codex process identity unavailable");
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (value.Length % 2 != 0 || value.Length > 32768 || offset < 0 || offset + value.Length > returned)
                throw new InvalidDataException("Codex process command bound");
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal nint Buffer; }
    [DllImport("kernel32.dll",SetLastError=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, [Out] char[]? value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, [Out] char[]? value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, [Out] char[]? value);
    [DllImport("ntdll.dll",EntryPoint="NtQueryInformationProcess")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int QueryProcess(SafeProcessHandle process,int information,nint buffer,int length,out int returned);
}
