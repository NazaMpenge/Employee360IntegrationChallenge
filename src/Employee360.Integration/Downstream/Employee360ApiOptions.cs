namespace Employee360.Integration.Downstream;

/// <summary>
/// Bound from configuration section "Employee360Api". Nothing here is hard-coded in the
/// client; the ApiKey in particular must come from user-secrets / environment variables
/// locally and Key Vault (via managed identity) in Azure - never from source control.
/// </summary>
public sealed class Employee360ApiOptions
{
    public const string SectionName = "Employee360Api";

    public string BaseUrl { get; set; } = "http://localhost:5100";

    /// <summary>Optional. Sent as X-Api-Key when set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Retries after the first attempt, so total attempts = 1 + MaxRetryAttempts. Must be at least 1.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>First retry delay. Doubles each retry, with jitter so retries from many events do not synchronise.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Timeout applied to EACH attempt, not to the whole operation.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
