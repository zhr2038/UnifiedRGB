using System.Windows;
using System.Windows.Media;
using UnifiedRgb.App.Branding;
using UnifiedRgb.App.ViewModels;

namespace UnifiedRgb.Host.Tests;

public sealed class BrandAssetsTests
{
    [Theory]
    [InlineData(TrayState.Disconnected)]
    [InlineData(TrayState.Ok)]
    [InlineData(TrayState.Attention)]
    public void Embedded_tray_icons_are_owned_and_render_after_the_resource_stream_is_closed(TrayState state)
    {
        using var icon = BrandAssets.LoadTrayIcon(state);
        using var bitmap = icon.ToBitmap();
        bitmap.Width.Should().BeGreaterThanOrEqualTo(16);
        var visible = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).A > 0) visible++;
        visible.Should().BeGreaterThan(bitmap.Width * bitmap.Height / 3);
    }

    [Fact]
    public void The_theme_contains_both_vector_brand_resources_without_changing_other_pages()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var theme = (ResourceDictionary)Application.LoadComponent(new Uri("/UnifiedRgb.App;component/Themes/StudioTheme.xaml", UriKind.Relative));
                foreach (var key in new[] { "Brand.Mark", "Brand.Logo" })
                {
                    var image = theme[key].Should().BeOfType<DrawingImage>().Subject;
                    image.Drawing.Bounds.Width.Should().Be(256); image.Drawing.Bounds.Height.Should().Be(256);
                }
                theme.Contains("AccentBrush").Should().BeTrue(); theme.Contains("QuietButton").Should().BeTrue();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (failure is not null) throw new InvalidOperationException("Brand resource loading failed", failure);
    }
}
