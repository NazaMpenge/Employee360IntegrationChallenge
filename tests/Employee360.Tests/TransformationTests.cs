using Employee360.Integration.Contracts;
using Employee360.Integration.Processing;

namespace Employee360.Tests;

public class TransformationTests
{
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["CPT"] = "Cape Town",
        ["Cape Town"] = "Cape Town",
        ["JNB"] = "Johannesburg",
    };

    private static EmployeeEventTransformer CreateTransformer() =>
        new(new LocationNormaliser(Aliases));

    private static EmployeeUpdatedEvent ValidEvent(
        string? eventId = "EVT-10452-001",
        string? employeeId = "EMP10452",
        string? location = "Cape Town") =>
        new(eventId, "EmployeeUpdated", employeeId, "Amira", "Khan", "Data & AI", location,
            DateTimeOffset.Parse("2026-10-01T08:30:00Z"));

    // ---- Parsing -----------------------------------------------------------------

    [Fact]
    public void Parse_ValidJson_ReturnsEvent()
    {
        const string json = """
        {
          "eventId": "EVT-10452-001", "eventType": "EmployeeUpdated", "employeeId": "EMP10452",
          "firstName": "Amira", "lastName": "Khan", "department": "Data & AI",
          "location": "Cape Town", "effectiveDate": "2026-10-01T08:30:00Z"
        }
        """;

        var result = EventParser.Parse(json);

        Assert.True(result.Success);
        Assert.Equal("EMP10452", result.Event!.EmployeeId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T08:30:00Z"), result.Event.EffectiveDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{ "eventId": "E1", "effectiveDate": "not-a-date" }""")]
    public void Parse_MalformedPayload_FailsWithoutThrowing(string payload)
    {
        var result = EventParser.Parse(payload);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ---- Validation --------------------------------------------------------------

    [Fact]
    public void Validate_MissingEmployeeId_ReturnsMissingEmployeeIdError()
    {
        var result = CreateTransformer().Transform(ValidEvent(employeeId: null));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == "MissingEmployeeId");
    }

    [Fact]
    public void Validate_ReportsAllProblemsAtOnce()
    {
        var evt = new EmployeeUpdatedEvent(null, "SomethingElse", "", null, null, null, null, null);

        var errors = EventValidator.Validate(evt);

        var codes = errors.Select(e => e.Code).ToHashSet();
        Assert.Contains("MissingEventId", codes);
        Assert.Contains("UnsupportedEventType", codes);
        Assert.Contains("MissingEmployeeId", codes);
        Assert.Contains("MissingEffectiveDate", codes);
    }

    [Fact]
    public void Validate_BadEmployeeIdFormat_IsRejected()
    {
        var result = CreateTransformer().Transform(ValidEvent(employeeId: "10452; DROP TABLE"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == "InvalidEmployeeId");
    }

    // ---- Normalisation -----------------------------------------------------------

    [Theory]
    [InlineData("CPT", "Cape Town")]
    [InlineData("cpt", "Cape Town")]
    [InlineData("  Cape Town ", "Cape Town")]
    [InlineData("cape   town", "Cape Town")]
    [InlineData("JNB", "Johannesburg")]
    public void Normalise_KnownRepresentations_MapToCanonicalName(string raw, string expected)
    {
        var normaliser = new LocationNormaliser(Aliases);

        Assert.True(normaliser.TryNormalise(raw, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Transform_UnknownLocation_IsQuarantinedNotPassedThrough()
    {
        var result = CreateTransformer().Transform(ValidEvent(location: "Atlantis"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == "UnknownLocation");
    }

    // ---- Mapping -----------------------------------------------------------------

    [Fact]
    public void Transform_ValidEvent_MapsToDownstreamContract()
    {
        var result = CreateTransformer().Transform(ValidEvent(location: "CPT"));

        Assert.True(result.Success);
        var update = result.Update!;
        Assert.Equal("EMP10452", update.EmployeeId);
        Assert.Equal("Amira Khan", update.DisplayName);
        Assert.Equal("Cape Town", update.Location);
        Assert.Equal("EVT-10452-001", update.SourceEventId);
        Assert.Equal("D365", update.SourceSystem);
        Assert.Equal(TimeSpan.Zero, update.EffectiveFromUtc.Offset);
    }
}
