namespace Employee360.Integration.Contracts;

/// <summary>
/// The inbound HR event (System A - D365). Every field is nullable on purpose:
/// we must be able to deserialise a *bad* event so we can report exactly what is wrong
/// with it, instead of failing with an opaque serialisation error.
/// </summary>
public sealed record EmployeeUpdatedEvent(
    string? EventId,
    string? EventType,
    string? EmployeeId,
    string? FirstName,
    string? LastName,
    string? Department,
    string? Location,
    DateTimeOffset? EffectiveDate);
