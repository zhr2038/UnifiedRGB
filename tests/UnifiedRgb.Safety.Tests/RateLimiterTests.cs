using System.Diagnostics;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Safety.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task Holds_20_fps_within_1_fps_over_5_seconds()
    {
        var sink = new RecordingTransport();
        var limiter = new RateLimiter(sink, 20);
        var frame = new byte[] { 16, 32, 0 };
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await limiter.SendAsync(frame, CancellationToken.None);
        }

        var fps = sink.SentFrames.Count / sw.Elapsed.TotalSeconds;
        fps.Should().BeInRange(19.0, 21.0);
    }

    [Fact]
    public void TryTake_refuses_when_bucket_empty()
    {
        var bucket = new TokenBucket(10);
        bucket.TryTake().Should().BeTrue();
        bucket.TryTake(out var wait).Should().BeFalse();
        wait.Should().BeGreaterThan(0).And.BeLessOrEqualTo(0.1);
    }

    [Fact]
    public async Task Cancellation_is_honoured_while_waiting()
    {
        var limiter = new RateLimiter(new RecordingTransport(), 0.5);
        await limiter.SendAsync(new byte[] { 1 }, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var act = async () => await limiter.SendAsync(new byte[] { 1 }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
