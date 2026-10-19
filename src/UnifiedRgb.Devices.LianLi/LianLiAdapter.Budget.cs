using UnifiedRgb.Devices.LianLi.Protocol;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>
/// OPUS-GENERIC-SCENE-PACKAGES: the strict-content budget seam. <see cref="MeasureLoop"/> runs exactly the upload's transform
/// (wire order, then the current mounting map) and the loaded CPU codec, without the upload gate, a sender or any device I/O,
/// so strict content is refused BEFORE a scene is committed instead of being decimated by <see cref="Compress"/>.
/// </summary>
public sealed partial class LianLiAdapter : ILoopBudgetProbe
{
    public LoopBudget MeasureLoop(ControllerId id, ZoneId zone, LoopFrames loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        const int max = LianLiWire.MaxCompressedAnimation;
        Session s;
        try
        {
            s = Get(id);
        }
        catch (InvalidOperationException ex)
        {
            return new LoopBudget(0, max, ex.Message);
        }

        if (zone != ChainZone)
        {
            return new LoopBudget(0, max, $"{id} has one zone ({ChainZone}), not {zone}.");
        }

        if (loop.LedCount != s.Device.LedCount)
        {
            return new LoopBudget(0, max, $"{id} has {s.Device.LedCount} LEDs; the loop has {loop.LedCount}.");
        }

        if (loop.LedCount > byte.MaxValue || loop.Frames > ushort.MaxValue || !(loop.IntervalMs > 0) || loop.IntervalMs >= ushort.MaxValue + 1
            || Math.Abs((loop.IntervalMs * 100.0) - Math.Round(loop.IntervalMs * 100.0)) > 1e-6)
        {
            return new LoopBudget(0, max, $"{id}: {loop.Frames} frames × {loop.LedCount} LEDs at interval {loop.IntervalMs} do not fit the upload header exactly.");
        }

        if (_encoder is not { } encoder)
        {
            return new LoopBudget(0, max, "No codec loaded.");
        }

        var rgb = WireRgb(s, id, loop);
        var packed = encoder.Encode(rgb.Span);
        return packed.Length <= max
            ? new LoopBudget(packed.Length, max, null)
            : new LoopBudget(packed.Length, max, $"{id}: {loop.Frames} frames encode to {packed.Length} bytes (> {max}); strict content is never decimated.");
    }

    /// <summary>The loop's bytes in the device's wire order with the current mounting map, as <see cref="UploadLoopAsync"/> sends them.</summary>
    private ReadOnlyMemory<byte> WireRgb(Session s, ControllerId id, LoopFrames loop)
    {
        var mounting = MountingOf(id);
        return s.Device.Assembler.ReordersWire || mounting.Map is not null
            ? MountingMap.Apply(s.Device.Assembler.ToWireOrder(loop.Rgb, loop.LedCount), loop.LedCount, mounting.Map)
            : loop.Rgb;
    }
}
