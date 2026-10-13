namespace Employee360.Integration.Persistence;

public enum EventStatus
{
    /// <summary>Claimed and in flight (or the process crashed mid-way; safe to retry).</summary>
    Received,

    /// <summary>Delivered to Employee 360. Terminal - further deliveries are ignored.</summary>
    Succeeded,

    /// <summary>A newer update for the same employee had already been applied. Terminal.</summary>
    Superseded,

    /// <summary>Could not be processed; a row exists in the dead-letter store.</summary>
    DeadLettered
}

/// <summary>
/// The idempotency record: one row per eventId. Its existence (and status) is how we
/// know an event has already been handled.
/// </summary>
public class ProcessedEvent
{
    public string EventId { get; set; } = default!;
    public string? EmployeeId { get; set; }
    public EventStatus Status { get; set; }
    public int Attempts { get; set; }
    public int DuplicatesIgnored { get; set; }
    public string? CorrelationId { get; set; }
    public string? Detail { get; set; }
    public DateTime FirstReceivedUtc { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}

/// <summary>
/// The latest effective date we have applied per employee. Used to reject an older
/// event that arrives after a newer one.
/// </summary>
public class EmployeeState
{
    public string EmployeeId { get; set; } = default!;
    public DateTime LastEffectiveUtc { get; set; }
    public string LastEventId { get; set; } = default!;
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// A failed event with everything needed to understand and replay it: the raw payload,
/// the stage it failed at, the reason, and a correlation id that appears in the logs.
/// </summary>
public class DeadLetter
{
    public Guid Id { get; set; }
    public string? EventId { get; set; }
    public string? EmployeeId { get; set; }
    public string Stage { get; set; } = default!;
    public string Reason { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public string? CorrelationId { get; set; }
    public int Attempts { get; set; }
    public DateTime FailedUtc { get; set; }
    public bool Replayed { get; set; }
    public DateTime? ReplayedUtc { get; set; }
}
