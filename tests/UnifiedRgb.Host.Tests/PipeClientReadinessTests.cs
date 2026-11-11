using System.Reflection;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Tests;

/// <summary>Deterministic publication checkpoints only: unused proxies and a never-started client, no production pipe or Host.</summary>
public sealed class PipeClientReadinessTests
{
    [Fact]
    public async Task Partial_RGB_publication_is_not_connected_and_no_optional_proxy_escapes_before_the_complete_state()
    {
        await using var client = new PipeClient("unused-readiness-" + Guid.NewGuid().ToString("N"));
        Set(client, "_state", ConnectionState.Connecting);
        Set(client, "_service", Proxy<IRgbService>()); // the precise old RunAsync window, before optional attachments

        (await client.WaitConnectedAsync(TimeSpan.Zero)).Should().BeFalse("RGB alone is not a complete session");
        client.Service.Should().BeNull();
        foreach (var require in Requires(client)) require.Should().Throw<HostNotConnectedException>();
        var wait = client.WaitConnectedAsync(TimeSpan.FromSeconds(5));
        wait.IsCompleted.Should().BeFalse();

        AttachRemaining(client);
        (await client.WaitConnectedAsync(TimeSpan.Zero)).Should().BeFalse("all proxies still await the Connected publication");
        foreach (var require in Requires(client)) require.Should().Throw<HostNotConnectedException>();
        var observed = false;
        client.StateChanged += (_, state) =>
        {
            if (state != ConnectionState.Connected) return;
            foreach (var require in Requires(client)) require.Should().NotThrow();
            observed = true;
        };
        typeof(PipeClient).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [ConnectionState.Connected]);
        (await wait).Should().BeTrue();
        observed.Should().BeTrue(); client.Service.Should().NotBeNull();

        Set(client, "_service", null); // first cleanup write; State is not yet notified and optional fields still exist
        (await client.WaitConnectedAsync(TimeSpan.Zero)).Should().BeFalse();
        client.Service.Should().BeNull();
        foreach (var require in Requires(client)) require.Should().Throw<HostNotConnectedException>();
    }

    [Fact]
    public async Task Partial_publication_keeps_cancellation_disposal_and_the_one_way_UI_fault_gate()
    {
        var client = new PipeClient("unused-readiness-" + Guid.NewGuid().ToString("N"));
        Set(client, "_state", ConnectionState.Connecting); Set(client, "_service", Proxy<IRgbService>());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await client.Awaiting(c => c.WaitConnectedAsync(Timeout.InfiniteTimeSpan, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        client.BlockForUiFault("partial session is blocked");
        (await client.WaitConnectedAsync(Timeout.InfiniteTimeSpan)).Should().BeFalse();
        foreach (var require in Requires(client)) require.Should().Throw<HostAccessBlockedException>().WithMessage("partial session is blocked");
        await client.DisposeAsync();
        (await client.WaitConnectedAsync(TimeSpan.Zero)).Should().BeFalse();
        client.Service.Should().BeNull();
    }

    private static void AttachRemaining(PipeClient c)
    {
        Set(c, "_screens", Proxy<IScreenService>()); Set(c, "_packages", Proxy<IScenePackageService>());
        Set(c, "_library", Proxy<ISceneLibraryService>()); Set(c, "_screenSettings", Proxy<IScreenDeviceSettingsService>());
        Set(c, "_screenPerformance", Proxy<IScreenPerformanceService>()); Set(c, "_screenVideo", Proxy<IScreenVideoService>());
        Set(c, "_keyboardDisplay", Proxy<IKeyboardDisplayService>()); Set(c, "_peripheralConfiguration", Proxy<IPeripheralConfigurationService>());
        Set(c, "_nativeMenu", Proxy<INativeMenuService>());
    }

    private static IEnumerable<Action> Requires(PipeClient c) =>
    [
        () => c.Require(), () => c.RequireScreens(), () => c.RequirePackages(), () => c.RequireLibrary(),
        () => c.RequireScreenSettings(), () => c.RequireScreenPerformance(), () => c.RequireScreenVideo(),
        () => c.RequireKeyboardDisplay(), () => c.RequirePeripheralConfiguration(), () => c.RequireNativeMenu(),
    ];
    private static void Set(PipeClient client, string field, object? value) =>
        typeof(PipeClient).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, value);
    private static T Proxy<T>() where T : class => DispatchProxy.Create<T, UnusedProxy>();
    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Readiness tests must not invoke Host methods.");
    }
}
