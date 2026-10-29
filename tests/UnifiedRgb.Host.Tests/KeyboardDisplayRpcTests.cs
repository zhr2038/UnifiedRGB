using System.Text.Json;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class KeyboardDisplayRpcTests
{
    [Fact]
    public async Task Additive_keyboard_endpoint_round_trips_and_writes_only_its_local_fake_settings()
    {
        await using var host = await TestHost.StartAsync();
        await using var client = new PipeClient(host.Options.PipeName, PipeTrust.ElevatedOwnerOrCurrentUser);
        client.Start(); (await client.WaitConnectedAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        var before = JsonSerializer.Serialize(await client.Require().GetSceneAsync());
        var keyboard = client.RequireKeyboardDisplay();
        (await keyboard.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
        await keyboard.SetKeyboardDisplayEnabledAsync(false);
        (await keyboard.GetKeyboardDisplayStatusAsync()).Enabled.Should().BeFalse();
        File.Exists(Path.Combine(host.Options.ResolvedDataDirectory, "peripherals", "keyboard-display.json")).Should().BeTrue();
        await ((Func<Task>)(() => keyboard.SetKeyboardDisplayEnabledAsync(true))).Should().ThrowAsync<StreamJsonRpc.RemoteInvocationException>();
        JsonSerializer.Serialize(await client.Require().GetSceneAsync()).Should().Be(before);
    }
}
