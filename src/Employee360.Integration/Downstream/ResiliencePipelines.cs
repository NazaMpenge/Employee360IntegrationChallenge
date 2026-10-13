using System.Net;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Employee360.Integration.Downstream;

public static class ResiliencePipelines
{
    public static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

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
                    .Handle<HttpRequestException>()      
                    .Handle<TimeoutRejectedException>()  
                    .HandleResult(r => IsTransient(r.StatusCode)),
                OnRetry = args =>
                {
                    var reason = args.Outcome.Exception?.GetType().Name
                                 ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString()
                                 ?? "unknown";
                    logger.LogWarning(
                        "Employee 360 call failed, retrying. Retry={Retry} Reason={Reason} Delay={Delay}",
                        args.AttemptNumber + 1, reason, args.RetryDelay);

                    args.Outcome.Result?.Dispose(); 
                    return default;
                }
            })
            .AddTimeout(options.AttemptTimeout)
            .Build();
    }
}
