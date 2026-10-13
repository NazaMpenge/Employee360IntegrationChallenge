using System.Text.Json;
using Employee360.Integration.Contracts;

namespace Employee360.Integration.Processing;

public sealed record ParseResult(EmployeeUpdatedEvent? Event, string? Error)
{
    public bool Success => Event is not null;

    public static ParseResult Ok(EmployeeUpdatedEvent evt) => new(evt, null);
    public static ParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Turns a raw message body into an <see cref="EmployeeUpdatedEvent"/>.
/// Never throws for bad input: a malformed payload is a normal, expected outcome
/// that the pipeline routes to the dead-letter store.
/// </summary>
public static class EventParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ParseResult Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return ParseResult.Fail("Payload is empty.");

        try
        {
            var evt = JsonSerializer.Deserialize<EmployeeUpdatedEvent>(payload, Options);
            return evt is null
                ? ParseResult.Fail("Payload deserialised to null.")
                : ParseResult.Ok(evt);
        }
        catch (JsonException ex)
        {
            return ParseResult.Fail($"Malformed JSON: {ex.Message}");
        }
    }
}
