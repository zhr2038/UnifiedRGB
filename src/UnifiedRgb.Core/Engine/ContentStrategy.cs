namespace UnifiedRgb.Core.Engine;

/// <summary>
/// Where an assignment's content comes from (NATIVE design §7.2), replacing the binary <see cref="LoopPolicy"/>. All three share
/// one period, the anchor T0 and PhaseTrimMs, the zone order and placement, and one Diagnostics variant label
/// (<c>Vendor:&lt;route&gt;</c>, <c>Program:&lt;generatorId&gt;</c>, <c>Frames:&lt;assetId&gt;</c>).
/// </summary>
public enum ContentStrategy
{
    /// <summary>
    /// The L-Connect body of the selected effect (former <see cref="LoopPolicy.LConnect"/>): uploaded-loop zones get the vendor
    /// loop as before; streamed zones join a shared route as plain linear dots of the same plan and are played by the streamed
    /// loop player; per-device routes give streamed zones the Program equivalent live (<c>+program(N)</c>, §7.3).
    /// </summary>
    Vendor = 0,

    /// <summary>The generator itself (former <see cref="LoopPolicy.Generic"/>): one LoopSeconds period uploaded, live elsewhere.</summary>
    Program = 1,

    /// <summary>An authored frame set (<see cref="FrameSet"/>) uploaded to loop zones and played on streamed zones.</summary>
    Frames = 2,
}
