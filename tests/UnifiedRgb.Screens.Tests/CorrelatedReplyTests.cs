using System.Buffers.Binary;
using UnifiedRgb.Screens.Usb;

namespace UnifiedRgb.Screens.Tests;

public sealed class CorrelatedReplyTests
{
    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    public void Image_status_requires_matching_command_success_and_the_exact_little_endian_request_stamp(int command)
    {
        var reply = new byte[512]; reply[0] = (byte)command; reply[1] = 200;
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(2), 0xfedcba98);
        ScreenReplyFlow.MatchesImageReply(reply, (byte)command, 0xfedcba98).Should().BeTrue();
        ScreenReplyFlow.MatchesImageReply(reply, (byte)command, 0xfedcba99).Should().BeFalse();
        ScreenReplyFlow.MatchesImageReply(reply, (byte)(command + 1), 0xfedcba98).Should().BeFalse();
        reply[1] = 201;
        ScreenReplyFlow.MatchesImageReply(reply, (byte)command, 0xfedcba98).Should().BeFalse();
        ScreenReplyFlow.MatchesImageReply(reply.AsSpan(0, 5), (byte)command, 0xfedcba98).Should().BeFalse();
    }

    [Fact]
    public void Only_the_current_reply_followed_by_its_ZLP_completes_without_an_extra_empty_read()
    {
        var queue = new Queue<ScreenPipeRead>([new(0, false), new(512, false), new(0, false), new(512, false, true), new(0, false)]);
        ScreenPipeRead Read(Span<byte> _, int timeout) => queue.Dequeue();
        ScreenReplyFlow.ReadCorrelated(Read, new byte[512], 2000).Should().Be(512);
        queue.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Missing_current_status_or_terminator_is_an_error_not_a_successful_frame(int failure)
    {
        var packets = failure switch
        {
            0 => new[] { new ScreenPipeRead(0, true) },
            1 => [new(512, false), new(0, false), new(0, true)],
            2 => [new(512, false, true), new(0, true)],
            _ => Enumerable.Repeat(new ScreenPipeRead(512, false), 20).ToArray(),
        };
        var queue = new Queue<ScreenPipeRead>(packets);
        var calls = 0;
        ScreenPipeRead Read(Span<byte> _, int timeout) { calls++; return queue.Dequeue(); }
        var act = () => ScreenReplyFlow.ReadCorrelated(Read, new byte[512], 2000);
        act.Should().Throw<ScreenTransferException>().Which.Kind.Should().Be(ScreenTransferError.ResponseMismatch);
        calls.Should().BeLessThanOrEqualTo(ScreenReplyFlow.FrameDrainLimit + 1);
    }

    [Fact]
    public void A_new_device_needs_eight_consecutive_complete_conservative_transactions_before_auto_optimization()
    {
        var c = new ScreenReplyCapability();
        var full = new ScreenReadCounts(3, 1, 1, Matched: 1);
        for (var i = 0; i < 7; i++) c.Observe(true, full);
        c.Verified.Should().BeFalse();
        c.Observe(true, full with { Matched = 0 });
        c.Verified.Should().BeFalse();
        for (var i = 0; i < 8; i++) c.Observe(true, full);
        c.Verified.Should().BeTrue();
        c.Observe(false, full); c.Verified.Should().BeFalse("a bootstrap or unknown request cannot establish capability");
    }
}
