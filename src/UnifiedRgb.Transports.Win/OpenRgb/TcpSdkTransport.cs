using System.Net;
using System.Net.Sockets;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.OpenRgb;

/// <summary>
/// TCP byte stream to the supervised headless OpenRGB. Loopback only: any non-loopback endpoint is refused before a
/// socket is created (ARCHITECTURE §8: no listeners or peers except loopback OpenRGB SDK). Sends are synchronous and
/// serialised, so several DeviceWriter threads may share one connection.
/// </summary>
public sealed class TcpSdkTransport : ITransport, IDisposable
{
    public const int DefaultPort = 16744;

    private readonly Socket _socket;
    private readonly object _sendGate = new();

    private TcpSdkTransport(Socket socket) => _socket = socket;

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

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sendGate)
        {
            var span = frame.Span;
            while (!span.IsEmpty)
            {
                var n = _socket.Send(span, SocketFlags.None);
                span = span[n..];
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _socket.ReceiveAsync(buffer, SocketFlags.None, ct);

    public void Dispose() => _socket.Dispose();
}
