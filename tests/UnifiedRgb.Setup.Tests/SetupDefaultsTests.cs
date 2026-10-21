using System.Text;

namespace UnifiedRgb.Setup.Tests;

public sealed class SetupDefaultsTests
{
    [Fact]
    public void Existing_hardware_choices_and_screen_output_are_not_reset_by_opening_the_wizard()
    {
        var json = """{"UnifiedRgb":{"UseVirtualAdapter":false,"EnableLianLi":true,"LianLi":{"WireFormatVerified":true,"Devices":[{"Mac":"01:02:03:04:05:06"}]},"Screens":{"Enabled":true,"H2":{"Output":true},"Turzx":{"Output":false}}}}""";
        var bytes=Encoding.UTF8.GetBytes(json);
        var snapshot=new SetupConfigSnapshot(@"C:\Program Files\UnifiedRgb\appsettings.json",true,SetupConfigDocument.Sha256(bytes),bytes,null);
        var selection=SetupDefaults.From(snapshot);
        selection.Mode.Should().Be(SetupMode.Hardware);
        selection.Families.Single(c=>c.Family==SetupFamily.LianLi).Should().Be(new SetupFamilySelection(SetupFamily.LianLi,true,true));
        selection.Families.Single(c=>c.Family==SetupFamily.ScreenH2).Should().Be(new SetupFamilySelection(SetupFamily.ScreenH2,true,true));
        selection.Families.Single(c=>c.Family==SetupFamily.ScreenTurzx).Enable.Should().BeFalse();
        selection.AcceptedLianLiMacs.Should().BeNull("opening setup must not silently replace existing/offline device bindings");
        SetupDefaults.HasConfiguredLianLiDevices(snapshot).Should().BeTrue();
        snapshot.Content.Should().Equal(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void A_new_installation_defaults_to_demo_without_raising_any_output_gate()
    {
        var snapshot=new SetupConfigSnapshot(@"C:\Program Files\UnifiedRgb\appsettings.json",false,"ABSENT",null,null);
        var selection=SetupDefaults.From(snapshot);
        selection.Mode.Should().Be(SetupMode.Demo);
        selection.Families.Should().BeEmpty();
        SetupDefaults.HasConfiguredLianLiDevices(snapshot).Should().BeFalse();
    }
}
