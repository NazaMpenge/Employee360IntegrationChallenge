using Employee360.Integration.Contracts;

namespace Employee360.Integration.Downstream;

public enum DownstreamOutcome
{
    /// <summary>Employee 360 accepted the update.</summary>
    Success,

    /// <summary>Timeout, 5xx, 429, network error. Worth retrying; if retries run out the event is dead-lettered for replay.</summary>
    TransientFailure,

    /// <summary>4xx other than 429. Retrying the same payload cannot succeed, so it is dead-lettered immediately.</summary>
    PermanentFailure
}

public sealed record DownstreamResult(DownstreamOutcome Outcome, int? StatusCode, string? Detail)
{
    public static DownstreamResult Succeeded(int? statusCode = null) => new(DownstreamOutcome.Success, statusCode, null);
}

/// <summary>
/// Boundary to System B. The processor only depends on this interface, so retry/HTTP
/// concerns live in one place and the processing logic is testable with a fake.
/// </summary>
public interface IEmployee360Client
{
    Task<DownstreamResult> SendAsync(Employee360Update update, CancellationToken ct);
}
