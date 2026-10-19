using UnifiedRgb.Core.Effects;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Engine;

public readonly record struct ZoneRef(ControllerId Controller, ZoneId Zone)
{
    public override string ToString() => $"{Controller}/{Zone}";
}

/// <summary>
/// One effect bound to a zone (single target) or a group (several zones concatenated along u in list order,
/// possibly spanning controllers: fans → GPU → DRAM). <paramref name="Strategy"/> selects the content source (NATIVE design
/// §7.2); <see cref="FrameSetId"/> names the frame set of a <see cref="ContentStrategy.Frames"/> assignment (the effect is then
/// the fallback when the set is missing or does not fit the targets).
/// </summary>
public sealed record Assignment(IReadOnlyList<ZoneRef> Targets, string EffectId, EffectParams Params, string? GroupName = null,
    ContentStrategy Strategy = ContentStrategy.Vendor)
{
    /// <summary>
    /// Legacy alias (WP-11 <c>loopPolicy</c>): reads Vendor as LConnect and Program/Frames as Generic; setting it maps
    /// LConnect → Vendor and Generic → Program.
    /// </summary>
    public LoopPolicy Policy
    {
        get => Strategy == ContentStrategy.Vendor ? LoopPolicy.LConnect : LoopPolicy.Generic;
        init => Strategy = FromPolicy(value);
    }

    /// <summary>The frame set a <see cref="ContentStrategy.Frames"/> assignment plays (ignored otherwise).</summary>
    public string? FrameSetId { get; init; }

    /// <summary>
    /// SPATIAL-PATH: an optional explicit source axis (stages, parallel bindings, local reversal) replacing the list-order
    /// concatenation; null = exactly the historical layout. Deep-copied by <see cref="SpatialPath"/>; checked by
    /// <see cref="ValidatePath"/> before a scene is activated or persisted.
    /// </summary>
    public SpatialPath? Path { get; init; }

    /// <summary>
    /// STACK-RETURN: an optional hold + reverse-flow drain after the final full frame (<see cref="Engine.LoopReturn"/>); null =
    /// exactly the historical loop. Checked by <see cref="ValidateLoopReturn"/>.
    /// </summary>
    public LoopReturn? LoopReturn { get; init; }

    /// <summary><see cref="ValidatePath"/>, then <see cref="ValidateLoopReturn"/>: everything checkable without hardware.</summary>
    public void Validate()
    {
        ValidatePath();
        ValidateLoopReturn();
    }

    /// <summary>
    /// STACK-RETURN: throws <see cref="ArgumentException"/> when <see cref="LoopReturn"/> has a non-finite or out-of-range
    /// option, is set on anything but Vendor Stacking with an explicit <see cref="Path"/> (the only route that renders one shared
    /// source strip to extend), or would expand the loop beyond <see cref="Engine.LoopReturn.MaxExtendedFrames"/>.
    /// Hardware-independent, so an offline profile is accepted or rejected the same way. Null is always valid.
    /// </summary>
    public void ValidateLoopReturn()
    {
        if (LoopReturn is null)
        {
            return;
        }

        LoopReturn.Validate();
        if (Strategy != ContentStrategy.Vendor || !Effects.LianLi.LConnectLoops.IsStackingRoute(EffectId))
        {
            throw new ArgumentException($"A loop return is supported only with Vendor Stacking (not {Strategy} {EffectId}).", nameof(LoopReturn));
        }

        if (Path is null)
        {
            throw new ArgumentException("A loop return needs an explicit spatial path (it drains back along the authored path).", nameof(LoopReturn));
        }

        if (Effects.LianLi.LConnectLoops.LoopReturnRefusal(Params.Speed, Path.TotalLength, LoopReturn) is { } refusal)
        {
            throw new ArgumentException(refusal + ".", nameof(LoopReturn));
        }
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <see cref="Path"/> is malformed for <see cref="Targets"/>, or is combined with
    /// a route that cannot honour it: Frames (authored per-target content) or a Vendor per-device route (RainbowMorph,
    /// Twinkle: independent family periods, no shared layout). A Vendor shared-Stacking path also needs every stage to be a
    /// multiple of 12 source dots (one deposit per 12): hardware-independent, so a profile that cannot play is rejected when it
    /// is saved, not only when applied. The TL 12 · fans rule needs the descriptor and stays in RenderLoop. A null path is
    /// always valid.
    /// </summary>
    public void ValidatePath()
    {
        if (Path is null)
        {
            return;
        }

        if (Targets is null)
        {
            throw new ArgumentException("Assignment has no target list.", nameof(Targets));
        }

        Path.Validate(Targets);
        if (Strategy == ContentStrategy.Frames)
        {
            throw new ArgumentException("A spatial path is not supported with Frames content (authored per-target frames are not remapped).", nameof(Path));
        }

        if (Strategy == ContentStrategy.Vendor && Effects.LianLi.LConnectLoops.IsPerDeviceGroupRoute(EffectId))
        {
            throw new ArgumentException($"A spatial path is not supported with the Vendor per-device route of {EffectId} (independent family periods); use Program.", nameof(Path));
        }

        if (Strategy == ContentStrategy.Vendor && Effects.LianLi.LConnectLoops.IsStackingRoute(EffectId))
        {
            for (var i = 0; i < Path.Stages.Count; i++)
            {
                if (Path.Stages[i].Length % 12 != 0)
                {
                    throw new ArgumentException(
                        $"Spatial Stacking stage {i} is {Path.Stages[i].Length} source dots, not a multiple of 12.", nameof(Path));
                }
            }
        }
    }

    public static Assignment ForZone(ZoneRef zone, string effectId, EffectParams? p = null) =>
        new([zone], effectId, p ?? EffectParams.Default);

    public static Assignment ForGroup(string name, IReadOnlyList<ZoneRef> zones, string effectId, EffectParams? p = null) =>
        new(zones, effectId, p ?? EffectParams.Default, name);

    public bool IsGroup => GroupName is not null || Targets.Count > 1;

    /// <summary>The §7.2 mapping of a persisted <see cref="LoopPolicy"/>.</summary>
    public static ContentStrategy FromPolicy(LoopPolicy policy) => policy == LoopPolicy.Generic ? ContentStrategy.Program : ContentStrategy.Vendor;
}
