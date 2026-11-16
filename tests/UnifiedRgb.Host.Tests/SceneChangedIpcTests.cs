using System.Collections.Concurrent;
using System.IO.Pipes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Ipc;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed class SceneChangedIpcTests
{
    private static async Task<(JsonRpc Rpc, IRgbService Rgb, IScenePackageService Packages, NamedPipeClientStream Pipe)> ConnectAsync(TestHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var rgb = rpc.Attach<IRgbService>();
        var packages = rpc.Attach<IScenePackageService>();
        rpc.StartListening();
        return (rpc, rgb, packages, pipe);
    }

    [Fact]
    public async Task SetScene_SetAssignment_ApplyProfile_and_ApplyScenePackage_broadcast_SceneChanged_over_pipe()
    {
        await using var host = await TestHost.StartAsync(s =>
            s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(new PackageLoopAdapter())));

        var (rpc1, rgb1, packages1, pipe1) = await ConnectAsync(host);
        var (rpc2, rgb2, _, pipe2) = await ConnectAsync(host);
        using (rpc1)
        using (rpc2)
        await using (pipe1)
        await using (pipe2)
        {
            var received = new ConcurrentQueue<SceneDto>();
            TaskCompletionSource<SceneDto> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            rgb2.SceneChanged += (_, s) =>
            {
                received.Enqueue(s);
                next.TrySetResult(s);
            };

            static TaskCompletionSource<SceneDto> Reset(ref TaskCompletionSource<SceneDto> tcs)
                => tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // 1. SetSceneAsync
            var scene1 = new SceneDto([new AssignmentDto([new ZoneRefDto("virtual:fan1", 0)], "StaticColor", ["#112233"])]);
            await rgb1.SetSceneAsync(scene1);
            var s1 = await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            s1.Assignments.Should().ContainSingle(a => a.EffectId == "StaticColor").Which.Colors.Should().Equal("#112233");

            // 2. SetAssignmentAsync
            var wait2 = Reset(ref next);
            await rgb1.SetAssignmentAsync(new AssignmentDto([new ZoneRefDto("virtual:fan2", 0)], "StaticColor", ["#445566"]));
            var s2 = await wait2.Task.WaitAsync(TimeSpan.FromSeconds(5));
            s2.Assignments.Should().HaveCount(2);
            s2.Assignments.Should().ContainSingle(a => a.Targets.Any(t => t.ControllerId == "virtual:fan2")).Which.Colors.Should().Equal("#445566");

            // 3. ApplyProfileAsync
            var profileScene = new SceneDto([new AssignmentDto([new ZoneRefDto("virtual:fan1", 0)], "StaticColor", ["#ABCDEF"])]);
            await rgb1.SaveProfileAsync(new ProfileDto("SceneEventProfile", profileScene));
            var wait3 = Reset(ref next);
            await rgb1.ApplyProfileAsync("SceneEventProfile");
            var s3 = await wait3.Task.WaitAsync(TimeSpan.FromSeconds(5));
            s3.Assignments.Should().ContainSingle().Which.Colors.Should().Equal("#ABCDEF");

            // 4. ApplyScenePackageAsync
            var details = await packages1.ImportScenePackageAsync(new ScenePackageUploadDto("pkg.zip", Convert.ToBase64String(Pkg.Lighting())));
            var cid = details.Info.ContentId;
            var bindingState = await packages1.SaveScenePackageBindingsAsync(new ScenePackageBindingsDto(
                cid,
                [new ScenePackageBindingDto("strip", new ZoneRefDto("pkg:1", 0))]));
            bindingState.Ready.Should().BeTrue(string.Join("; ", bindingState.Problems));

            var wait4 = Reset(ref next);
            var applied = await packages1.ApplyScenePackageAsync(cid);
            applied.Applied.Should().BeTrue(applied.Detail);
            var s4 = await wait4.Task.WaitAsync(TimeSpan.FromSeconds(5));
            s4.Package.Should().NotBeNull();
            s4.Package!.ContentId.Should().Be(cid);
            received.Count.Should().BeGreaterThanOrEqualTo(4);
        }
    }

    [Fact]
    public void Legacy_IRgbService_implementations_compile_without_explicitly_implementing_SceneChanged()
    {
        IRgbService svc = new MinimalStubService();
        EventHandler<SceneDto> handler = (_, _) => { };
        svc.SceneChanged += handler;
        svc.SceneChanged -= handler;
    }

    private sealed class MinimalStubService : IRgbService
    {
        public event EventHandler<IReadOnlyList<ControllerDto>>? ControllersChanged { add { } remove { } }
        public event EventHandler<StatusDto>? StatusChanged { add { } remove { } }
        public event EventHandler<IReadOnlyList<TelemetryDto>>? TelemetryTick { add { } remove { } }
        public Task<IReadOnlyList<ControllerDto>> ListControllersAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EffectDto>> ListEffectsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SceneDto> GetSceneAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSceneAsync(SceneDto scene, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAssignmentAsync(AssignmentDto assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProfileInfoDto>> ListProfilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveProfileAsync(ProfileDto profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProfileDto> LoadProfileAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyProfileAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StatusDto> GetStatusAsync(bool diagnostics = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TelemetryDto>> GetTelemetryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TakeoverDto> SetTakeoverAsync(string deviceClass, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ControllerDto>> RescanAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> SaveLianLiConfigAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ResyncLianLiAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MountingDto>> ListMountingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MountingDto> SetMountingAsync(MountingDto mounting, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MountingDto> ResetMountingAsync(string controllerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MountingDto> BeginRingCalibrationAsync(RingCalibrationDto calibration, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MountingDto> EndRingCalibrationAsync(string controllerId, bool apply, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BringupSessionDto> BeginBringupAsync(string controllerId, int leaseSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProbeResultDto> RunNativeProbeAsync(string controllerId, string probe, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProbeResultDto> BeginMarkerAsync(MarkerDto marker, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProbeResultDto> EndMarkerAsync(string controllerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BringupSessionDto> RenewBringupAsync(string controllerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BringupEndDto> EndBringupAsync(string controllerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlaybackCalibrationDto> GetPlaybackCalibrationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlaybackCalibrationDto> SetPlaybackCalibrationAsync(PlaybackCalibrationDto calibration, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
