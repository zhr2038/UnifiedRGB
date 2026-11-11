using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Services;

/// <summary>Actor-only physical-page history, independent of ARM, artwork acknowledgements and connection recovery.
/// Firmware owns the dedicated entry assets; Host uploads nothing during their entry hold.</summary>
internal sealed class CodexEntryHold(TimeProvider time)
{
    private byte? _page;
    private long _enteredAt;

    public void Observe(byte page)
    {
        if (_page == page) return;
        _page = page;
        if (page is RogNativeMenuCodec.CodexMode or RogNativeMenuCodec.HomeAssistantMode) _enteredAt = time.GetTimestamp();
    }

    public bool Holding => _page is RogNativeMenuCodec.CodexMode or RogNativeMenuCodec.HomeAssistantMode
        && time.GetElapsedTime(_enteredAt, time.GetTimestamp()) < TimeSpan.FromMilliseconds(1200);
}
