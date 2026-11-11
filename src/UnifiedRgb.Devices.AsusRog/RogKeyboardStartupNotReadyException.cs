namespace UnifiedRgb.Devices.AsusRog;

/// <summary>A new RF owner's initial read-only snapshot is incomplete; no settings or native ARM have been admitted.</summary>
public sealed class RogKeyboardStartupNotReadyException : IOException
{
    internal RogKeyboardStartupNotReadyException(string operation, Exception inner)
        : base("Initial RF read-only snapshot not ready; operation=" + operation + "; no setting was written. " + inner.Message, inner)
    { Operation = operation; }
    public string Operation { get; }
}
