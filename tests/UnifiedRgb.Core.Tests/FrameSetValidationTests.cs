using UnifiedRgb.Core.Engine;

namespace UnifiedRgb.Core.Tests;

public sealed class FrameSetValidationTests
{
    [Theory]
    [InlineData(1_073_741_824, 4)]
    [InlineData(65_536, 65_536)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Wrapped_frame_dimensions_cannot_admit_an_empty_buffer(int frames, int leds)
    {
        var set = new FrameSet("malformed", frames, 20, [leds], [Array.Empty<byte>()]);

        set.Validate().Should().NotBeNull();
        FluentActions.Invoking(() => new InMemoryFrameSetSource().Add(set))
            .Should().Throw<ArgumentException>().WithMessage("*block is 0 bytes*");
    }
}
