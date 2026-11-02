using System.Net;
using System.Net.Sockets;
using UnifiedRgb.Transports.OpenRgb;

namespace UnifiedRgb.Devices.Tests;

public sealed class OpenRgbTcpLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_server_that_stops_receiving_cannot_hold_a_sender_or_leave_a_partial_stream_reusable(bool cancel)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { SendBufferSize = 0, NoDelay = true };
        var accepting = listener.AcceptSocketAsync();
        await sender.ConnectAsync(listener.LocalEndpoint);
        using var receiver = await accepting;
        receiver.ReceiveBufferSize = 4096;
        using var transport = new TcpSdkTransport(sender, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100));
        using var stop = new CancellationTokenSource();
        var sending = transport.SendAsync(new byte[4 * 1024 * 1024], stop.Token).AsTask();
        if (cancel)
        {
            await Task.Delay(25);
            sending.IsCompleted.Should().BeFalse("the loopback peer does not read and both socket buffers are small");
            stop.Cancel();
        }
        var bounded = async () => await sending.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel) await bounded.Should().ThrowAsync<OperationCanceledException>();
        else await bounded.Should().ThrowAsync<TimeoutException>().WithMessage("*packet send deadline*");
        var again = async () => await transport.SendAsync(new byte[] { 1 }, default);
        await again.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_pre_cancelled_send_writes_nothing_and_leaves_the_connection_usable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptSocketAsync();
        using var transport = await TcpSdkTransport.ConnectAsync((IPEndPoint)listener.LocalEndpoint, default);
        using var receiver = await accepting;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = async () => await transport.SendAsync(new byte[] { 0xFF }, stop.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await transport.SendAsync(new byte[] { 42 }, default);
        var buffer = new byte[8];
        (await receiver.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(3))).Should().Be(1);
        buffer[0].Should().Be(42);
    }
}
