using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.Services;
using UnifiedRgb.App.Workbench;

namespace UnifiedRgb.Host.Tests;

public sealed class WholeCaseBlackoutTests
{
    [Theory]
    [InlineData("StaticColor", "#000000", 1, true)]
    [InlineData("StaticColor", "#FF0000", 0, true)]
    [InlineData("StaticColor", "#FF0000", 1, false)]
    [InlineData("Breathing", "#000000", 0, false)]
    public void Only_an_authored_static_black_scene_requests_sleep(string effect, string color, double brightness, bool expected)
    {
        var scene = DtoMapper.FromDto(new SceneDto([new([new("virtual:test", 0)], effect, [color], Brightness: (float)brightness)]));
        WholeCaseBlackout.IsRequested(scene).Should().Be(expected);
        StudioViewModel.IsPersistedBlackout(DtoMapper.ToDto(scene)).Should().Be(expected, "the button and background must recognize the same persisted blackout");
        WholeCaseBlackout.IsRequested(scene with { Package = new("content", "binding", "v1") }).Should().BeFalse();
        StudioViewModel.IsPersistedBlackout(DtoMapper.ToDto(scene with { Package = new("content", "binding", "v1") })).Should().BeFalse();
        WholeCaseBlackout.IsRequested(Scene.Empty).Should().BeFalse();
    }
}
