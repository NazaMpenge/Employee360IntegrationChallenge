using System.Text.Json;
using Employee360.Integration.Contracts;
using Polly;
using Polly.Timeout;

namespace Employee360.Integration.Downstream;

/// <summary>
/// Sends one update to Employee 360 and reduces whatever happened to Success / Transient / Permanent.
/// Retries happen inside the pipeline; by the time this returns Transient, retries are exhausted.
/// </summary>
public sealed class Employee360HttpClient : IEmployee360Client
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly ILogger<Employee360HttpClient> _logger;

    public Employee360HttpClient(
        HttpClient http,
        ResiliencePipeline<HttpResponseMessage> pipeline,
        ILogger<Employee360HttpClient> logger)
    {
        _http = http;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<DownstreamResult> SendAsync(Employee360Update update, CancellationToken ct)
    {
        try
        {
            using var response = await _pipeline.ExecuteAsync(async token =>
            {
                // A new request per attempt: an HttpRequestMessage cannot be sent twice.
                using var request = new HttpRequestMessage(
                    HttpMethod.Put, $"api/employees/{Uri.EscapeDataString(update.EmployeeId)}")
                {
                    Content = JsonContent.Create(update, options: Json)
                };

                // The same key on every attempt. If our first call actually succeeded but the
                // response was lost, the retry is recognised downstream and not applied twice.
                request.Headers.Add(IdempotencyKeyHeader, update.SourceEventId);

                return await _http.SendAsync(request, token);
            }, ct);

            return await ClassifyAsync(response, ct);
        }
        catch (TimeoutRejectedException)
        {
            return Transient(null, "Timed out on every attempt.");
        }
        catch (HttpRequestException ex)
        {
            return Transient(null, $"Network error: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled but not by the caller (e.g. an inner timeout): treat as transient.
            return Transient(null, "Request was cancelled by a timeout.");
        }
        // If the caller cancelled (service shutting down) we deliberately let it propagate.
    }

    private async Task<DownstreamResult> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return DownstreamResult.Succeeded((int)response.StatusCode);

        var body = await ReadSnippetAsync(response, ct);
        var detail = $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}".Trim();

        if (ResiliencePipelines.IsTransient(response.StatusCode))
        {
            _logger.LogWarning("Employee 360 still failing after retries. {Detail}", detail);
            return Transient((int)response.StatusCode, detail);
        }

        // Permanent: sending the same payload again would get the same answer.
        _logger.LogError("Employee 360 permanently rejected the update. {Detail}", detail);
        return new DownstreamResult(DownstreamOutcome.PermanentFailure, (int)response.StatusCode, detail);
    }

    private static DownstreamResult Transient(int? status, string detail) =>
        new(DownstreamOutcome.TransientFailure, status, detail);

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > 500 ? text[..500] : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "<could not read response body>";
        }
    }
}
