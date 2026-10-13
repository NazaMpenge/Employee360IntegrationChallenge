using Employee360.Integration.Contracts;

namespace Employee360.Integration.Downstream;

public enum DownstreamOutcome
{
    //Employee 360 accepted the update.
    Success,

    TransientFailure,

    PermanentFailure
}

public sealed record DownstreamResult(DownstreamOutcome Outcome, int? StatusCode, string? Detail)
{
    public static DownstreamResult Succeeded(int? statusCode = null) => new(DownstreamOutcome.Success, statusCode, null);
}

public interface IEmployee360Client
{
    Task<DownstreamResult> SendAsync(Employee360Update update, CancellationToken ct);
}
