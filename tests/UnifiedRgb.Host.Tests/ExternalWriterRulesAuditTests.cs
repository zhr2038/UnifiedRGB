using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

public sealed class ExternalWriterRulesAuditTests
{
    [Fact]
    public void A_local_process_rule_blocks_only_selected_hardware_and_cannot_grant_scm_rights()
    {
        var rules = KnownWriters.WithExternalProcesses([new() { Name = "CustomLightingBridge.exe", Affects = [DeviceClass.AsusAura] }]);
        rules.Should().Contain(KnownWriters.All);
        var scanner = new ProcessScanner(new Snapshot(), rules);
        scanner.ScanFor(DeviceClass.AsusAura).Should().ContainSingle().Which.Writer.Kind.Should().Be(WriterKind.Process);
        scanner.ScanFor(DeviceClass.EneDram).Should().BeEmpty();
        rules[^1].ServiceName.Should().BeNull();
    }

    [Theory]
    [InlineData("../OtherProgram")]
    [InlineData("C:\\Program Files\\Anything.exe")]
    [InlineData("Process --argument")]
    [InlineData("..")]
    public void Configured_rules_reject_paths_or_command_lines(string name)
    {
        var rule = () => KnownWriters.WithExternalProcesses([new() { Name = name, Affects = [DeviceClass.AsusAura] }]);
        rule.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_legacy_controller_payload_does_not_claim_verification_when_the_field_is_absent()
    {
        const string payload = """{"id":"legacy","vendor":"test","name":"older host","deviceClass":"AsusAura","deviceType":"Motherboard","serial":"","zones":[],"maxFps":20,"state":"Blocked","blockedBy":null}""";
        JsonSerializer.Deserialize<ControllerDto>(payload, ContractsJson.Configure(new JsonSerializerOptions()))!.ControlVerified.Should().BeFalse();
    }

    private sealed class Snapshot : ISystemSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes() => [new(123, "CustomLightingBridge")];
        public IReadOnlyList<ServiceInfo> Services() => [new("CustomLightingBridge", true, "Automatic")];
    }
}
