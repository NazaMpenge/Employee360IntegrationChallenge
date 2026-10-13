using Employee360.Integration.Contracts;

namespace Employee360.Integration.Processing;

public sealed record TransformResult(Employee360Update? Update, IReadOnlyList<ValidationError> Errors)
{
    public bool Success => Update is not null;

    public static TransformResult Ok(Employee360Update update) => new(update, Array.Empty<ValidationError>());
    public static TransformResult Fail(IReadOnlyList<ValidationError> errors) => new(null, errors);
}

/// <summary>
/// Validate -> normalise -> map. Pure and side-effect free, so it is trivial to unit test.
/// </summary>
public sealed class EmployeeEventTransformer
{
    public const string SourceSystem = "D365";

    private readonly LocationNormaliser _locations;

    public EmployeeEventTransformer(LocationNormaliser locations) => _locations = locations;

    public TransformResult Transform(EmployeeUpdatedEvent evt)
    {
        var errors = EventValidator.Validate(evt).ToList();

        // Only attempt normalisation if a location was actually supplied;
        // a missing location is already reported by the validator.
        string? location = null;
        if (!string.IsNullOrWhiteSpace(evt.Location) && !_locations.TryNormalise(evt.Location, out location))
        {
            errors.Add(new("UnknownLocation",
                $"location '{evt.Location}' is not a recognised location or alias."));
        }

        if (errors.Count > 0)
            return TransformResult.Fail(errors);

        var first = evt.FirstName!.Trim();
        var last = evt.LastName!.Trim();

        return TransformResult.Ok(new Employee360Update(
            EmployeeId: evt.EmployeeId!.Trim(),
            FirstName: first,
            LastName: last,
            DisplayName: $"{first} {last}",
            Department: evt.Department!.Trim(),
            Location: location!,
            EffectiveFromUtc: evt.EffectiveDate!.Value.ToUniversalTime(),
            SourceEventId: evt.EventId!.Trim(),
            SourceSystem: SourceSystem));
    }
}
