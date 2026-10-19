using UnifiedRgb.Devices;

namespace UnifiedRgb.Core.Effects;

/// <summary>
/// Family-agnostic effect generator. Implementations are pure functions of (ctx.T, ctx.Seed, samples, params):
/// stateless, allocation-free and deterministic, which makes golden-frame tests exact.
/// </summary>
public interface IEffectGenerator
{
    string Id { get; }

    EffectParamSchema Schema { get; }

    void Render(in RenderContext ctx, Span<Rgb> output);
}

/// <summary>Base class supplying Id/Schema boilerplate.</summary>
public abstract class EffectGenerator : IEffectGenerator
{
    protected EffectGenerator(string id, EffectParamSchema schema)
    {
        Id = id;
        Schema = schema;
    }

    public string Id { get; }

    public EffectParamSchema Schema { get; }

    public abstract void Render(in RenderContext ctx, Span<Rgb> output);
}
