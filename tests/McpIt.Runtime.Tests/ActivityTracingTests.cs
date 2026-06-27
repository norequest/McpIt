using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace McpIt.Runtime.Tests;

/// <summary>
/// Tests for D1: OpenTelemetry activity tracing in HttpClientMcpEndpointInvoker.
///
/// Design note: multiple test classes in this assembly run in parallel. All listeners
/// registered against McpItActivitySource.Source will see every activity from every
/// parallel InvokeAsync call. Tests use unique path segments (e.g. "trace-basic-42") so
/// they can filter _captured by url.path tag and find exactly their own activity.
/// </summary>
public class ActivityTracingTests : IDisposable
{
    // Thread-safe because stopped activities arrive on the thread that stopped them.
    private readonly ConcurrentBag<Activity> _captured = new();
    private readonly ActivityListener _listener;

    public ActivityTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "McpIt",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _captured.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ------------------------------------------------------------------
    // Shared helpers (same pattern as HttpClientMcpEndpointInvokerTests)
    // ------------------------------------------------------------------

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode = HttpStatusCode.OK;
        public string ResponseBody = "RESPONSE";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseBody),
            });
    }

    private static HttpClientMcpEndpointInvoker WithExplicitBase(HttpClient http, string baseAddress) =>
        new(http, new HttpContextAccessor(), new McpEndpointsOptions { BaseAddress = new Uri(baseAddress) });

    private static HttpClientMcpEndpointInvoker WithThrowOnError(HttpClient http, string baseAddress) =>
        new(http, new HttpContextAccessor(),
            new McpEndpointsOptions { BaseAddress = new Uri(baseAddress), ThrowOnUnsuccessfulResponse = true });

    /// <summary>
    /// Finds the single activity whose url.path tag contains the given sentinel.
    /// Using a unique sentinel per test lets us isolate our activity even when other
    /// parallel tests are also producing activities from McpItActivitySource.Source.
    /// </summary>
    private Activity? FindByPath(string pathSentinel)
    {
        foreach (var a in _captured)
        {
            if ((a.GetTagItem("url.path") as string)?.Contains(pathSentinel) == true)
                return a;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Successful_invocation_produces_activity_with_expected_name()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var invoker = WithExplicitBase(http, "http://localhost/");

        await invoker.InvokeAsync("GET", "trace-name-test", null, null, CancellationToken.None);

        var activity = FindByPath("trace-name-test");
        Assert.NotNull(activity);
        Assert.Equal("mcpit.endpoint.invoke", activity!.OperationName);
    }

    [Fact]
    public async Task Activity_carries_http_method_tag()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var invoker = WithExplicitBase(http, "http://localhost/");

        await invoker.InvokeAsync("POST", "trace-method-test", null, "{}", CancellationToken.None);

        var activity = FindByPath("trace-method-test");
        Assert.NotNull(activity);
        Assert.Equal("POST", activity!.GetTagItem("http.request.method"));
    }

    [Fact]
    public async Task Activity_carries_sanitized_path_tag_without_query_string()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var invoker = WithExplicitBase(http, "http://localhost/");

        // query string must NOT appear in the url.path tag
        await invoker.InvokeAsync("GET", "trace-path-sentinel/7", "expand=items&token=secret", null, CancellationToken.None);

        var activity = FindByPath("trace-path-sentinel");
        Assert.NotNull(activity);
        var urlPath = activity!.GetTagItem("url.path") as string;
        Assert.NotNull(urlPath);
        Assert.Contains("trace-path-sentinel", urlPath!);
        Assert.DoesNotContain("expand", urlPath!);
        Assert.DoesNotContain("secret", urlPath!);
    }

    [Fact]
    public async Task Activity_carries_response_status_code_tag()
    {
        var handler = new CapturingHandler { StatusCode = HttpStatusCode.OK };
        var http = new HttpClient(handler);
        var invoker = WithExplicitBase(http, "http://localhost/");

        await invoker.InvokeAsync("GET", "trace-status-test", null, null, CancellationToken.None);

        var activity = FindByPath("trace-status-test");
        Assert.NotNull(activity);
        Assert.Equal(200, activity!.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task Failed_invocation_with_throw_sets_activity_status_to_error()
    {
        var handler = new CapturingHandler
        {
            StatusCode = HttpStatusCode.InternalServerError,
            ResponseBody = "server error",
        };
        var http = new HttpClient(handler);
        var invoker = WithThrowOnError(http, "http://localhost/");

        await Assert.ThrowsAsync<McpEndpointInvocationException>(() =>
            invoker.InvokeAsync("GET", "trace-error-test", null, null, CancellationToken.None));

        var activity = FindByPath("trace-error-test");
        Assert.NotNull(activity);
        Assert.Equal(ActivityStatusCode.Error, activity!.Status);
        Assert.Equal(500, activity!.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public void Activity_source_is_named_McpIt()
    {
        Assert.Equal("McpIt", McpItActivitySource.Source.Name);
    }

    [Fact]
    public async Task No_listener_attached_means_no_activity_and_invocation_still_succeeds()
    {
        // Dispose our listener so McpIt has no active listener.
        _listener.Dispose();

        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var invoker = WithExplicitBase(http, "http://localhost/");

        // InvokeAsync must not throw even when StartActivity returns null.
        var body = await invoker.InvokeAsync("GET", "trace-nolisten-test", null, null, CancellationToken.None);

        Assert.Equal("RESPONSE", body);
        // No activity in our bag for this path (listener was disposed before the call).
        Assert.Null(FindByPath("trace-nolisten-test"));
    }
}
