using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.ScenePackages;

namespace UnifiedRgb.Host.Tests;

public sealed partial class ScenePackageHostTests
{
    [Fact]
    public async Task Editor_RPC_round_trip_preserves_original_output_until_explicit_apply()
    {
        await using var rig = await StartAsync();
        var original = await ImportBoundAsync(rig, Pkg.Lighting());
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", rig.Host.Options.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var rpc = new StreamJsonRpc.JsonRpc(new StreamJsonRpc.HeaderDelimitedMessageHandler(pipe, pipe, UnifiedRgb.Host.Ipc.PipeServer.CreateFormatter()));
        var packages = rpc.Attach<IScenePackageService>(); rpc.StartListening();
        var before = rig.Engine.Loop.Scene;
        var editor = await packages.GetScenePackageEditorAsync(original.ContentId);
        var preview = await packages.GetScenePackagePreviewAsync(original.ContentId);
        var chunk = await packages.ReadScenePackagePreviewChunkAsync(new(original.ContentId, preview.Lighting[0].File, 0));
        Convert.FromBase64String(chunk.Base64).Should().NotBeEmpty();
        var saved = await packages.SaveScenePackageEditAsync(new(original.ContentId, editor.BindingId, editor.Bindings, new(LightingBrightness: .42)));
        await packages.SaveScenePackageVariantAsync(new(original.ContentId, saved.BindingId!, "RPC editor variant"));
        rig.Engine.Loop.Scene.Should().BeSameAs(before);
        saved.Playback.LightingBrightness.Should().Be(.42);
        (await packages.GetScenePackageEditorAsync(original.ContentId, saved.BindingId)).BindingId.Should().Be(saved.BindingId);
    }
    [Fact]
    public async Task Editor_identity_preserves_legacy_binding_bytes_and_original_content()
    {
        await using var rig = await StartAsync();
        var original = await ImportBoundAsync(rig, Pkg.Lighting());
        var editor = await rig.Packages.GetScenePackageEditorAsync(original.ContentId);
        var saved = await rig.Packages.SaveScenePackageEditAsync(new(original.ContentId, editor.BindingId, editor.Bindings, new()));
        saved.BindingId.Should().Be(original.BindingId);
        (await rig.Packages.GetScenePackageAsync(original.ContentId)).Info.ContentId.Should().Be(original.ContentId);
    }

    [Fact]
    public async Task Edited_version_has_exact_RGB_transform_timing_and_strict_contract_without_apply_on_save()
    {
        await using var rig = await StartAsync();
        var original = await ImportBoundAsync(rig, Pkg.Lighting());
        var before = rig.Engine.Loop.Scene;
        var editor = await rig.Packages.GetScenePackageEditorAsync(original.ContentId);
        var options = new ScenePackagePlaybackDto(2, true, .5, 120);
        var saved = await rig.Packages.SaveScenePackageEditAsync(new(original.ContentId, editor.BindingId, editor.Bindings, options));
        saved.BindingId.Should().NotBe(original.BindingId);
        rig.Engine.Loop.Scene.Should().BeSameAs(before);
        var applied = await rig.Packages.ApplyScenePackageCheckedAsync(new(original.ContentId, saved.BindingId!));
        applied.Applied.Should().BeTrue(applied.Detail);
        rig.Engine.Loop.Scene.Package!.PlaybackContract.Should().Be(ScenePackagePlayback.EditedContract);
        var source = rig.Host.Get<ScenePackageFrameSource>();
        source.TryGet(ScenePackageIds.TimelineIdOf(original.ContentId, saved.BindingId!), out var frames).Should().BeTrue();
        frames.IntervalMs.Should().Be(30);
        frames.ZoneRgb[0].Take(24).Should().Equal(ScenePackagePlayback.TransformFrames(Pkg.Rgb(200, 8), 8, 200, options).Take(24));
        (await rig.Packages.GetScenePackageEditorAsync(original.ContentId)).Playback.Should().BeEquivalentTo(options);
    }

    [Fact]
    public async Task Stale_editor_save_and_invalid_values_keep_previous_snapshot_and_scene()
    {
        await using var rig = await StartAsync();
        var original = await ImportBoundAsync(rig, Pkg.Lighting());
        var first = await rig.Packages.GetScenePackageEditorAsync(original.ContentId);
        var second = await rig.Packages.SaveScenePackageEditAsync(new(original.ContentId, first.BindingId, first.Bindings, new(LightingBrightness: .3)));
        Func<Task> stale = () => rig.Packages.SaveScenePackageEditAsync(new(original.ContentId, first.BindingId, first.Bindings, new(LightingBrightness: .6)));
        await stale.Should().ThrowAsync<InvalidOperationException>();
        Func<Task> invalid = () => rig.Packages.SaveScenePackageEditAsync(new(original.ContentId, second.BindingId, first.Bindings, new(Speed: double.NaN)));
        await invalid.Should().ThrowAsync<ArgumentException>();
        (await rig.Packages.GetScenePackageEditorAsync(original.ContentId)).BindingId.Should().Be(second.BindingId);
    }

    [Fact]
    public async Task Bounded_preview_chunks_reconstruct_original_bytes_and_refuse_arbitrary_files()
    {
        await using var rig = await StartAsync();
        var original = await ImportBoundAsync(rig, Pkg.Lighting());
        var preview = await rig.Packages.GetScenePackagePreviewAsync(original.ContentId);
        var t = preview.Lighting.Single(); var bytes = new List<byte>();
        while (bytes.Count < t.Bytes)
        {
            var chunk = await rig.Packages.ReadScenePackagePreviewChunkAsync(new(original.ContentId, t.File, bytes.Count, 701));
            chunk.Offset.Should().Be(bytes.Count); chunk.TotalBytes.Should().Be(t.Bytes); chunk.Sha256.Should().Be(t.Sha256);
            bytes.AddRange(Convert.FromBase64String(chunk.Base64));
        }
        bytes.Should().Equal(Pkg.Rgb(200, 8));
        Func<Task> outside = () => rig.Packages.ReadScenePackagePreviewChunkAsync(new(original.ContentId, "../../takeover.json", 0));
        await outside.Should().ThrowAsync<ArgumentException>();
        Func<Task> tooBig = () => rig.Packages.ReadScenePackagePreviewChunkAsync(new(original.ContentId, t.File, 0, 49153));
        await tooBig.Should().ThrowAsync<ArgumentException>();
        rig.Screens.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Saving_named_variant_does_not_apply_and_pins_edited_snapshot()
    {
        await using var rig = await StartAsync();
        var original = await ImportBoundAsync(rig, Pkg.Lighting());
        var editor = await rig.Packages.GetScenePackageEditorAsync(original.ContentId);
        var saved = await rig.Packages.SaveScenePackageEditAsync(new(original.ContentId, editor.BindingId, editor.Bindings, new(Speed: 2)));
        var before = rig.Engine.Loop.Scene;
        await rig.Packages.SaveScenePackageVariantAsync(new(original.ContentId, saved.BindingId!, "My edited variant"));
        rig.Engine.Loop.Scene.Should().BeSameAs(before);
        var profile = await rig.Rgb.LoadProfileAsync("My edited variant");
        profile.Scene.Package!.BindingId.Should().Be(saved.BindingId);
        profile.Scene.Package.PlaybackContract.Should().Be(ScenePackagePlayback.EditedContract);
        await rig.Rgb.ApplyProfileAsync("My edited variant");
        rig.Engine.Loop.Scene.Package!.BindingId.Should().Be(saved.BindingId);
    }

    [Fact]
    public void Source_time_and_RGB_transform_have_consistent_boundary_semantics()
    {
        var options = new ScenePackagePlaybackDto(2, true, .5, 120);
        ScenePackagePlayback.SourcePosition(0, 1000, options).Should().BeApproximately(999.999999, .000001);
        ScenePackagePlayback.SourcePosition(250, 1000, options).Should().BeApproximately(499.999999, .000001);
        ScenePackagePlayback.SourcePosition(500, 1000, options).Should().BeApproximately(999.999999, .000001);
        ScenePackagePlayback.TransformRgb(255, 0, 0, options, out var r, out var g, out var b);
        r.Should().Be(0); g.Should().Be(128); b.Should().Be(0);
        ScenePackagePlayback.TransformRgb(30, 30, 30, options, out r, out g, out b);
        r.Should().Be(15); g.Should().Be(15); b.Should().Be(15);
        var original = JsonSerializer.Serialize(new BindingSnapshotFile(1, new string('a', 64), []));
        original.Should().NotContain("Playback");
    }
}
