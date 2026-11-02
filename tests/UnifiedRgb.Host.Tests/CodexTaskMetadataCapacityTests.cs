using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>Disposable SQLite and rollout fixtures only; never reads the user's Codex stores or launches processes.</summary>
public sealed class CodexTaskMetadataCapacityTests
{
    [Fact]
    public void Historical_terminal_chats_are_filtered_before_the_unchanged_live_capacity_limit()
    {
        using var fixture = new MetadataFixture();
        for (var i = 0; i < 513; i++) fixture.Add(running: false);
        var active = fixture.Add(running: true);
        var store = new CodexTaskSqliteStore(fixture.Root);

        store.Read(fixture.Owners, default).Should().ContainSingle().Which.Id.Should().Be(active.Id);
        store.Read(fixture.Owners, default).Should().ContainSingle().Which.Id.Should().Be(active.Id);

        fixture.Complete(active);
        store.Read(fixture.Owners, default).Should().BeEmpty("a matching terminal event retires the retained candidate without another log row");
        fixture.Restart(active);
        store.Read(fixture.Owners, default).Should().ContainSingle().Which.Id.Should().Be(active.Id);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mismatch")]
    public async Task Unknown_or_mismatched_terminal_metadata_is_unavailable_and_does_not_commit_the_cursor(string fault)
    {
        using var fixture = new MetadataFixture();
        var active = fixture.Add(running: true);
        var lifecycle = new ControlledLifecycle(active.Turn, fault);
        var store = new CodexTaskSqliteStore(fixture.Root, lifecycle);
        using var reader = new NativeCodexTaskReader(store, new FixtureOwners(fixture.Owners), lifecycle, TimeProvider.System);
        var result = await reader.ReadAsync(default);
        result.State.Should().Be("Unavailable");
        result.Tasks.Should().BeEmpty();

        lifecycle.Fault = null;
        result = await reader.ReadAsync(default);
        result.State.Should().Be("Ready");
        result.Tasks.Should().ContainSingle().Which.Id.Should().Be(active.Id,
            "an unsuccessful metadata scan must not advance past an unretained active row");
    }

    [Fact]
    public void More_than_512_genuinely_running_chats_are_refused_and_a_later_matching_completion_recovers()
    {
        using var fixture = new MetadataFixture();
        var active = fixture.Add(running: true);
        for (var i = 0; i < 512; i++) fixture.Add(running: true);
        var store = new CodexTaskSqliteStore(fixture.Root);
        ((Action)(() => store.Read(fixture.Owners, default))).Should().Throw<InvalidDataException>().WithMessage("*candidate bound*");
        fixture.Complete(active);
        store.Read(fixture.Owners, default).Should().HaveCount(512,
            "the prior over-capacity failure must not permanently poison the retained cache or cursor");
    }

    [Fact]
    public void Archived_or_noninteractive_history_needs_no_lifecycle_read_and_does_not_consume_live_capacity()
    {
        using var fixture = new MetadataFixture();
        for (var i = 0; i < 513; i++) fixture.Add(running: true, archived: true);
        var active = fixture.Add(running: true);
        var store = new CodexTaskSqliteStore(fixture.Root);
        store.Read(fixture.Owners, default).Should().ContainSingle().Which.Id.Should().Be(active.Id);
    }

    private sealed class FixtureOwners(IReadOnlyList<CodexTaskOwner> owners) : ICodexTaskOwnerSource
    {
        public IReadOnlyList<CodexTaskOwner> Read(CancellationToken ct) => owners;
    }

    private sealed class ControlledLifecycle(string turn, string? fault) : ICodexTaskLifecycleSource
    {
        public string? Fault { get; set; } = fault;
        public CodexTaskLifecycle Read(string path, string id, CancellationToken ct) => Fault switch
        {
            "unknown" => throw new InvalidDataException("unknown lifecycle fixture"),
            "mismatch" => new(false, Guid.Empty.ToString(), null),
            _ => new(true, turn, DateTimeOffset.UtcNow.AddMinutes(-1)),
        };
    }

    private sealed class MetadataFixture : IDisposable
    {
        private readonly FixtureDatabase _logs;
        private readonly FixtureDatabase _state;
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "urgb-metadata-capacity-" + Guid.NewGuid().ToString("N"));
        public IReadOnlyList<CodexTaskOwner> Owners { get; } = [new(71, DateTimeOffset.UtcNow.AddHours(-1))];
        public MetadataFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "sessions"));
            _logs = new(Path.Combine(Root, "logs_2.sqlite"));
            _state = new(Path.Combine(Root, "state_5.sqlite"));
            _logs.Execute("CREATE TABLE logs(id INTEGER PRIMARY KEY AUTOINCREMENT,thread_id TEXT,ts INTEGER,ts_nanos INTEGER,process_uuid TEXT,feedback_log_body TEXT,target TEXT)");
            _state.Execute("CREATE TABLE threads(id TEXT PRIMARY KEY,name TEXT,title TEXT,rollout_path TEXT,archived INTEGER,thread_source TEXT,source TEXT,agent_path TEXT)");
        }

        public (string Id, string Turn, string Path) Add(bool running, bool archived = false)
        {
            var id = Guid.NewGuid().ToString(); var turn = Guid.NewGuid().ToString();
            var path = Path.Combine(Root, "sessions", "rollout-fixture-" + id + ".jsonl");
            _state.Execute($"INSERT INTO threads VALUES('{id}','fixture task','','{Quote(path)}',{(archived ? 1 : 0)},'user','appServer',NULL)");
            // Old rows for the same chat must be grouped before the terminal/current turn is inspected.
            Log(id, Guid.NewGuid().ToString()); Log(id, turn);
            Append(path, "task_started", turn);
            if (!running) Append(path, "task_complete", turn);
            return (id, turn, path);
        }

        public void Complete((string Id, string Turn, string Path) item) => Append(item.Path, "task_complete", item.Turn);
        public void Restart((string Id, string Turn, string Path) item)
        {
            var turn = Guid.NewGuid().ToString();
            Log(item.Id, turn); Append(item.Path, "task_started", turn);
        }

        private void Log(string id, string turn) => _logs.Execute($"INSERT INTO logs(thread_id,ts,ts_nanos,process_uuid,feedback_log_body,target) VALUES('{id}',{_now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)},0,'pid:71:{Guid.Empty}','turn_id={turn}','codex_core::session::turn')");
        private void Append(string path, string kind, string turn) => File.AppendAllText(path,
            JsonSerializer.Serialize(new { timestamp = _now.AddMinutes(-1), type = "event_msg", payload = new { type = kind, turn_id = turn } }) + "\n");
        private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        public void Dispose()
        {
            _logs.Dispose(); _state.Dispose();
            var actual = Path.GetFullPath(Root);
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "urgb-metadata-capacity-"));
            if (!actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup scope");
            Directory.Delete(actual, recursive: true);
        }
    }

    private sealed class FixtureDatabase : IDisposable
    {
        private nint _database;
        public FixtureDatabase(string path)
        {
            if (Open(path, out _database, 6, 0) != 0) throw new IOException("Cannot create fixture database");
            // Crash durability is irrelevant for throwaway fixture construction; production readers remain read-only.
            Execute("PRAGMA synchronous=OFF; PRAGMA journal_mode=MEMORY");
        }
        public void Execute(string sql)
        {
            var code = Exec(_database, sql, 0, 0, out var error);
            if (error != 0) Free(error);
            if (code != 0) throw new IOException("Fixture SQL failed: " + code.ToString(CultureInfo.InvariantCulture));
        }
        public void Dispose() { if (_database != 0) { _ = Close(_database); _database = 0; } }
#pragma warning disable CA2101 // SQLite's C API requires explicitly marshalled UTF-8, including for the disposable fixture paths.
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int Exec(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint context, out nint error);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void Free(nint value);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int Close(nint database);
#pragma warning restore CA2101
    }
}
