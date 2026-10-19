using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public class RfDeliveryStatusTests
{
    [Theory]
    [InlineData("HostWritten", 0)]
    [InlineData("ReportedMatch", 1)]
    [InlineData("ReportedMismatch", 0)]
    [InlineData("Unknown", 2)]
    public void Receiver_snapshot_crosses_the_real_status_mapper_and_json_without_changing_original_upload(string state, int retries)
    {
        var at = DateTimeOffset.UtcNow;
        var stats = new LoopUploadStats(at, 12345, 100, 200, 20, 1000, 100, 2, TimeSpan.FromMilliseconds(75), 0, "codec")
        { ReceiverDelivery = new(state, 100, 99, 2600, at.AddSeconds(5), retries, "generation evidence only") };
        var dto = LightingEngine.ToDto(stats)!;
        var json = JsonSerializer.Serialize(dto, ContractsJsonContext.Default.LoopUploadDto);
        var read = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.LoopUploadDto)!;
        read.ReceiverDelivery.Should().Be(new RfDeliveryDto(state, 100, 99, 2600, at.AddSeconds(5), retries, "generation evidence only"));
        (read.At, read.EffectIndex, read.Frames, read.DurationMs).Should().Be((at, 100u, 200, 75d));
        stats.StartedTimestamp.Should().Be(12345);
    }

    [Fact]
    public void Legacy_upload_status_without_feedback_still_deserializes()
    {
        const string old = "{\"at\":\"2026-10-18T00:00:00Z\",\"effectIndex\":100,\"frames\":1,\"intervalMs\":20,\"rawBytes\":72,\"compressedBytes\":72,\"packets\":2,\"durationMs\":75,\"decimations\":0,\"codecSha256\":\"codec\"}";
        var read = JsonSerializer.Deserialize(old, ContractsJsonContext.Default.LoopUploadDto)!;
        read.ReceiverDelivery.Should().BeNull();
        read.EffectIndex.Should().Be(100);
    }
}
