using UnifiedRgb.Contracts;

namespace UnifiedRgb.App.Ipc;

/// <summary>
/// The part of <see cref="PipeClient"/> a connection-owned feature needs (NATIVE design §6: a bring-up session belongs to the
/// pipe connection that began it). <see cref="Connections"/> changes on every reconnect, so a caller can tell that a session
/// it began on an earlier connection is gone (the Host ended it when that connection closed).
/// </summary>
public interface IHostConnection
{
    event EventHandler<ConnectionState>? StateChanged;

    ConnectionState State { get; }

    /// <summary>Number of successful connections so far.</summary>
    int Connections { get; }

    /// <summary>The proxy, or a typed InvalidOperationException when disconnected or blocked after an unsafe UI fault.</summary>
    IRgbService Require();
}
