using System.Text;
using System.Text.Json;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

public sealed class SceneStateRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urgb-state-tests", Guid.NewGuid().ToString("N"));
    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private string At(string p) => Path.Combine(_root, p.Replace('/', Path.DirectorySeparatorChar));
    private void Seed(string p, string value) { Directory.CreateDirectory(Path.GetDirectoryName(At(p))!); File.WriteAllBytes(At(p), B(value)); }

    [Fact]
    public void Two_file_commit_keeps_the_original_formats_and_finishes_without_a_journal()
    {
        Seed("profiles/active.json", "old rgb"); Seed("screens/scene.json", "old screen");
        SceneStateFiles.Commit(_root, [new("profiles/active.json", B("new rgb")), new("screens/scene.json", B("new screen"))]);
        File.ReadAllText(At("profiles/active.json")).Should().Be("new rgb");
        File.ReadAllText(At("screens/scene.json")).Should().Be("new screen");
        File.Exists(At(SceneStateFiles.JournalName)).Should().BeFalse();
    }

    [Fact]
    public void A_failure_after_first_replace_rolls_back_both_files_and_new_file_creation()
    {
        Seed("profiles/active.json", "old");
        var act = () => SceneStateFiles.CommitCore(_root,
            [new("profiles/active.json", B("new")), new("screens/scene.json", B("screen"))],
            i => { if (i == 1) throw new IOException("injected disk failure"); });
        act.Should().Throw<IOException>();
        File.ReadAllText(At("profiles/active.json")).Should().Be("old");
        File.Exists(At("screens/scene.json")).Should().BeFalse();
        File.Exists(At(SceneStateFiles.JournalName)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_resolves_partial_disk_state_to_one_complete_version(bool committed)
    {
        Seed("profiles/active.json", "old rgb"); Seed("screens/scene.json", "old screen");
        byte[]? prepared = null;
        var fail = () => SceneStateFiles.CommitCore(_root,
            [new("profiles/active.json", B("new rgb")), new("screens/scene.json", B("new screen"))],
            _ => { prepared = File.ReadAllBytes(At(SceneStateFiles.JournalName)); throw new IOException("crash snapshot"); });
        fail.Should().Throw<IOException>();
        var record = JsonSerializer.Deserialize(prepared!, SceneStateJsonContext.Default.SceneStateRecord)!;
        // Simulate a process disappearing after one replace, or after the durable commit marker.
        Seed("profiles/active.json", "new rgb");
        File.WriteAllBytes(At(SceneStateFiles.JournalName), JsonSerializer.SerializeToUtf8Bytes(record with { Committed = committed }, SceneStateJsonContext.Default.SceneStateRecord));
        SceneStateFiles.Recover(_root);
        File.ReadAllText(At("profiles/active.json")).Should().Be(committed ? "new rgb" : "old rgb");
        File.ReadAllText(At("screens/scene.json")).Should().Be(committed ? "new screen" : "old screen");
        File.Exists(At(SceneStateFiles.JournalName)).Should().BeFalse();
    }

    [Fact]
    public void Recovery_refuses_foreign_paths_and_retains_the_record()
    {
        Directory.CreateDirectory(_root);
        var bad = new SceneStateRecord(1, true, [new("../appsettings.json", null, [], null, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])))]);
        File.WriteAllBytes(At(SceneStateFiles.JournalName), JsonSerializer.SerializeToUtf8Bytes(bad, SceneStateJsonContext.Default.SceneStateRecord));
        var act = () => SceneStateFiles.Recover(_root);
        act.Should().Throw<InvalidDataException>();
        File.Exists(At(SceneStateFiles.JournalName)).Should().BeTrue();
        File.Exists(Path.Combine(_root, "..", "appsettings.json")).Should().BeFalse();
    }

    [Fact]
    public void A_directory_instead_of_the_screen_file_refuses_before_touching_rgb()
    {
        Seed("profiles/active.json", "old"); Directory.CreateDirectory(At("screens/scene.json"));
        var act = () => SceneStateFiles.Commit(_root, [new("profiles/active.json", B("new")), new("screens/scene.json", B("new"))]);
        act.Should().Throw<IOException>();
        File.ReadAllText(At("profiles/active.json")).Should().Be("old");
    }

    [Fact]
    public async Task Stop_waits_for_a_started_publication_but_cancels_an_uncommitted_lease()
    {
        await using var rig = new Rig();
        var scene = new UnifiedRgb.Contracts.Dto.ScreenSceneDto([new("screen:h2:test", BackgroundColor: "#123456")]);
        using var transaction = await rig.Manager.BeginSceneAsync(scene);
        var prepared = (IPreparedScreenSceneTransaction)transaction;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var publish = Task.Run(() => prepared.Coordinate(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
            prepared.Commit();
        }));
        entered.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        var stop = Task.Run(rig.Manager.StopAsync);
        try { await Task.Delay(30); stop.IsCompleted.Should().BeFalse(); }
        finally { release.Set(); }
#pragma warning disable VSTHRD003 // these exact test-owned tasks synchronize the publication/stop race
        await Task.WhenAll(publish, stop).WaitAsync(TimeSpan.FromSeconds(3));
#pragma warning restore VSTHRD003
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
        rig.Manager.MutationHeld.Should().BeFalse();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
