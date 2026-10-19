using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

public class ScreenReplyFlowTests
{
    [Fact]
    public void Ack_zlp_pairs_are_drained_until_an_actual_timeout()
    {
        var replies = new Queue<ScreenPipeRead>([new(512, false), new(0, false), new(512, false), new(0, false), new(0, true)]);
        var timeouts = new List<int>();
        ScreenPipeRead Read(Span<byte> bytes, int timeout) { timeouts.Add(timeout); bytes[0] = 0x66; return replies.Dequeue(); }
        Span<byte> response = stackalloc byte[512];
        ScreenReplyFlow.ReadAndDrain(Read, response, 2000).Should().Be(512);
        response[0].Should().Be(0x66);
        replies.Should().BeEmpty();
        timeouts.Should().Equal(2000, 10, 10, 10, 10);
    }

    [Fact]
    public void Leading_zlp_does_not_hide_the_actual_ack()
    {
        var replies = new Queue<ScreenPipeRead>([new(0, false), new(512, false), new(0, false), new(0, true)]);
        ScreenPipeRead Read(Span<byte> bytes, int timeout) => replies.Dequeue();
        ScreenReplyFlow.ReadAndDrain(Read, new byte[512], 2000).Should().Be(512);
        replies.Should().BeEmpty();
    }

    [Fact]
    public void No_response_timeout_is_not_a_successful_empty_packet()
    {
        var calls = 0;
        ScreenPipeRead Read(Span<byte> bytes, int timeout) { calls++; return new(0, true); }
        ScreenReplyFlow.ReadAndDrain(Read, new byte[512], 50).Should().Be(0);
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    public void A_stream_that_never_settles_has_a_finite_bound(int limit)
    {
        var calls = 0;
        ScreenPipeRead Read(Span<byte> bytes, int timeout) { calls++; timeout.Should().Be(10); return new(0, false); }
        Action drain = () => ScreenReplyFlow.Drain(Read, limit);
        drain.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ReplyBacklog);
        calls.Should().Be(limit);
    }

    [Fact]
    public void Open_drain_removes_the_measured_seventeen_ack_zlp_pairs()
    {
        var replies = new Queue<ScreenPipeRead>();
        for (var i = 0; i < 17; i++) { replies.Enqueue(new(512, false)); replies.Enqueue(new(0, false)); }
        replies.Enqueue(new(0, true));
        ScreenPipeRead Read(Span<byte> bytes, int timeout) => replies.Dequeue();
        ScreenReplyFlow.Drain(Read, ScreenReplyFlow.OpenDrainLimit);
        replies.Should().BeEmpty();
    }

    [Fact]
    public void Abort_or_disconnect_is_propagated_without_more_reads()
    {
        var calls = 0;
        ScreenPipeRead Read(Span<byte> bytes, int timeout) { calls++; throw new ScreenTransferException(ScreenTransferError.Aborted, "stop"); }
        Action drain = () => ScreenReplyFlow.Drain(Read, 64);
        drain.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.Aborted);
        calls.Should().Be(1);
    }
}
