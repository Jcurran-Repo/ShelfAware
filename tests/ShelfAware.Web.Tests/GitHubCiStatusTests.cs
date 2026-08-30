using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

/// <summary>The GitHub-hitting CI provider, driven through a stubbed HTTP handler (no network). The
/// load-bearing behaviours: it never throws to the caller (a non-2xx or a network error come back as an
/// Error status), and only SUCCESSES are cached — an error retries on the next load rather than sticking
/// for the whole cache window.</summary>
public class GitHubCiStatusTests
{
    private const string RunsJson = """
    { "workflow_runs": [ { "name": "CI", "status": "completed", "conclusion": "success",
      "head_branch": "master", "head_sha": "abc1234", "display_title": "green",
      "updated_at": "2026-03-02T09:30:00Z", "html_url": "https://gh/runs/1" } ] }
    """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    private static GitHubCiStatus Service(HttpMessageHandler handler) =>
        new(new StubFactory(handler), Options.Create(new GitHubOptions()), NullLogger<GitHubCiStatus>.Instance);

    [Fact]
    public async Task A_successful_fetch_returns_the_parsed_runs()
    {
        var status = await Service(new StubHandler(_ => Json(HttpStatusCode.OK, RunsJson))).GetAsync();

        Assert.Null(status.Error);
        Assert.Equal("CI", Assert.Single(status.Runs).Workflow);
    }

    [Fact]
    public async Task A_non_2xx_becomes_an_error_state_not_an_exception()
    {
        var status = await Service(new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "boom"))).GetAsync();

        Assert.NotNull(status.Error);
        Assert.Empty(status.Runs);
    }

    [Fact]
    public async Task A_network_failure_becomes_an_error_state_not_an_exception()
    {
        var status = await Service(new StubHandler(_ => throw new HttpRequestException("no network"))).GetAsync();

        Assert.Equal("couldn't reach GitHub", status.Error);
        Assert.Empty(status.Runs);
    }

    [Fact]
    public async Task A_timeout_becomes_an_error_state_not_an_exception()
    {
        // An HttpClient TIMEOUT throws TaskCanceledException (an OperationCanceledException) with the
        // caller's token NOT cancelled. It must degrade to an Error status, not propagate — otherwise the
        // CI card, loaded off OnAfterRenderAsync, hangs on "Loading…" forever.
        var status = await Service(new StubHandler(_ => throw new TaskCanceledException())).GetAsync();

        Assert.NotNull(status.Error);
        Assert.Empty(status.Runs);
    }

    [Fact]
    public async Task A_success_is_cached_so_a_second_call_does_not_refetch()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, RunsJson));
        var service = Service(handler);

        await service.GetAsync();
        await service.GetAsync();

        Assert.Equal(1, handler.Calls); // the second read came from the cache
    }

    [Fact]
    public async Task An_error_is_not_cached_so_the_next_call_retries()
    {
        var responses = new Queue<HttpResponseMessage>(
            [Json(HttpStatusCode.InternalServerError, "boom"), Json(HttpStatusCode.OK, RunsJson)]);
        var handler = new StubHandler(_ => responses.Dequeue());
        var service = Service(handler);

        var first = await service.GetAsync();
        var second = await service.GetAsync();

        Assert.NotNull(first.Error);    // first failed
        Assert.Null(second.Error);      // second retried and succeeded
        Assert.Equal(2, handler.Calls); // the failure was NOT cached
    }
}
