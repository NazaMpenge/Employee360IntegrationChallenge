namespace Employee360.Integration.Downstream;

public sealed class Employee360ApiOptions
{
    public const string SectionName = "Employee360Api";

    public string BaseUrl { get; set; } = "http://localhost:5100";

    public string? ApiKey { get; set; }

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
