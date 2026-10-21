using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

public sealed class HidBoundedIoAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncooperative_stream_is_timed_out_and_never_reused(bool read)
    {
        using var stream = new HungStream();
        using var transport = new HidTransport(stream, OpcodeLayout.LianLiHid, TimeSpan.FromMilliseconds(50));
        var io = async () =>
        {
            if (read) _ = await transport.ReadAsync(new byte[65], default);
            else await transport.SendAsync(new byte[] { 0, 17 }, default);
        };
        await io.Should().ThrowAsync<TimeoutException>();
        stream.Closed.Should().BeTrue();
        var again = async () => await transport.SendAsync(new byte[] { 0, 17 }, default);
        await again.Should().ThrowAsync<ObjectDisposedException>();
        stream.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Forbidden_packet_is_rejected_before_even_a_hung_stream_is_touched()
    {
        using var stream = new HungStream();
        using var transport = new HidTransport(stream, OpcodeLayout.LianLiHid, TimeSpan.FromMilliseconds(50));
        var send = async () => await transport.SendAsync(new byte[] { 0, 32 }, default);
        await send.Should().ThrowAsync<ForbiddenOpcodeException>();
        stream.Calls.Should().Be(0);
    }

    private sealed class HungStream : Stream
    {
        private readonly TaskCompletionSource _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; private set; }
        public int Calls { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Calls++; return new(_write.Task); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Calls++; return new(_read.Task); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
