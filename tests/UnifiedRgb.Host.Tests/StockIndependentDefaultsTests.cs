using Microsoft.Extensions.Configuration;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// The shipped source settings keep every stock-independent keyboard path off: no ROG adapter or keyboard binding, no keyboard
/// OLED display or animation admission, and no installed native-menu ownership (so no future-native coexistence is assumed).
/// Root alone turns these on after physical acceptance. This reads the file only; it binds no Host and opens no device.
/// </summary>
public sealed class StockIndependentDefaultsTests
{
    [Fact]
    public void Shipped_settings_keep_rog_keyboard_oled_animation_and_native_menu_ownership_off()
    {
        var path = Path.Combine(PreviewTests.RepoRoot(), "src", "UnifiedRgb.Host", "appsettings.json");
        var o = new UnifiedRgbOptions();
        new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(UnifiedRgbOptions.Section).Bind(o);

        o.EnableAsusRog.Should().BeFalse();
        o.AsusRog.Keyboards.Should().BeEmpty();
        o.KeyboardDisplay.Should().BeEquivalentTo(new KeyboardDisplayOptions(), "the shipped file has no keyboard display section");
        (o.KeyboardDisplay.Enabled, o.KeyboardDisplay.AnimationAdmitted).Should().Be((false, false));
        o.KeyboardDisplay.AnimationAdmissionProblem.Should().Be("not admitted");
        (o.KeyboardDisplay.HasVerifiedWiredIdentity, o.KeyboardDisplay.HasVerifiedWirelessIdentity, o.KeyboardDisplay.HasVerifiedLightingRoute)
            .Should().Be((false, false, false));
        o.KeyboardNativeMenu.Should().BeEquivalentTo(new KeyboardNativeMenuOptions(), "the shipped file has no native menu section");
        o.KeyboardNativeMenu.Admitted.Should().BeFalse("native ownership, and so any native-presentation coexistence, stays denied");
    }
}
