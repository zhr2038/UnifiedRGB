namespace UnifiedRgb.Core.Engine;

/// <summary>
/// How an assignment's UploadedLoop zones are compiled (WP-11). Superseded by <see cref="ContentStrategy"/> (NATIVE design §7.2) and
/// kept as the legacy alias of <see cref="Assignment.Policy"/>: <see cref="LConnect"/> ↔ <see cref="ContentStrategy.Vendor"/>,
/// <see cref="Generic"/> ↔ <see cref="ContentStrategy.Program"/>. Older profiles (<c>loopPolicy</c>) load through that mapping.
/// </summary>
public enum LoopPolicy
{
    /// <summary>
    /// Legacy token for the procedural path-loop planner (<see cref="Effects.LianLi.LConnectLoops"/>); otherwise the
    /// generic path below.
    /// </summary>
    LConnect = 0,

    /// <summary>The generic generator sampled over its natural period (<see cref="LoopCompiler"/>, ARCHITECTURE-D2 §4).</summary>
    Generic = 1,
}
