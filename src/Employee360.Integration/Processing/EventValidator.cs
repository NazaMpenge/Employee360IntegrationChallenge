using System.Text.RegularExpressions;
using Employee360.Integration.Contracts;

namespace Employee360.Integration.Processing;

public sealed record ValidationError(string Code, string Message);

/// <summary>
/// Structural validation of an inbound event. Returns *all* problems, not just the first,
/// so a dead-lettered event tells the operator everything that is wrong in one go.
/// </summary>
public static class EventValidator
{
    public const string ExpectedEventType = "EmployeeUpdated";

    // Assumption: employee IDs look like EMP10452. Anything else is treated as unsafe
    // rather than guessed at, because the ID is the key we update downstream.
    private static readonly Regex EmployeeIdPattern = new(@"^EMP\d+$", RegexOptions.Compiled);

    public static IReadOnlyList<ValidationError> Validate(EmployeeUpdatedEvent evt)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(evt.EventId))
            errors.Add(new("MissingEventId", "eventId is required."));

        if (!string.Equals(evt.EventType, ExpectedEventType, StringComparison.Ordinal))
            errors.Add(new("UnsupportedEventType",
                $"eventType must be '{ExpectedEventType}' but was '{evt.EventType ?? "<null>"}'."));

        if (string.IsNullOrWhiteSpace(evt.EmployeeId))
            errors.Add(new("MissingEmployeeId", "employeeId is required."));
        else if (!EmployeeIdPattern.IsMatch(evt.EmployeeId.Trim()))
            errors.Add(new("InvalidEmployeeId",
                $"employeeId '{evt.EmployeeId}' does not match the expected format EMP<digits>."));

        if (string.IsNullOrWhiteSpace(evt.FirstName))
            errors.Add(new("MissingFirstName", "firstName is required."));

        if (string.IsNullOrWhiteSpace(evt.LastName))
            errors.Add(new("MissingLastName", "lastName is required."));

        if (string.IsNullOrWhiteSpace(evt.Department))
            errors.Add(new("MissingDepartment", "department is required."));

        if (string.IsNullOrWhiteSpace(evt.Location))
            errors.Add(new("MissingLocation", "location is required."));

        if (evt.EffectiveDate is null || evt.EffectiveDate.Value == default)
            errors.Add(new("MissingEffectiveDate", "effectiveDate is required."));

        return errors;
    }
}
