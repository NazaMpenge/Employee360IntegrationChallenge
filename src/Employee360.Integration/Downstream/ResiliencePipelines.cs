using System.Net;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Employee360.Integration.Downstream;

public static class ResiliencePipelines
{
    /// <summary>
    /// Which HTTP statuses are worth retrying. 5xx, 408 and 429 are "try again later";
    /// every other 4xx means "this request is wrong" and retrying cannot fix it.
    /// </summary>
    public static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

    /// <summary>
    /// Retry (outer) wrapping a per-attempt timeout (inner). Because the timeout sits inside the
    /// retry, a hung call is cut off after AttemptTimeout and then retried, instead of one
    /// timeout covering every attempt.
    /// </summary>
    public static ResiliencePipeline<HttpResponseMessage> CreateEmployee360Pipeline(
        Employee360ApiOptions options, ILogger logger)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = options.BaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()      // connection refused, DNS, reset...
                    .Handle<TimeoutRejectedException>()  // our per-attempt timeout fired
                    .HandleResult(r => IsTransient(r.StatusCode)),
                OnRetry = args =>
                {
                    var reason = args.Outcome.Exception?.GetType().Name
                                 ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString()
                                 ?? "unknown";
                    logger.LogWarning(
                        "Employee 360 call failed, retrying. Retry={Retry} Reason={Reason} Delay={Delay}",
                        args.AttemptNumber + 1, reason, args.RetryDelay);

                    args.Outcome.Result?.Dispose(); // the response we are discarding
                    return default;
                }
            })
            .AddTimeout(options.AttemptTimeout)
            .Build();
    }
}
