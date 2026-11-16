using System.Windows.Media;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Services;

/// <summary>Off-clock208x64 artwork; the compiled project logo and independently packed pixels match the accepted trial.</summary>
internal static class KeyboardStatusArtwork
{
    private static readonly Lazy<KeyboardArtworkRenderer> Shared = new(() => new KeyboardArtworkRenderer());

    static KeyboardStatusArtwork() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        if (Shared.IsValueCreated) Shared.Value.Dispose();
    };

    public static byte[] Render(CaseStatusObservation state) => Shared.Value.Render(state);

    /// <summary>The Guangyu entry logo: the same compiled mark, centred with the product name and no status text.</summary>
    public static byte[] RenderLogo() => Shared.Value.RenderLogo();

    internal static void Text(DrawingContext dc, string value, double size, double x, double y, double width) =>
        KeyboardArtworkRenderer.Text(dc, value, size, x, y, width);

    internal static byte[] Rasterize(Action<DrawingContext, Drawing> draw) => Shared.Value.Rasterize(draw);

    internal static byte[] PackBgra(ReadOnlySpan<byte> bgra) => KeyboardArtworkRenderer.PackBgra(bgra);
}
