namespace UnifiedRgb.App.Ipc;

/// <summary>
/// Thrown by <see cref="PipeClient"/> Require* accessors after the UI entered its unsafe-fault state
/// (<see cref="PipeClient.BlockForUiFault"/>): no further Host operations may be issued from a possibly inconsistent view.
/// The Host and the pipe session themselves are left untouched.
/// </summary>
public sealed class HostAccessBlockedException : InvalidOperationException
{
    public HostAccessBlockedException()
        : base("界面处于错误状态，已停止向后台发送操作。")
    {
    }

    public HostAccessBlockedException(string message)
        : base(message)
    {
    }

    public HostAccessBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
