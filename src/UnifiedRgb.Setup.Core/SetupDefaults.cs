using System.Text.Json.Nodes;

namespace UnifiedRgb.Setup;

/// <summary>Initial wizard choices follow the existing file; merely opening setup is never a reset.</summary>
public static class SetupDefaults
{
    public static SetupSelection From(SetupConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = snapshot.Content is null || snapshot.Error is not null ? null : SetupConfigDocument.Parse(snapshot.Content);
        var app = Section(root, "UnifiedRgb");
        if (!Bool(app, "UseVirtualAdapter", fallback: true))
        {
            var screens = Section(app, "Screens");
            return new(SetupMode.Hardware,
            [
                Choice(SetupFamily.LianLi,"EnableLianLi", Bool(Section(app,"LianLi"),"WireFormatVerified")),
                Choice(SetupFamily.MsiGpu,"EnableMsiGpu", Bool(Section(app,"MsiGpu"),"ControlVerified") && Bool(Section(app,"MsiGpu"),"RegisterMapVerified")),
                Choice(SetupFamily.AsusAura,"EnableAsusAura", Bool(Section(app,"AsusAura"),"ControlVerified")),
                Choice(SetupFamily.EneDram,"EnableEneDram", Bool(Section(app,"EneDram"),"ControlVerified") && Bool(Section(app,"EneDram"),"PawnIoModuleVerified")),
                Screen(SetupFamily.ScreenTlLcd,"TlLcd"), Screen(SetupFamily.ScreenH2,"H2"), Screen(SetupFamily.ScreenTurzx,"Turzx"),
            ]);
            SetupFamilySelection Choice(SetupFamily family,string key,bool authorized) => new(family,Bool(app,key),Bool(app,key) && authorized);
            SetupFamilySelection Screen(SetupFamily family,string key)
            {
                var enabled = Bool(screens,"Enabled") && Bool(Section(screens,key),"Output");
                return new(family,enabled,enabled);
            }
        }
        return new(SetupMode.Demo, []);
    }

    public static bool HasConfiguredLianLiDevices(SetupConfigSnapshot snapshot) =>
        snapshot.Error is null && snapshot.Content is not null && Section(Section(SetupConfigDocument.Parse(snapshot.Content),"UnifiedRgb"),"LianLi") is {} section
        && SetupConfigDocument.Member(section,"Devices")?.Value is JsonArray { Count: > 0 };

    private static JsonObject? Section(JsonObject? parent,string key) => parent is null ? null : SetupConfigDocument.Member(parent,key)?.Value as JsonObject;
    private static bool Bool(JsonObject? parent,string key,bool fallback=false) => parent is not null && SetupConfigDocument.Member(parent,key)?.Value is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
}
