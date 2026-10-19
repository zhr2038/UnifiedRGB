using UnifiedRgb.Devices;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Safety.Tests;

public class FrameValidatorTests
{
    private static readonly FrameValidationRules Rules = new()
    {
        MinLength = 4,
        MaxLength = 64,
        ChannelOffset = 2,
        MaxChannel = 7,
    };

    [Fact]
    public async Task Valid_frame_passes()
    {
        var sink = new RecordingTransport();
        var v = new FrameValidator(sink, Rules, new DeviceState());
        await v.SendAsync(new byte[] { 16, 32, 3, 0 }, CancellationToken.None);
        sink.SentFrames.Should().ContainSingle();
    }

    [Theory]
    [InlineData(3, FrameRejectReason.TooShort)]
    [InlineData(65, FrameRejectReason.TooLong)]
    public async Task Wrong_length_is_rejected(int len, FrameRejectReason reason)
    {
        var sink = new RecordingTransport();
        var v = new FrameValidator(sink, Rules, new DeviceState());
        var act = async () => await v.SendAsync(new byte[len], CancellationToken.None);
        (await act.Should().ThrowAsync<FrameRejectedException>()).Which.Reason.Should().Be(reason);
        sink.SentFrames.Should().BeEmpty();
    }

    [Fact]
    public void Length_not_in_allowed_set_is_rejected()
    {
        var rules = Rules with { AllowedLengths = new HashSet<int> { 65 }, MaxLength = 128 };
        FrameValidator.Validate(new byte[64], rules, new DeviceState()).Should().Be(FrameRejectReason.LengthNotAllowed);
        FrameValidator.Validate(new byte[65], rules, new DeviceState()).Should().Be(FrameRejectReason.None);
    }

    [Fact]
    public async Task Out_of_range_channel_is_rejected()
    {
        var sink = new RecordingTransport();
        var v = new FrameValidator(sink, Rules, new DeviceState());
        var act = async () => await v.SendAsync(new byte[] { 16, 32, 8, 0 }, CancellationToken.None);
        (await act.Should().ThrowAsync<FrameRejectedException>()).Which.Reason.Should().Be(FrameRejectReason.ChannelOutOfRange);
        sink.SentFrames.Should().BeEmpty();
    }

    [Fact]
    public async Task Offline_device_is_rejected()
    {
        var sink = new RecordingTransport();
        var state = new DeviceState(online: false);
        var v = new FrameValidator(sink, Rules, state);
        var act = async () => await v.SendAsync(new byte[] { 16, 32, 0, 0 }, CancellationToken.None);
        (await act.Should().ThrowAsync<FrameRejectedException>()).Which.Reason.Should().Be(FrameRejectReason.DeviceOffline);
        state.IsOnline = true;
        await v.SendAsync(new byte[] { 16, 32, 0, 0 }, CancellationToken.None);
        sink.SentFrames.Should().ContainSingle();
    }
}
