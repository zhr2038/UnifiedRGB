using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.ScenePackages;
using Xunit.Abstractions;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Audit R5: bounded chunked scene-package import (Host staging sessions, the App upload/fallback rules, the stream reader/store
/// path) and the cached library usage accounting. Real Host DI with fake hardware only; nothing reaches real devices.
/// </summary>
[Collection("Host")]
public sealed class ScenePackageStreamingAuditTests(ITestOutputHelper output)
{
    private sealed record Rig(TestHost Host) : IAsyncDisposable
    {
        public ScenePackageService Service => Host.Get<ScenePackageService>();
        public ScenePackageStore Store => Host.Get<ScenePackageStore>();
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> StartAsync()
    {
        var screens = new FakeScreens();
        var host = await TestHost.StartAsync(s =>
        {
            s.AddSingleton<IDeviceAdapterFactory>(new PackageAdapterFactory(new PackageLoopAdapter()));
            s.AddSingleton(screens.Platform);
        });
        return new Rig(host);
    }

    private static async Task<ScenePackageDetailsDto> UploadAsync(IScenePackageService svc, byte[] zip, int chunk = 64 * 1024)
    {
        var session = await svc.BeginScenePackageUploadAsync(new("upload.zip", zip.Length, Pkg.Sha(zip)));
        for (var offset = 0; offset < zip.Length; offset += chunk)
        {
            var n = Math.Min(chunk, zip.Length - offset);
            (await svc.UploadScenePackageChunkAsync(new(session.Token, offset, Convert.ToBase64String(zip, offset, n)))).Received.Should().Be(offset + n);
        }

        return await svc.FinishScenePackageUploadAsync(new(session.Token));
    }

    private static Task<ScenePackageDetailsDto> LegacyAsync(ScenePackageService svc, byte[] zip) =>
        svc.ImportScenePackageAsync(new ScenePackageUploadDto("upload.zip", Convert.ToBase64String(zip)));

    private static string[] Staged(Rig rig) => Directory.Exists(rig.Service.UploadRoot) ? Directory.GetFiles(rig.Service.UploadRoot) : [];

    private static string Chunk(byte[] data, int offset, int count) => Convert.ToBase64String(data, offset, count);

    [Fact]
    public async Task Chunked_import_installs_exactly_what_the_legacy_import_installs()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        foreach (var zip in new[] { Pkg.Lighting(), Pkg.Mixed(Pkg.Png()) })
        {
            var chunked = await UploadAsync(svc, zip, chunk: 1000); // many chunks, last one partial
            File.ReadAllBytes(Path.Combine(rig.Store.Root, chunked.Info.ContentId, ScenePackageStore.ZipName)).Should().Equal(zip, "the original ZIP is stored byte for byte");
            var legacy = await LegacyAsync(svc, zip);
            legacy.Should().BeEquivalentTo(chunked, "both paths validate and install the same content");
        }

        (await svc.ListScenePackagesAsync()).Should().HaveCount(2);
        Staged(rig).Should().BeEmpty("a finished session deletes its staging file");
        svc.OpenUploads.Should().Be(0);
    }

    [Fact]
    public async Task Begin_validates_the_declaration_and_tokens_are_random_and_never_paths()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var sha = Pkg.Sha([1, 2, 3]);
        foreach (var bad in new BeginScenePackageUploadDto[]
                 {
                     new("x", 0, sha), new("x", ScenePackageFormat.MaxZipBytes + 1, sha), new("x", -5, sha),
                     new("x", 3, sha.ToUpperInvariant()), new("x", 3, sha[..63]), new("x", 3, "../" + sha[3..]),
                     new("", 3, sha), new("a\nb", 3, sha), new(new string('n', 129), 3, sha),
                 })
        {
            await FluentActions.Awaiting(() => svc.BeginScenePackageUploadAsync(bad)).Should().ThrowAsync<ArgumentException>();
        }

        var a = await svc.BeginScenePackageUploadAsync(new("a", 3, sha));
        var b = await svc.BeginScenePackageUploadAsync(new("b", 3, sha));
        foreach (var t in new[] { a.Token, b.Token })
        {
            t.Should().MatchRegex("^[0-9a-f]{64}$");
        }

        a.Token.Should().NotBe(b.Token);
        a.MaxChunkBytes.Should().Be(ScenePackageService.MaxUploadChunkBytes);
        Staged(rig).Select(Path.GetFileName).Should().BeEquivalentTo([a.Token + ".part", b.Token + ".part"]);
        foreach (var token in new[] { "..\\" + a.Token, a.Token.ToUpperInvariant(), "", a.Token + ".part", Path.Combine(svc.UploadRoot, a.Token) })
        {
            await FluentActions.Awaiting(() => svc.UploadScenePackageChunkAsync(new(token, 0, "AAEC"))).Should().ThrowAsync<ArgumentException>();
            await FluentActions.Awaiting(() => svc.FinishScenePackageUploadAsync(new(token))).Should().ThrowAsync<ArgumentException>();
            (await svc.AbortScenePackageUploadAsync(new(token))).Should().BeFalse();
        }

        svc.OpenUploads.Should().Be(2, "unknown tokens never touch an open session");
        (await svc.AbortScenePackageUploadAsync(new(a.Token))).Should().BeTrue();
        (await svc.AbortScenePackageUploadAsync(new(b.Token))).Should().BeTrue();
        Staged(rig).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_chunk_ends_the_session_and_installs_nothing()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var zip = Pkg.Lighting();

        async Task RefusedAsync(Func<string, ScenePackageUploadChunkDto> chunk, long declared = -1, string? sha = null)
        {
            var s = await svc.BeginScenePackageUploadAsync(new("x", declared < 0 ? zip.Length : declared, sha ?? Pkg.Sha(zip)));
            await svc.UploadScenePackageChunkAsync(new(s.Token, 0, Chunk(zip, 0, 100)));
            await FluentActions.Awaiting(() => svc.UploadScenePackageChunkAsync(chunk(s.Token))).Should().ThrowAsync<ArgumentException>();
            await FluentActions.Awaiting(() => svc.UploadScenePackageChunkAsync(new(s.Token, 100, Chunk(zip, 100, 100))))
                .Should().ThrowAsync<ArgumentException>("the refusal ended the session; a correct follow-up chunk is not accepted");
            await FluentActions.Awaiting(() => svc.FinishScenePackageUploadAsync(new(s.Token))).Should().ThrowAsync<ArgumentException>();
            Staged(rig).Should().BeEmpty();
        }

        await RefusedAsync(t => new(t, 0, Chunk(zip, 0, 100)));                 // rewrite of received bytes
        await RefusedAsync(t => new(t, 200, Chunk(zip, 200, 100)));             // gap
        await RefusedAsync(t => new(t, 100, "not base64!"));                    // invalid text
        await RefusedAsync(t => new(t, 100, ""));                               // empty
        await RefusedAsync(t => new(t, 100, Convert.ToBase64String(new byte[ScenePackageService.MaxUploadChunkBytes + 1]))); // oversized
        await RefusedAsync(t => new(t, 100, Chunk(zip, 100, 101)), declared: 200); // past the declared length
        svc.OpenUploads.Should().Be(0);
        (await svc.ListScenePackagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Finish_refuses_short_mismatched_or_invalid_uploads_exactly_like_the_legacy_import()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var zip = Pkg.Lighting();

        var shortUpload = await svc.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(zip)));
        await svc.UploadScenePackageChunkAsync(new(shortUpload.Token, 0, Chunk(zip, 0, zip.Length - 1)));
        await FluentActions.Awaiting(() => svc.FinishScenePackageUploadAsync(new(shortUpload.Token))).Should().ThrowAsync<ArgumentException>().WithMessage("*declared bytes*");
        await FluentActions.Awaiting(() => svc.FinishScenePackageUploadAsync(new(shortUpload.Token))).Should().ThrowAsync<ArgumentException>("Finish always ends the session");

        var other = Pkg.Lighting(leds: 9);
        var mismatch = await svc.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(other)));
        await svc.UploadScenePackageChunkAsync(new(mismatch.Token, 0, Chunk(zip, 0, zip.Length)));
        await FluentActions.Awaiting(() => svc.FinishScenePackageUploadAsync(new(mismatch.Token))).Should().ThrowAsync<ArgumentException>().WithMessage("*SHA-256*");

        // Correctly declared bytes that are not a valid package go through the unchanged ZIP/content validation.
        foreach (var invalid in new[]
                 {
                     new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 },
                     Pkg.Zip([("../evil.bin", new byte[] { 1 })]),
                     Pkg.Lighting(edit: m => m["format"] = "not-a-scene-package"),
                 })
        {
            var legacy = await Record.ExceptionAsync(() => LegacyAsync(svc, invalid));
            var chunked = await Record.ExceptionAsync(() => UploadAsync(svc, invalid));
            legacy.Should().NotBeNull();
            chunked.Should().NotBeNull();
            chunked!.GetType().Should().Be(legacy!.GetType());
            chunked.Message.Should().Be(legacy.Message);
        }

        (await svc.ListScenePackagesAsync()).Should().BeEmpty();
        Staged(rig).Should().BeEmpty();
        svc.OpenUploads.Should().Be(0);
    }

    [Fact]
    public async Task Abort_and_idle_or_lifetime_expiry_delete_staged_bytes()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var zip = Pkg.Lighting();
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        svc.UploadClock = () => now;
        var tick = 0L;
        svc.UploadMonotonicMilliseconds = () => tick;
        void Advance(TimeSpan duration) { now += duration; tick += (long)duration.TotalMilliseconds; }

        var aborted = await svc.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(zip)));
        await svc.UploadScenePackageChunkAsync(new(aborted.Token, 0, Chunk(zip, 0, 10)));
        Staged(rig).Should().ContainSingle();
        (await svc.AbortScenePackageUploadAsync(new(aborted.Token))).Should().BeTrue();
        (await svc.AbortScenePackageUploadAsync(new(aborted.Token))).Should().BeFalse();
        Staged(rig).Should().BeEmpty();

        var idle = await svc.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(zip)));
        idle.ExpiresAt.Should().Be(now + svc.UploadIdleTimeout);
        Advance(svc.UploadIdleTimeout - TimeSpan.FromSeconds(1));
        await svc.UploadScenePackageChunkAsync(new(idle.Token, 0, Chunk(zip, 0, 10))); // activity slides the idle expiry
        Advance(svc.UploadIdleTimeout - TimeSpan.FromSeconds(1));
        svc.ExpireUploads();
        svc.OpenUploads.Should().Be(1);
        Advance(TimeSpan.FromSeconds(2));
        svc.ExpireUploads();
        svc.OpenUploads.Should().Be(0);
        Staged(rig).Should().BeEmpty();
        await FluentActions.Awaiting(() => svc.UploadScenePackageChunkAsync(new(idle.Token, 10, Chunk(zip, 10, 10)))).Should().ThrowAsync<ArgumentException>();

        svc.UploadMaxLifetime = TimeSpan.FromMinutes(3);
        var busy = await svc.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(zip)));
        for (var i = 0; i < 2; i++)
        {
            Advance(TimeSpan.FromMinutes(1));
            await svc.UploadScenePackageChunkAsync(new(busy.Token, i * 10, Chunk(zip, i * 10, 10)));
        }

        Advance(TimeSpan.FromMinutes(1)); // continuously active, but past its maximum lifetime
        await FluentActions.Awaiting(() => svc.UploadScenePackageChunkAsync(new(busy.Token, 20, Chunk(zip, 20, 10)))).Should().ThrowAsync<ArgumentException>();
        Staged(rig).Should().BeEmpty();
        svc.OpenUploads.Should().Be(0);
    }

    [Fact]
    public async Task Disposing_the_Host_closes_and_deletes_open_staging_files()
    {
        var rig = await StartAsync();
        ScenePackageService svc;
        string staged;
        try
        {
            svc = rig.Service;
            var zip = Pkg.Lighting();
            var s = await svc.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(zip)));
            await svc.UploadScenePackageChunkAsync(new(s.Token, 0, Chunk(zip, 0, 10)));
            staged = Staged(rig).Single();
        }
        finally
        {
            await rig.DisposeAsync();
        }

        svc.OpenUploads.Should().Be(0);
        File.Exists(staged).Should().BeFalse("disposal closes the delete-on-close staging file (an open file would also block the test cleanup)");
    }

    [Fact]
    public async Task Session_count_and_staging_bytes_are_bounded()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var sha = Pkg.Sha([1]);
        var first = await svc.BeginScenePackageUploadAsync(new("a", 1, sha));
        var second = await svc.BeginScenePackageUploadAsync(new("b", 1, sha));
        await FluentActions.Awaiting(() => svc.BeginScenePackageUploadAsync(new("c", 1, sha))).Should().ThrowAsync<InvalidOperationException>();
        Staged(rig).Should().HaveCount(ScenePackageService.MaxUploadSessions);
        await svc.AbortScenePackageUploadAsync(new(first.Token));
        var third = await svc.BeginScenePackageUploadAsync(new("c", 1, sha));
        await svc.AbortScenePackageUploadAsync(new(second.Token));
        await svc.AbortScenePackageUploadAsync(new(third.Token));

        svc.MaxUploadStagingBytes = 1000;
        var big = await svc.BeginScenePackageUploadAsync(new("a", 600, sha));
        await FluentActions.Awaiting(() => svc.BeginScenePackageUploadAsync(new("b", 600, sha))).Should().ThrowAsync<InvalidOperationException>();
        var fits = await svc.BeginScenePackageUploadAsync(new("b", 400, sha));
        await svc.AbortScenePackageUploadAsync(new(big.Token));
        await svc.AbortScenePackageUploadAsync(new(fits.Token));
        Staged(rig).Should().BeEmpty();
    }

    [Fact]
    public async Task An_interrupted_finalize_installs_nothing_ends_the_session_and_keeps_usage_true()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var zip = Pkg.Mixed(Pkg.Png());
        rig.Store.BeforeCommit = _ => throw new IOException("injected commit failure");
        var failure = await Record.ExceptionAsync(() => UploadAsync(svc, zip));
        failure.Should().NotBeNull();
        failure!.ToString().Should().Contain("injected commit failure");

        svc.OpenUploads.Should().Be(0);
        Staged(rig).Should().BeEmpty();
        (await svc.ListScenePackagesAsync()).Should().BeEmpty();
        Directory.GetDirectories(rig.Store.Root).Select(Path.GetFileName)
            .Should().NotContain(n => n!.StartsWith("~stage-", StringComparison.Ordinal) || ScenePackageFormat.IsSha256Hex(n));
        rig.Store.LibraryUsage().Count.Should().Be(0);

        rig.Store.BeforeCommit = null;
        var installed = await UploadAsync(svc, zip);
        (await svc.ListScenePackagesAsync()).Single().ContentId.Should().Be(installed.Info.ContentId);
        rig.Store.LibraryUsage().Should().Be((1, TreeBytes(rig.Store.Root), 0L));
    }

    [Fact]
    public async Task Finalizing_sessions_keep_their_count_and_staging_reservations_until_the_file_is_closed()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var zip = Pkg.Lighting();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.Store.BeforeCommit = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(8)).Should().BeTrue(); };
        var first = UploadAsync(svc, zip);
        Task<ScenePackageDetailsDto>? second = null;
        try
        {
            (await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();
            var next = await svc.BeginScenePackageUploadAsync(new("second", zip.Length, Pkg.Sha(zip)));
            await svc.UploadScenePackageChunkAsync(new(next.Token, 0, Convert.ToBase64String(zip)));
            second = svc.FinishScenePackageUploadAsync(new(next.Token));
            svc.OpenUploads.Should().Be(2);
            (await svc.AbortScenePackageUploadAsync(new(next.Token))).Should().BeFalse("a queued finalizer is not an unowned upload");
            await FluentActions.Awaiting(() => svc.BeginScenePackageUploadAsync(new("third", zip.Length, Pkg.Sha(zip))))
                .Should().ThrowAsync<InvalidOperationException>();
            Staged(rig).Should().HaveCount(2);
        }
        finally { release.Set(); await first; if (second is not null) await second; }
        svc.OpenUploads.Should().Be(0);
        Staged(rig).Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_expiry_is_not_extended_by_a_backwards_wall_clock()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var wall = DateTimeOffset.UtcNow;
        var tick = 100L;
        svc.UploadClock = () => wall;
        svc.UploadMonotonicMilliseconds = () => tick;
        await svc.BeginScenePackageUploadAsync(new("clock", 1, Pkg.Sha([1])));
        wall = wall.AddYears(-2);
        tick += (long)svc.UploadIdleTimeout.TotalMilliseconds;
        svc.ExpireUploads();
        svc.OpenUploads.Should().Be(0);
        Staged(rig).Should().BeEmpty();
    }

    [Fact]
    public async Task Install_admission_refreshes_actual_disk_usage_even_when_the_ui_cache_is_recent()
    {
        await using var rig = await StartAsync();
        await UploadAsync(rig.Service, Pkg.Lighting());
        rig.Store.LibraryUsage();
        var firstDirectory = Directory.GetDirectories(rig.Store.Root).Single(p => ScenePackageFormat.IsSha256Hex(Path.GetFileName(p)));
        File.WriteAllBytes(Path.Combine(firstDirectory, "outside-owner-added.bin"), new byte[4096]);
        var walks = rig.Store.FullUsageWalks;
        await UploadAsync(rig.Service, Pkg.Mixed(Pkg.Png()));
        rig.Store.FullUsageWalks.Should().BeGreaterThan(walks, "hard quota admission must not trust the UI's cached size");
        rig.Store.LibraryUsage().Bytes.Should().Be(TreeBytes(rig.Store.Root));
    }

    [Fact]
    public async Task Cached_library_usage_stays_exact_across_install_reimport_and_removal()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        var store = rig.Store;

        store.LibraryUsage().Should().Be((0, TreeBytes(store.Root), 0L));
        var a = await UploadAsync(svc, Pkg.Lighting());
        var b = await LegacyAsync(svc, Pkg.Mixed(Pkg.Png()));
        store.LibraryUsage().Should().Be((2, TreeBytes(store.Root), 0L));

        var walks = store.FullUsageWalks;
        var measured = store.MeasuredDirectories;
        store.LibraryUsage().Should().Be((2, TreeBytes(store.Root), 0L));
        store.FullUsageWalks.Should().Be(walks, "a recent complete walk is reused");
        (store.MeasuredDirectories - measured).Should().Be(UncachedTopLevel(store.Root), "only non-content directories are measured again");

        (await UploadAsync(svc, Pkg.Lighting())).Should().BeEquivalentTo(a, "re-importing identical content returns the existing entry");
        store.LibraryUsage().Should().Be((2, TreeBytes(store.Root), 0L));

        (await svc.RemoveScenePackageAsync(new(a.Info.ContentId))).Removed.Should().BeTrue();
        var afterRemove = store.LibraryUsage();
        afterRemove.Count.Should().Be(1);
        afterRemove.Bytes.Should().Be(TreeBytes(store.Root));
        (await svc.GetSceneLibraryUsageAsync()).Packages.Should().Be(1);

        // Documented limit: a change made from outside the Host inside a cached entry is seen at the next full walk.
        File.WriteAllBytes(Path.Combine(store.Root, b.Info.ContentId, "outside.bin"), new byte[1000]);
        store.LibraryUsage().Bytes.Should().Be(TreeBytes(store.Root) - 1000);
        store.UsageRefreshInterval = TimeSpan.Zero;
        store.LibraryUsage().Bytes.Should().Be(TreeBytes(store.Root));
        store.FullUsageWalks.Should().BeGreaterThan(walks);
    }

    [Fact]
    public async Task The_real_pipe_routes_the_chunked_methods_and_an_older_Host_is_detected_as_unsupported()
    {
        await using var rig = await StartAsync();
        var zip = Pkg.Mixed(Pkg.Png());
        await using (var pipe = new NamedPipeClientStream(".", rig.Host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(5000);
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
            var packages = rpc.Attach<IScenePackageService>();
            rpc.StartListening();
            var remote = await UploadAsync(packages, zip, chunk: 4096);
            (await LegacyAsync(rig.Service, zip)).Should().BeEquivalentTo(remote);
            var s = await packages.BeginScenePackageUploadAsync(new("x", zip.Length, Pkg.Sha(zip)));
            var refused = await Record.ExceptionAsync(() => packages.UploadScenePackageChunkAsync(new(s.Token, 7, Chunk(zip, 0, 10))));
            refused.Should().BeOfType<RemoteInvocationException>();
            ScenePackagesViewModel.ChunkedImportUnsupported(refused!).Should().BeFalse("a refused chunk is never treated as an older Host");
            rig.Service.OpenUploads.Should().Be(0);
        }

        foreach (var (target, label) in new (object?, string)[] { (new OldPackages(), "old interface without overrides"), (null, "no package target") })
        {
            var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
            using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
            if (target is IScenePackageService old) server.AddLocalRpcTarget<IScenePackageService>(old, null);
            server.StartListening();
            using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
            var proxy = client.Attach<IScenePackageService>();
            client.StartListening();
            var ex = await Record.ExceptionAsync(() => proxy.BeginScenePackageUploadAsync(new("x", 1, Pkg.Sha([1]))));
            output.WriteLine($"{label}: {ex?.GetType().FullName} {(ex as RemoteInvocationException)?.DeserializedErrorData?.GetType().Name}");
            ex.Should().NotBeNull();
            ScenePackagesViewModel.ChunkedImportUnsupported(ex!).Should().BeTrue(label);
        }
    }

    [Fact]
    public async Task The_workbench_uploads_in_chunks_and_uses_the_legacy_call_only_without_chunk_support()
    {
        var path = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var zip = new byte[(2 * 1024 * 1024) + 12345];
        new Random(5).NextBytes(zip);
        await File.WriteAllBytesAsync(path, zip);
        try
        {
            foreach (var mode in Enum.GetValues<Mode>())
            {
                var service = new ChunkPackages(mode);
                var errors = new List<Exception>();
                var vm = new ScenePackagesViewModel(() => service,
                    () => Task.FromResult<IReadOnlyList<ControllerDto>>([]), () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([]),
                    () => path, errors.Add);
                await vm.RefreshAsync();
                await vm.ImportCommand.ExecuteAsync();
                switch (mode)
                {
                    case Mode.Chunked:
                        errors.Should().BeEmpty();
                        service.Received.ToArray().Should().Equal(zip);
                        service.Calls.Should().StartWith("Begin").And.EndWith("Finish").And.NotContain("Legacy").And.NotContain("Abort");
                        service.Calls.Count(c => c == "Chunk").Should().Be(3, "1 MiB chunks");
                        service.Declared.Should().Be(((long)zip.Length, Pkg.Sha(zip)));
                        break;
                    case Mode.MissingMethod or Mode.NotSupported:
                        errors.Should().BeEmpty();
                        service.Calls.Should().Equal("Begin", "Legacy");
                        service.LegacyBytes.Should().Equal(zip);
                        break;
                    case Mode.ChunkFails:
                        errors.Should().ContainSingle().Which.Message.Should().Contain("chunk refused");
                        service.Calls.Should().Equal("Begin", "Chunk", "Chunk", "Abort");
                        break;
                    case Mode.FinishFails:
                        errors.Should().ContainSingle().Which.Message.Should().Contain("finish refused");
                        service.Calls.Should().NotContain("Legacy", "a failed commit is never retried through the legacy call");
                        break;
                    case Mode.BeginRefused:
                        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
                        service.Calls.Should().Equal("Begin");
                        break;
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Measured_import_allocations_drop_the_whole_ZIP_copy()
    {
        await using var rig = await StartAsync();
        var svc = rig.Service;
        await LegacyAsync(svc, Pkg.Lighting(leds: 7)); // warm both paths (JIT, pools)
        await UploadAsync(svc, Pkg.Lighting(leds: 6));

        // Inert-sized but real packages: 12 MB of RGB24 stored without compression (within every reader cap; not 64 MiB of media).
        var legacyZip = Stored(Pkg.Lighting(leds: 1024, frames: 4000));
        var chunkedZip = Stored(Pkg.Lighting(leds: 1024, frames: 3999));
        var base64 = Convert.ToBase64String(legacyZip);
        var chunks = Enumerable.Range(0, (chunkedZip.Length + ScenePackageService.MaxUploadChunkBytes - 1) / ScenePackageService.MaxUploadChunkBytes)
            .Select(i => Chunk(chunkedZip, i * ScenePackageService.MaxUploadChunkBytes, Math.Min(ScenePackageService.MaxUploadChunkBytes, chunkedZip.Length - (i * ScenePackageService.MaxUploadChunkBytes))))
            .ToArray();

        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await svc.ImportScenePackageAsync(new ScenePackageUploadDto("legacy.zip", base64));
        var legacy = GC.GetTotalAllocatedBytes(precise: true) - before;

        GC.Collect();
        before = GC.GetTotalAllocatedBytes(precise: true);
        var s = await svc.BeginScenePackageUploadAsync(new("chunked.zip", chunkedZip.Length, Pkg.Sha(chunkedZip)));
        long offset = 0;
        foreach (var c in chunks)
        {
            offset = (await svc.UploadScenePackageChunkAsync(new(s.Token, offset, c))).Received;
        }

        await svc.FinishScenePackageUploadAsync(new(s.Token));
        var chunked = GC.GetTotalAllocatedBytes(precise: true) - before;

        using var process = Process.GetCurrentProcess();
        var report = $"zip={legacyZip.Length} legacyAllocated={legacy} chunkedAllocated={chunked} processPeakWorkingSet={process.PeakWorkingSet64}";
        output.WriteLine(report);
        var target = Environment.GetEnvironmentVariable("URGB_AUDIT_MEMORY_OUT");
        if (!string.IsNullOrEmpty(target)) File.WriteAllText(target, report + Environment.NewLine);
        (await svc.ListScenePackagesAsync()).Should().HaveCount(4);
        chunked.Should().BeLessThan(legacy - (legacyZip.Length / 2), "the chunked Host path keeps no in-memory copy of the ZIP (allocations are process-wide, so a margin is used)");
    }

    private static byte[] Stored(byte[] zip)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in Pkg.Entries(zip))
            {
                var e = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(data);
            }
        }

        return ms.ToArray();
    }

    private static long TreeBytes(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Sum(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length))
        : 0;

    private static int UncachedTopLevel(string root) => Directory.EnumerateDirectories(root).Select(Path.GetFileName)
        .Count(n => !ScenePackageFormat.IsSha256Hex(n) && !n!.StartsWith("~corrupt-", StringComparison.Ordinal));

    /// <summary>An implementation compiled against the interface before chunked import existed (it inherits the defaults).</summary>
    private sealed class OldPackages : IScenePackageService
    {
        public Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScenePackageInfoDto>>([]);
        public Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ScenePackageApplyDto> ApplyScenePackageCheckedAsync(ScenePackageApplyRequestDto request, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private enum Mode { Chunked, MissingMethod, NotSupported, ChunkFails, FinishFails, BeginRefused }

    private sealed class ChunkPackages(Mode mode) : IScenePackageService
    {
        private static readonly ScenePackageInfoDto Info = new("c", "c", "Package", 1, "rgb24-hold-physical-ms-v1", 12000, 1, 0);
        public List<string> Calls { get; } = [];
        public MemoryStream Received { get; } = new();
        public byte[]? LegacyBytes { get; private set; }
        public (long Length, string Sha)? Declared { get; private set; }

        public Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScenePackageInfoDto>>([Info]);
        public Task<ScenePackageDetailsDto> GetScenePackageAsync(string contentId, CancellationToken ct = default) => Task.FromResult(new ScenePackageDetailsDto(Info, []));
        public Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string contentId, CancellationToken ct = default) => Task.FromResult(new ScenePackageBindingStateDto(contentId, null, [], false, []));
        public Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto bindings, CancellationToken ct = default) => throw new InvalidOperationException("no writes expected");
        public Task<ScenePackageApplyDto> ApplyScenePackageAsync(string contentId, CancellationToken ct = default) => throw new InvalidOperationException("no writes expected");
        public Task<ScenePackageApplyDto> ApplyScenePackageCheckedAsync(ScenePackageApplyRequestDto request, CancellationToken ct = default) => throw new InvalidOperationException("no writes expected");

        public Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto package, CancellationToken ct = default)
        {
            Calls.Add("Legacy");
            LegacyBytes = Convert.FromBase64String(package.Base64);
            return Task.FromResult(new ScenePackageDetailsDto(Info, []));
        }

        public Task<ScenePackageUploadSessionDto> BeginScenePackageUploadAsync(BeginScenePackageUploadDto upload, CancellationToken ct = default)
        {
            Calls.Add("Begin");
            Declared = (upload.Length, upload.Sha256);
            return mode switch
            {
                // StreamJsonRpc creates this exception internally when the peer lacks the method.
                Mode.MissingMethod => Task.FromException<ScenePackageUploadSessionDto>((RemoteMethodNotFoundException)
                    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RemoteMethodNotFoundException))),
                Mode.NotSupported => Task.FromException<ScenePackageUploadSessionDto>(new NotSupportedException()),
                Mode.BeginRefused => Task.FromException<ScenePackageUploadSessionDto>(new InvalidOperationException("2 package uploads are already open")),
                _ => Task.FromResult(new ScenePackageUploadSessionDto("t", ScenePackageService.MaxUploadChunkBytes, DateTimeOffset.UtcNow.AddMinutes(2))),
            };
        }

        public Task<ScenePackageUploadProgressDto> UploadScenePackageChunkAsync(ScenePackageUploadChunkDto chunk, CancellationToken ct = default)
        {
            Calls.Add("Chunk");
            if (mode == Mode.ChunkFails && Received.Length > 0) return Task.FromException<ScenePackageUploadProgressDto>(new IOException("chunk refused"));
            chunk.Offset.Should().Be(Received.Length);
            Received.Write(Convert.FromBase64String(chunk.Base64));
            return Task.FromResult(new ScenePackageUploadProgressDto(Received.Length));
        }

        public Task<ScenePackageDetailsDto> FinishScenePackageUploadAsync(ScenePackageUploadTokenDto upload, CancellationToken ct = default)
        {
            Calls.Add("Finish");
            return mode == Mode.FinishFails
                ? Task.FromException<ScenePackageDetailsDto>(new ArgumentException("finish refused"))
                : Task.FromResult(new ScenePackageDetailsDto(Info, []));
        }

        public Task<bool> AbortScenePackageUploadAsync(ScenePackageUploadTokenDto upload, CancellationToken ct = default)
        {
            Calls.Add("Abort");
            return Task.FromResult(true);
        }
    }
}
