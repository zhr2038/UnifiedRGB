using System.Reflection;
using System.Text.RegularExpressions;
using UnifiedRgb.Transports.Nvapi;

namespace UnifiedRgb.Safety.Tests;

/// <summary>ARCHITECTURE §8: no clock, voltage, power or fan NVAPI entry point is bound.</summary>
public partial class NvapiBindingTests
{
    /// <summary>Well-known NVAPI ids that change clocks, voltage or fans (NvAPI_GPU_SetPstates20, NvAPI_GPU_SetCoolerLevels, NvAPI_GPU_GetPstates20).</summary>
    private static readonly uint[] ForbiddenIds = [0x0F4DAE6B, 0x891FA0AE, 0x6FF81213];

    [Fact]
    public void Interface_id_table_is_exactly_the_allow_list()
    {
        NvapiInterfaceIds.All.Should().BeEquivalentTo(new uint[]
        {
            NvapiInterfaceIds.Initialize, NvapiInterfaceIds.EnumPhysicalGpus, NvapiInterfaceIds.GetPciIdentifiers,
            NvapiInterfaceIds.I2cWriteExtended, NvapiInterfaceIds.I2cReadExtended, NvapiInterfaceIds.GetThermalSettings,
        });
        NvapiInterfaceIds.All.Should().OnlyHaveUniqueItems();
        typeof(NvapiInterfaceIds).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)
            .Should().HaveCount(NvapiInterfaceIds.All.Count, "every bound id must be listed in All");
    }

    [Fact]
    public void No_forbidden_id_value_appears_as_a_constant_in_the_transport_assembly()
    {
        var constants = typeof(NvapiNative).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(uint))
            .Select(f => (uint)f.GetRawConstantValue()!)
            .ToArray();
        constants.Should().NotIntersectWith(ForbiddenIds);
    }

    [Fact]
    public void Nvapi_surface_has_no_clock_voltage_power_or_fan_members()
    {
        var types = new[] { typeof(INvapi), typeof(NvapiNative), typeof(NvapiInterfaceIds) };
        var names = types.SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .ToArray();
        names.Should().NotBeEmpty();
        names.Where(n => Forbidden().IsMatch(n)).Should().BeEmpty();
    }

    [GeneratedRegex("clock|volt|fan|cooler|pstate|power|overclock", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
