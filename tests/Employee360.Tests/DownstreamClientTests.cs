using System.Net;
using Employee360.Integration.Contracts;
using Employee360.Integration.Downstream;
using Microsoft.Extensions.Logging.Abstractions;

namespace Employee360.Tests;

/// <summary>
/// Fake HTTP server: lets a test script exactly what Employee 360 "answers" on each call
/// and records what was sent, with no network involved.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _callCount;

    public StubHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public int CallCount => _callCount;
    public List<(Uri? Uri, string? IdempotencyKey, string Body)> Requests { get; } = new();

    public static StubHandler Returning(params HttpStatusCode[] statuses) =>
        new((n, _) =>
        {
            var status = statuses[Math.Min(n, statuses.Length) - 1]; // last status repeats
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent($"stub {(int)status}") });
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _callCount);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Headers.TryGetValues(Employee360HttpClient.IdempotencyKeyHeader, out var keys);
        lock (Requests) Requests.Add((request.RequestUri, keys?.SingleOrDefault(), body));

        return await _respond(n, cancellationToken);
    }
}

public class DownstreamClientTests
{
    private static readonly Employee360Update Update = new(
        "EMP10452", "Amira", "Khan", "Amira Khan", "Data & AI", "Cape Town",
        DateTimeOffset.Parse("2026-10-01T08:30:00Z"), "EVT-1", "D365");

    private static Employee360HttpClient CreateClient(
        StubHandler handler, int maxRetries = 2, TimeSpan? attemptTimeout = null)
    {
        var options = new Employee360ApiOptions
        {
            MaxRetryAttempts = maxRetries,
            BaseDelay = TimeSpan.FromMilliseconds(1), // keep tests fast
            AttemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(5)
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://employee360.test/"),
            Timeout = Timeout.InfiniteTimeSpan
        };

        return new Employee360HttpClient(
            http,
            ResiliencePipelines.CreateEmployee360Pipeline(options, NullLogger.Instance),
            NullLogger<Employee360HttpClient>.Instance);
    }

    [Fact]
    public async Task Success_SendsOneRequest_WithIdempotencyKeyAndMappedBody()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK);

        var result = await CreateClient(handler).SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.Success, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://employee360.test/api/employees/EMP10452", request.Uri!.ToString());
        Assert.Equal("EVT-1", request.IdempotencyKey);
        Assert.Contains("\"displayName\":\"Amira Khan\"", request.Body); // camelCase contract
    }

    [Fact]
    public async Task Transient500s_AreRetried_AndEventuallySucceed_WithSameIdempotencyKey()
    {
        var handler = StubHandler.Returning(
            HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var result = await CreateClient(handler, maxRetries: 3).SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.Success, result.Outcome);
        Assert.Equal(3, handler.CallCount);
        Assert.All(handler.Requests, r => Assert.Equal("EVT-1", r.IdempotencyKey)); // retries are safe to repeat
    }

    [Fact]
    public async Task Persistent500_ExhaustsRetries_AndReportsTransientFailure()
    {
        var handler = StubHandler.Returning(HttpStatusCode.InternalServerError);

        var result = await CreateClient(handler, maxRetries: 2).SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.TransientFailure, result.Outcome);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(3, handler.CallCount); // 1 original + 2 retries, then it stops
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ClientErrors_ArePermanent_AndNeverRetried(HttpStatusCode status)
    {
        var handler = StubHandler.Returning(status);

        var result = await CreateClient(handler).SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.PermanentFailure, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TooManyRequests_IsRetried()
    {
        var handler = StubHandler.Returning(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        var result = await CreateClient(handler).SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.Success, result.Outcome);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task NetworkError_IsRetried_ThenSucceeds()
    {
        var handler = new StubHandler((n, _) => n == 1
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await CreateClient(handler).SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.Success, result.Outcome);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task HungCall_TimesOutPerAttempt_ThenReportsTransientFailure()
    {
        // The handler never answers; it only stops when the pipeline cancels it.
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = CreateClient(handler, maxRetries: 1, attemptTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.SendAsync(Update, CancellationToken.None);

        Assert.Equal(DownstreamOutcome.TransientFailure, result.Outcome);
        Assert.Equal(2, handler.CallCount); // the timeout is per attempt, so it was retried once
    }

    [Fact]
    public async Task CallerCancellation_Propagates_InsteadOfBeingSwallowedAsAFailure()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(handler).SendAsync(Update, cts.Token));
    }
}
