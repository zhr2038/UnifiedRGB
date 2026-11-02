using UnifiedRgb.Transports.OpenRgb;

namespace UnifiedRgb.Devices.Tests;

public sealed class OpenRgbClientLifetimeTests
{
    [Fact]
    public async Task Cancellation_after_a_partial_header_retires_the_stream_before_another_request()
    {
        using var transport = new PausedReply(partialHeader: true);
        using var client = new OpenRgbSdkClient(transport);
        using var stop = new CancellationTokenSource();
        var first = client.GetControllerCountAsync(stop.Token);
        await transport.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var cancelled = async () => await first;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        transport.Disposed.Should().BeTrue();
        var again = () => client.GetControllerCountAsync(default);
        await again.Should().ThrowAsync<ObjectDisposedException>();
        transport.Sends.Should().Be(1, "a partial TCP packet cannot be associated with a new request");
    }

    [Fact]
    public async Task Cancelling_a_queued_request_preserves_the_active_request_and_connection()
    {
        using var transport = new PausedReply();
        using var client = new OpenRgbSdkClient(transport);
        var first = client.GetControllerCountAsync(default);
        await transport.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var waiting = client.GetControllerCountAsync(stop.Token);
        stop.Cancel();
        var cancelled = async () => await waiting;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        transport.Disposed.Should().BeFalse();
        transport.Sends.Should().Be(1);
        transport.Continue.TrySetResult();
        (await first).Should().Be(2);
        (await client.GetControllerCountAsync(default)).Should().Be(2);
    }

    [Fact]
    public async Task Disposal_during_a_request_preserves_the_transport_failure_and_releases_resources_once()
    {
        using var transport = new PausedReply();
        using var client = new OpenRgbSdkClient(transport);
        var first = client.GetControllerCountAsync(default);
        await transport.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose();
        var closed = async () => await first;
        await closed.Should().ThrowAsync<IOException>().WithMessage("raw connection closed");
        client.Dispose();
        transport.Disposals.Should().Be(1);
    }

    private sealed class PausedReply(bool partialHeader = false) : ITransport, IDisposable
    {
        private readonly byte[] _reply = OrgbSpec.Packet(0, 0, [2, 0, 0, 0]);
        private int _position;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sends { get; private set; }
        public int Disposals { get; private set; }
        public bool Disposed => Disposals != 0;
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ct.ThrowIfCancellationRequested();
            Sends++;
            _position = 0;
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!partialHeader || _position != 0)
            {
                ReadEntered.TrySetResult();
                await Continue.Task.WaitAsync(ct);
            }
            var count = Math.Min(buffer.Length, partialHeader && _position == 0 ? 5 : _reply.Length - _position);
            _reply.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public void Dispose()
        {
            Disposals++;
            Continue.TrySetException(new IOException("raw connection closed"));
        }
    }
}
