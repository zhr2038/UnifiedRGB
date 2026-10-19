namespace UnifiedRgb.Core.Engine;

/// <summary>
/// How an assignment's UploadedLoop zones are compiled (WP-11). Superseded by <see cref="ContentStrategy"/> (NATIVE design §7.2) and
/// kept as the legacy alias of <see cref="Assignment.Policy"/>: <see cref="LConnect"/> ↔ <see cref="ContentStrategy.Vendor"/>,
/// <see cref="Generic"/> ↔ <see cref="ContentStrategy.Program"/>. Older profiles (<c>loopPolicy</c>) load through that mapping.
/// </summary>
public enum LoopPolicy
{
    /// <summary>
    /// Default. When every target is a Lian Li TL/H2 uploaded-loop zone and the effect has an IL-pinned L-Connect body, the
    /// loop is L-Connect's own frames, frame count and interval (<see cref="Effects.LianLi.LConnectLoops"/>); otherwise the
    /// generic path below.
    /// </summary>
    LConnect = 0,

    /// <summary>The generic generator sampled over its natural period (<see cref="LoopCompiler"/>, ARCHITECTURE-D2 §4).</summary>
    Generic = 1,
}
