namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// The first, read-only static-lighting snapshot of an exactly paired mouse timed out. Its receiver never opened a writer,
/// all old channels have closed and joined, and only discovery plus a fresh snapshot on a new owner may be attempted.
/// This signal is never emitted for a mutation, malformed data, a caller cancellation or an already opened receiver.
/// </summary>
public sealed class RogMouseStartupNotReadyException : IOException
{
    internal RogMouseStartupNotReadyException(string controllerId, string receiverKey, byte reportId, TimeoutException inner)
        : base("The pinned mouse's initial static-lighting snapshot is not ready; the read-only owner has closed.", inner)
    {
        ControllerId = controllerId;
        ReceiverKey = receiverKey;
        ReportId = reportId;
    }

    public string ControllerId { get; }
    public string ReceiverKey { get; }
    public byte ReportId { get; }
}
