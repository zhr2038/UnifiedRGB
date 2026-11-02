using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexTaskCacheAuditTests
{
    [Fact]
    public void More_than_512_chats_evict_old_reads_and_revisit_fresh_lifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-task-cache-audit-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            var reader = new CodexTaskLifecycleReader(root);
            var firstId = Guid.NewGuid().ToString();
            var firstPath = AppendStart(sessions, firstId);
            reader.Read(firstPath, firstId, default).Running.Should().BeTrue();
            for (var i = 0; i < 512; i++)
            {
                var id = Guid.NewGuid().ToString();
                reader.Read(AppendStart(sessions, id), id, default).Running.Should().BeTrue();
            }

            File.AppendAllText(firstPath, JsonSerializer.Serialize(new
            {
                type = "event_msg", payload = new { type = "task_complete", turn_id = firstId },
            }) + "\n");
            reader.Read(firstPath, firstId, default).Running.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string AppendStart(string sessions, string id)
    {
        var path = Path.Combine(sessions, "rollout-audit-" + id + ".jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            type = "event_msg",
            payload = new { type = "task_started", turn_id = id },
        }) + "\n");
        return path;
    }
}
