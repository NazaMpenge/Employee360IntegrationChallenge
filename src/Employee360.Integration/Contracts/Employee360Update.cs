namespace Employee360.Integration.Contracts;

/// <summary>
/// The trusted downstream contract (System B - Employee 360).
/// Deliberately a separate type from the inbound event so the two systems can evolve
/// independently and we never leak source-system quirks downstream.
/// </summary>
public sealed record Employee360Update(
    string EmployeeId,
    string FirstName,
    string LastName,
    string DisplayName,
    string Department,
    string Location,
    DateTimeOffset EffectiveFromUtc,
    string SourceEventId,
    string SourceSystem);
