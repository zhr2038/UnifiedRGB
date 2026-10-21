using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class MediaLifecycleAuditTests
{
    [Fact]
    public void Scratch_cleanup_cannot_hide_the_original_decode_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-audit-scratch", Guid.NewGuid().ToString("N"));
        FileStream? scanner = null;
        string? used = null;
        var failure = new InvalidDataException("original decode failure");
        try
        {
            var import = () => Mp4Importer.WithInput([1, 2, 3], root, path =>
            {
                used = path;
                File.ReadAllBytes(path).Should().Equal(1, 2, 3);
                scanner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw failure;
            });
            import.Should().Throw<InvalidDataException>().Which.Should().BeSameAs(failure);
            used.Should().StartWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            File.Exists(used).Should().BeTrue("a scanner without delete sharing keeps this failed scratch file for later cleanup");
        }
        finally { scanner?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sta_dispose_from_worker_releases_the_queue_when_the_worker_eventually_exits()
    {
        var sta = new StaThread("audit-self-dispose");
        await sta.InvokeAsync(() => { sta.Dispose(); return 1; }, default);
        sta.Dispose(); // idempotent, including the caller arriving before worker finally
        var call = () => sta.InvokeAsync(() => 2, default);
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }
}
