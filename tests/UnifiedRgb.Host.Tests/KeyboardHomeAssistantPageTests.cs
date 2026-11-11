using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class KeyboardHomeAssistantPageTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Only_the_acknowledged_selection_can_run_and_a_pending_request_does_not_repeat()
    {
        var options = new KeyboardHomeAssistantOptions
        {
            Url = "http://192.168.10.7",
            Scenes = [new() { EntityId = "scene.arrival", Label = "回家" }, new() { EntityId = "script.movie", Label = "观影", ReadScriptResult = true }],
        };
        using var handler = new FakeHa();
        using var client = new HomeAssistantSceneClient(options, handler, _ => "fake-token");
        var opened = 0;
        using var page = new KeyboardHomeAssistantPage(options, client, TimeProvider.System,
            (_, _, _) => { Interlocked.Increment(ref opened); return Task.FromResult(true); });
        var home = KeyboardHomeAssistantPage.Render(new("2 个场景可用", "↑↓ 场景　中按打开网页", ConnectionBadge: "在线"));
        var arrival = KeyboardHomeAssistantPage.Render(new("回家", "↑↓ 选择　中按执行", 1, 2));
        var movie = KeyboardHomeAssistantPage.Render(new("观影", "↑↓ 选择　中按执行", 2, 2));
        var busy = KeyboardHomeAssistantPage.Render(new("观影", "执行中…", 2, 2));
        var result = KeyboardHomeAssistantPage.Render(new("观影", "部分设备未确认", 2, 2));
        await page.StartAsync(CancellationToken.None);
        page.ObservePage(true);
        try
        {
            await WaitForFrame(page, home);
            page.Interact(2, null, () => true);
            page.Interact(4, home, () => true);
            page.Interact(2, home, () => true);
            await WaitForFrame(page, arrival);
            Volatile.Read(ref opened).Should().Be(0, "moving to a scene invalidates the old home button");
            page.Interact(2, null, () => true);
            page.Interact(4, arrival, () => true);
            page.Interact(2, arrival, () => true);
            await WaitForFrame(page, movie);
            handler.Writes.Should().BeEmpty();
            var checks = 0;
            page.Interact(2, movie, () => Interlocked.Increment(ref checks) == 1);
            await WaitForFrame(page, KeyboardHomeAssistantPage.Render(new("观影", "已取消", 2, 2)));
            handler.Writes.Should().BeEmpty();
            page.Interact(2, page.Pixels, () => true);
            await WaitForFrame(page, busy);
            await handler.Started.Task.WaitAsync(Deadline);
            page.Interact(2, movie, () => true);
            page.Interact(4, busy, () => true);
            handler.Writes.Should().ContainSingle().Which.Should().Be("/api/services/script/movie?return_response");
            handler.Release.TrySetResult();
            await WaitForFrame(page, result);
            handler.Writes.Should().ContainSingle();
            page.ObservePage(false);
            page.ObservePage(true);
            await WaitForFrame(page, home);
        }
        finally { handler.Release.TrySetResult(); await page.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Home_button_opens_only_the_configured_web_address_even_while_refresh_waits()
    {
        var options = new KeyboardHomeAssistantOptions { Url = "http://192.168.10.7", Scenes = [new() { EntityId = "scene.arrival" }] };
        using var handler = new FakeHa { StallRead = true };
        using var client = new HomeAssistantSceneClient(options, handler, _ => "fake-token");
        var launch = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = new KeyboardHomeAssistantPage(options, client, TimeProvider.System, (endpoint, admitted, _) =>
        {
            admitted().Should().BeTrue();
            launch.TrySetResult(endpoint);
            return Task.FromResult(true);
        });
        await page.StartAsync(CancellationToken.None);
        page.ObservePage(true);
        try
        {
            var home = KeyboardHomeAssistantPage.Render(new("正在连接", "↑↓ 场景　中按打开网页", ConnectionBadge: "连接中"));
            await WaitForFrame(page, home);
            await handler.ReadStarted.Task.WaitAsync(Deadline);
            page.Interact(2, home, () => true);
            var endpoint = await launch.Task.WaitAsync(TimeSpan.FromSeconds(3));
            endpoint.AbsoluteUri.Should().Be("http://192.168.10.7/");
            handler.Writes.Should().BeEmpty();
        }
        finally { await page.StopAsync(CancellationToken.None); }
    }

    private static async Task WaitForFrame(KeyboardHomeAssistantPage page, byte[] expected)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (page.Pixels is not { } pixels || !pixels.AsSpan().SequenceEqual(expected))
            await Task.Delay(25, timeout.Token);
    }

    private sealed class FakeHa : HttpMessageHandler
    {
        public ConcurrentQueue<string> Writes { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StallRead { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization!.Parameter.Should().Be("fake-token");
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get)
            {
                ReadStarted.TrySetResult();
                if (StallRead) await Release.Task.WaitAsync(cancellationToken);
                var entity = uri.Segments[^1];
                var state = entity.StartsWith("scene.", StringComparison.Ordinal) ? "unknown" : "off";
                return Reply($$$"""{"entity_id":"{{{entity}}}","state":"{{{state}}}","attributes":{}}""");
            }
            request.Method.Should().Be(HttpMethod.Post);
            Writes.Enqueue(uri.PathAndQuery);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Reply("""{"service_response":{"unconfirmed":["示例设备"]}}""");
        }

        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
