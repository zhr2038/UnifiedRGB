namespace UnifiedRgb.Host.Services;

/// <summary>Actor-only physical-page history, independent of ARM, artwork acknowledgements and connection recovery.
/// Firmware owns the official CODEX entry asset; Host uploads nothing during its entry hold.</summary>
internal sealed class CodexEntryHold(TimeProvider time)
{
    private byte? _page;
    private long _enteredAt;

    public void Observe(byte page)
    {
        if (_page == page) return;
        _page = page;
        if (page == 10) _enteredAt = time.GetTimestamp();
    }

    public bool Holding => _page == 10 && time.GetElapsedTime(_enteredAt, time.GetTimestamp()) < TimeSpan.FromMilliseconds(1200);
}
