using System.Text;
using System.Text.Json.Nodes;

namespace UnifiedRgb.Setup.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("urgb-setup-").FullName;

    private string ConfigPath => Path.Combine(_dir, "appsettings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SetupReport NoHardware() => new FakeSetupSystem().Inspect();

    private static readonly SetupSelection Demo = new(SetupMode.Demo, []);

    private const string NonDemo = """{ "UnifiedRgb": { "UseVirtualAdapter": false, "Unknown": { "a": [1, "b"] } }, "Other": 1 }""";

    [Fact]
    public void Save_publishes_the_plan_and_keeps_the_exact_previous_file_as_backup()
    {
        var original = Encoding.UTF8.GetBytes(NonDemo);
        File.WriteAllBytes(ConfigPath, original);
        var store = new SetupConfigStore(ConfigPath);
        var snapshot = store.Read();
        var plan = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);

        var result = store.SavePlan(plan, snapshot.Sha256, CancellationToken.None);

        result.Status.Should().Be(SetupSaveStatus.Saved, result.Detail);
        File.ReadAllBytes(result.BackupPath!).Should().Equal(original);
        var written = File.ReadAllBytes(ConfigPath);
        SetupConfigDocument.Sha256(written).Should().Be(plan.ProposedSha256).And.Be(result.WrittenSha256);
        written.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        var root = JsonNode.Parse(written)!;
        root["UnifiedRgb"]!["Unknown"]!.ToJsonString().Should().Be("""{"a":[1,"b"]}""");
        root["Other"]!.GetValue<int>().Should().Be(1);
        Directory.GetFiles(_dir).Should().HaveCount(2, "only the config and its backup; no staging leftovers");
    }

    [Fact]
    public void A_file_changed_after_inspection_is_never_overwritten()
    {
        File.WriteAllText(ConfigPath, NonDemo);
        var store = new SetupConfigStore(ConfigPath);
        var snapshot = store.Read();
        var plan = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);
        const string edited = """{ "UnifiedRgb": { "UseVirtualAdapter": false, "EditedMeanwhile": true } }""";
        File.WriteAllText(ConfigPath, edited);

        var result = store.SavePlan(plan, snapshot.Sha256, CancellationToken.None);

        result.Status.Should().Be(SetupSaveStatus.Stale);
        File.ReadAllText(ConfigPath).Should().Be(edited);
        Directory.GetFiles(_dir).Should().ContainSingle();
    }

    [Fact]
    public void A_change_racing_the_swap_is_detected_and_the_racing_content_restored()
    {
        File.WriteAllText(ConfigPath, NonDemo);
        const string racing = """{ "Racing": 1 }""";
        var files = new ScriptedFiles { BeforeReplace = () => File.WriteAllText(ConfigPath, racing) };
        var store = new SetupConfigStore(ConfigPath, files);
        var snapshot = store.Read();
        var plan = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);

        var result = store.SavePlan(plan, snapshot.Sha256, CancellationToken.None);

        result.Status.Should().Be(SetupSaveStatus.Stale);
        File.ReadAllText(ConfigPath).Should().Be(racing);
    }

    [Fact]
    public void A_failed_publication_check_rolls_back_to_the_original_bytes()
    {
        var original = Encoding.UTF8.GetBytes(NonDemo);
        File.WriteAllBytes(ConfigPath, original);
        var files = new ScriptedFiles();
        var store = new SetupConfigStore(ConfigPath, files);
        var snapshot = store.Read();
        var plan = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);
        files.CorruptReadBackOf = ConfigPath;

        var result = store.SavePlan(plan, snapshot.Sha256, CancellationToken.None);

        result.Status.Should().Be(SetupSaveStatus.RolledBack, result.Detail);
        File.ReadAllBytes(ConfigPath).Should().Equal(original);
        File.ReadAllBytes(result.BackupPath!).Should().Equal(original);
    }

    [Fact]
    public void A_failed_swap_leaves_the_original_untouched()
    {
        File.WriteAllText(ConfigPath, NonDemo);
        var files = new ScriptedFiles { FailReplace = true };
        var store = new SetupConfigStore(ConfigPath, files);
        var snapshot = store.Read();

        var result = store.SavePlan(SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot), snapshot.Sha256, CancellationToken.None);

        result.Status.Should().Be(SetupSaveStatus.Failed);
        File.ReadAllText(ConfigPath).Should().Be(NonDemo);
        Directory.GetFiles(_dir).Should().ContainSingle("the staged file is removed");
    }

    [Fact]
    public void Fresh_install_creates_the_file_but_never_replaces_one_that_appeared_meanwhile()
    {
        var store = new SetupConfigStore(ConfigPath);
        var snapshot = store.Read();
        snapshot.Exists.Should().BeFalse();
        var plan = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);

        File.WriteAllText(ConfigPath, "{}");
        store.SavePlan(plan, snapshot.Sha256, CancellationToken.None).Status.Should().Be(SetupSaveStatus.Stale);
        File.ReadAllText(ConfigPath).Should().Be("{}");

        File.Delete(ConfigPath);
        var created = store.SavePlan(plan, snapshot.Sha256, CancellationToken.None);
        created.Status.Should().Be(SetupSaveStatus.Saved);
        created.BackupPath.Should().BeNull();
        JsonNode.Parse(File.ReadAllText(ConfigPath))!["UnifiedRgb"]!["UseVirtualAdapter"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Plans_from_another_snapshot_or_with_errors_are_refused_and_unchanged_plans_write_nothing()
    {
        File.WriteAllText(ConfigPath, """{ "UnifiedRgb": { "UseVirtualAdapter": true, "EnableLianLi": false, "EnableMsiGpu": false, "EnableAsusAura": false, "EnableEneDram": false, "EnableOpenRgb": false, "Screens": { "Enabled": false } } }""");
        var store = new SetupConfigStore(ConfigPath);
        var snapshot = store.Read();
        var unchanged = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);
        var before = File.GetLastWriteTimeUtc(ConfigPath);

        unchanged.HasChanges.Should().BeFalse();
        store.SavePlan(unchanged, snapshot.Sha256, CancellationToken.None).Status.Should().Be(SetupSaveStatus.NoChanges);
        store.SavePlan(unchanged, new string('0', 64), CancellationToken.None).Status.Should().Be(SetupSaveStatus.Refused);
        var invalid = SetupPlanner.BuildPlan(NoHardware(), new SetupSelection(SetupMode.Hardware, [new(SetupFamily.LianLi, true, true)]), snapshot);
        store.SavePlan(invalid, snapshot.Sha256, CancellationToken.None).Status.Should().Be(SetupSaveStatus.Refused);
        store.SavePlan(unchanged with { HasChanges = true, ProposedJson = "{}" }, snapshot.Sha256, CancellationToken.None)
            .Status.Should().Be(SetupSaveStatus.Refused, "a tampered plan no longer matches its hash");
        File.GetLastWriteTimeUtc(ConfigPath).Should().Be(before);
    }

    public static TheoryData<string, byte[]> BadFiles => new()
    {
        { "comment", Encoding.UTF8.GetBytes("{ // note\n \"a\": 1 }") },
        { "trailing comma", Encoding.UTF8.GetBytes("{ \"a\": 1, }") },
        { "duplicate", Encoding.UTF8.GetBytes("{ \"UnifiedRgb\": {}, \"unifiedRGB\": {} }") },
        { "nested duplicate", Encoding.UTF8.GetBytes("{ \"UnifiedRgb\": { \"EnableLianLi\": true, \"enableLianLi\": false } }") },
        { "array root", Encoding.UTF8.GetBytes("[ {} ]") },
        { "scalar root", Encoding.UTF8.GetBytes("1") },
        { "invalid utf8", [(byte)'{', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)':', (byte)'1', (byte)'}'] },
        { "truncated", Encoding.UTF8.GetBytes("{ \"a\": [1, 2") },
        { "too deep", Encoding.UTF8.GetBytes("{\"a\":" + string.Concat(Enumerable.Repeat("[", 40)) + string.Concat(Enumerable.Repeat("]", 40)) + "}") },
        { "oversized", Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', SetupConfigDocument.MaxBytes) + "\"}") },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void Malformed_or_unexpected_files_are_refused_and_never_overwritten(string name, byte[] content)
    {
        File.WriteAllBytes(ConfigPath, content);
        var store = new SetupConfigStore(ConfigPath);

        var snapshot = store.Read();
        var plan = SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot);
        var result = store.SavePlan(plan, snapshot.Sha256, CancellationToken.None);

        snapshot.Error.Should().NotBeNull(name);
        plan.CanSave.Should().BeFalse();
        result.Status.Should().Be(SetupSaveStatus.Refused);
        File.ReadAllBytes(ConfigPath).Should().Equal(content);
    }

    [Fact]
    public void A_bom_prefixed_file_is_accepted_and_written_back_without_bom()
    {
        File.WriteAllBytes(ConfigPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(NonDemo)]);
        var store = new SetupConfigStore(ConfigPath);
        var snapshot = store.Read();

        var result = store.SavePlan(SetupPlanner.BuildPlan(NoHardware(), Demo, snapshot), snapshot.Sha256, CancellationToken.None);

        result.Status.Should().Be(SetupSaveStatus.Saved);
        File.ReadAllBytes(ConfigPath)[0].Should().Be((byte)'{');
    }

    [Fact]
    public void A_linked_config_file_is_refused()
    {
        File.WriteAllText(ConfigPath, NonDemo);
        var store = new SetupConfigStore(ConfigPath, new ScriptedFiles { ReparsePoint = true });

        store.Read().Error.Should().Contain("reparse");
    }

    [Fact]
    public void Relative_store_paths_are_rejected()
    {
        var act = () => new SetupConfigStore("appsettings.json");
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>The physical file system with scripted faults around the swap.</summary>
    private sealed class ScriptedFiles : ISetupFileSystem
    {
        private readonly PhysicalSetupFileSystem _inner = new();
        private bool _swapped;

        public Action? BeforeReplace { get; init; }

        public bool FailReplace { get; init; }

        public bool ReparsePoint { get; init; }

        public string? CorruptReadBackOf { get; set; }

        public byte[]? ReadBounded(string path, int maxBytes)
        {
            var bytes = _inner.ReadBounded(path, maxBytes);
            return _swapped && path == CorruptReadBackOf && bytes is not null ? [.. bytes, (byte)' '] : bytes;
        }

        public bool IsReparsePoint(string path) => ReparsePoint || _inner.IsReparsePoint(path);

        public void WriteNew(string path, byte[] content) => _inner.WriteNew(path, content);

        public void Replace(string source, string destination, string backup)
        {
            if (FailReplace)
            {
                throw new IOException("sharing violation");
            }

            if (!_swapped)
            {
                BeforeReplace?.Invoke();
            }

            _inner.Replace(source, destination, backup);
            if (!_swapped)
            {
                _swapped = true;
            }
            else
            {
                CorruptReadBackOf = null; // after the restore, read back truthfully
            }
        }

        public void MoveNew(string source, string destination) => _inner.MoveNew(source, destination);

        public void Delete(string path) => _inner.Delete(path);
    }
}
