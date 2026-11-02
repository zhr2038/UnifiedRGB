using System.Net;
using System.Net.Sockets;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.OpenRgb;

/// <summary>
/// TCP byte stream to the supervised headless OpenRGB. Loopback only: any non-loopback endpoint is refused before a
/// socket is created (ARCHITECTURE §8: no listeners or peers except loopback OpenRGB SDK). Complete packets are serialised,
/// cancellable and bounded, so several DeviceWriter threads may share one connection without a stalled server hanging them.
/// </summary>
public sealed class TcpSdkTransport : ITransport, IDisposable
{
    public const int DefaultPort = 16744;

    private readonly Socket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TimeSpan _sendTimeout;

    internal TcpSdkTransport(Socket socket, TimeSpan? sendTimeout = null)
    {
        _socket = socket;
        _sendTimeout = sendTimeout ?? TimeSpan.FromSeconds(1);
        if (_sendTimeout <= TimeSpan.Zero || _sendTimeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(sendTimeout));
    }

    public static async Task<TcpSdkTransport> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IPAddress.IsLoopback(endpoint.Address))
        {
            throw new ArgumentException($"OpenRGB SDK endpoint {endpoint} is not loopback.", nameof(endpoint));
        }

        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new TcpSdkTransport(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_sendTimeout);
            try
            {
                while (!frame.IsEmpty)
                {
                    var n = await _socket.SendAsync(frame, SocketFlags.None, deadline.Token).ConfigureAwait(false);
                    if (n <= 0) throw new EndOfStreamException("OpenRGB SDK closed while sending a packet.");
                    frame = frame[n..];
                }
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _socket.Dispose();
                throw new TimeoutException("OpenRGB SDK packet send deadline elapsed; the partial stream is retired.", ex);
            }
            catch
            {
                // A cancelled/failed send may already have written a packet prefix. Never append another packet to it.
                _socket.Dispose();
                throw;
            }
        }
        finally { _sendGate.Release(); }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _socket.ReceiveAsync(buffer, SocketFlags.None, ct);

    public void Dispose() => _socket.Dispose(); // SemaphoreSlim uses no wait handle and may still have active send waiters.
}
