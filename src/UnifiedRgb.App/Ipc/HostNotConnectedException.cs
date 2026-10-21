namespace UnifiedRgb.App.Ipc;

/// <summary>
/// Thrown by the <c>PipeClient.Require*</c> accessors while no Host session exists. It stays an
/// <see cref="InvalidOperationException"/> (the documented contract of <see cref="IHostConnection.Require"/>), but is a
/// distinct type so the UI exception policy can recognise this one expected, recoverable condition without treating every
/// <see cref="InvalidOperationException"/> as harmless.
/// </summary>
public sealed class HostNotConnectedException : InvalidOperationException
{
    public const string DefaultMessage = "后台未连接。";

    public HostNotConnectedException()
        : base(DefaultMessage)
    {
    }

    public HostNotConnectedException(string message)
        : base(message)
    {
    }

    public HostNotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
