using System.Text.Json;
using Employee360.Integration.Contracts;
using Employee360.Integration.Downstream;
using Employee360.Integration.Persistence;
using Employee360.Integration.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Employee360.Tests;

/// <summary>Scriptable stand-in for Employee 360. Records every call so tests can assert on side effects.</summary>
public sealed class FakeEmployee360Client : IEmployee360Client
{
    private readonly Queue<DownstreamResult> _script = new();

    public List<Employee360Update> Calls { get; } = new();

    public void Enqueue(DownstreamResult result) => _script.Enqueue(result);

    public Task<DownstreamResult> SendAsync(Employee360Update update, CancellationToken ct)
    {
        Calls.Add(update);
        return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : DownstreamResult.Succeeded(200));
    }
}

/// <summary>Real EF Core + real SQLite (in memory), so idempotency is tested against the actual database.</summary>
public sealed class ProcessorHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public EmployeeIntegrationDbContext Db { get; }
    public FakeEmployee360Client Client { get; } = new();
    public EventProcessor Processor { get; }

    public ProcessorHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<EmployeeIntegrationDbContext>().UseSqlite(_connection).Options;
        Db = new EmployeeIntegrationDbContext(options);
        Db.Database.EnsureCreated();

        var normaliser = new LocationNormaliser(new Dictionary<string, string>
        {
            ["CPT"] = "Cape Town",
            ["Cape Town"] = "Cape Town",
        });

        Processor = new EventProcessor(
            new EmployeeEventTransformer(normaliser),
            new ProcessingStore(Db, TimeProvider.System),
            Client,
            NullLogger<EventProcessor>.Instance);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }

    public static string Evt(
        string eventId = "EVT-1",
        string? employeeId = "EMP10452",
        string location = "Cape Town",
        string effectiveDate = "2026-10-01T08:30:00Z") =>
        JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "EmployeeUpdated",
            employeeId,
            firstName = "Amira",
            lastName = "Khan",
            department = "Data & AI",
            location,
            effectiveDate
        });
}

public class EventProcessorTests
{
    [Fact]
    public async Task ValidEvent_IsSentDownstreamOnce_AndRecordedAsSucceeded()
    {
        using var h = new ProcessorHarness();

        var result = await h.Processor.ProcessAsync(ProcessorHarness.Evt(location: "CPT"));

        Assert.Equal(ProcessingOutcome.Processed, result.Outcome);
        var sent = Assert.Single(h.Client.Calls);
        Assert.Equal("Cape Town", sent.Location); // CPT normalised before it left the building

        var record = h.Db.ProcessedEvents.Single(e => e.EventId == "EVT-1");
        Assert.Equal(EventStatus.Succeeded, record.Status);
        Assert.Equal("EMP10452", h.Db.EmployeeStates.Single().EmployeeId);
    }

    [Fact]
    public async Task SameEventDeliveredTwice_CallsDownstreamOnlyOnce()
    {
        using var h = new ProcessorHarness();
        var payload = ProcessorHarness.Evt();

        var first = await h.Processor.ProcessAsync(payload);
        var second = await h.Processor.ProcessAsync(payload);
        var third = await h.Processor.ProcessAsync(payload);

        Assert.Equal(ProcessingOutcome.Processed, first.Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, second.Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, third.Outcome);
        Assert.Single(h.Client.Calls);
        Assert.Equal(2, h.Db.ProcessedEvents.Single().DuplicatesIgnored);
    }

    [Fact]
    public async Task OlderEventArrivingAfterNewer_IsSuperseded_AndNotSentDownstream()
    {
        using var h = new ProcessorHarness();

        await h.Processor.ProcessAsync(ProcessorHarness.Evt("EVT-NEW", effectiveDate: "2026-10-05T08:00:00Z"));
        var stale = await h.Processor.ProcessAsync(ProcessorHarness.Evt("EVT-OLD", effectiveDate: "2026-10-01T08:00:00Z"));

        Assert.Equal(ProcessingOutcome.Superseded, stale.Outcome);
        Assert.Single(h.Client.Calls); // only the newer one went downstream
        Assert.Equal(EventStatus.Superseded, h.Db.ProcessedEvents.Single(e => e.EventId == "EVT-OLD").Status);
        Assert.Equal("EVT-NEW", h.Db.EmployeeStates.Single().LastEventId); // state was not rolled back
    }

    [Fact]
    public async Task MalformedPayload_IsDeadLettered_AndNextEventStillProcesses()
    {
        using var h = new ProcessorHarness();

        var bad = await h.Processor.ProcessAsync("{ this is not json");
        var good = await h.Processor.ProcessAsync(ProcessorHarness.Evt("EVT-GOOD"));

        Assert.Equal(ProcessingOutcome.DeadLettered, bad.Outcome);
        Assert.Equal(ProcessingOutcome.Processed, good.Outcome);

        var deadLetter = h.Db.DeadLetters.Single();
        Assert.Equal("Parse", deadLetter.Stage);
        Assert.Equal("{ this is not json", deadLetter.Payload); // raw payload kept for replay
    }

    [Fact]
    public async Task MissingEmployeeId_IsDeadLettered_WithReason_AndNothingSentDownstream()
    {
        using var h = new ProcessorHarness();

        var result = await h.Processor.ProcessAsync(ProcessorHarness.Evt("EVT-NOID", employeeId: null));

        Assert.Equal(ProcessingOutcome.DeadLettered, result.Outcome);
        Assert.Empty(h.Client.Calls);

        var deadLetter = h.Db.DeadLetters.Single();
        Assert.Equal("Validation", deadLetter.Stage);
        Assert.Contains("MissingEmployeeId", deadLetter.Reason);
        Assert.Equal("EVT-NOID", deadLetter.EventId);
    }

    [Fact]
    public async Task InvalidEventRedelivered_DoesNotCreateSecondDeadLetter()
    {
        using var h = new ProcessorHarness();
        var payload = ProcessorHarness.Evt("EVT-NOID", employeeId: null);

        await h.Processor.ProcessAsync(payload);
        var again = await h.Processor.ProcessAsync(payload);

        Assert.Equal(ProcessingOutcome.Duplicate, again.Outcome);
        Assert.Single(h.Db.DeadLetters);
    }

    [Fact]
    public async Task PermanentDownstreamFailure_IsDeadLetteredWithPayload()
    {
        using var h = new ProcessorHarness();
        h.Client.Enqueue(new(DownstreamOutcome.PermanentFailure, 400, "Rejected by Employee 360"));
        var payload = ProcessorHarness.Evt();

        var result = await h.Processor.ProcessAsync(payload);

        Assert.Equal(ProcessingOutcome.DeadLettered, result.Outcome);
        var deadLetter = h.Db.DeadLetters.Single();
        Assert.Equal("Downstream", deadLetter.Stage);
        Assert.Contains("400", deadLetter.Reason);
        Assert.Equal(payload, deadLetter.Payload);
        Assert.Empty(h.Db.EmployeeStates); // a failed update must not advance the high-water mark
    }

    [Fact]
    public async Task DeadLetteredEvent_IsIgnoredOnRedelivery_ButReprocessedOnReplay()
    {
        using var h = new ProcessorHarness();
        h.Client.Enqueue(new(DownstreamOutcome.TransientFailure, 503, "Employee 360 unavailable"));
        var payload = ProcessorHarness.Evt();

        var first = await h.Processor.ProcessAsync(payload);
        var redelivered = await h.Processor.ProcessAsync(payload);
        var replayed = await h.Processor.ProcessAsync(payload, isReplay: true);

        Assert.Equal(ProcessingOutcome.DeadLettered, first.Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, redelivered.Outcome);
        Assert.Equal(ProcessingOutcome.Processed, replayed.Outcome);
        Assert.Equal(2, h.Client.Calls.Count); // the failed attempt + the successful replay
        Assert.Equal(EventStatus.Succeeded, h.Db.ProcessedEvents.Single().Status);
    }

    [Fact]
    public async Task ClientThrowing_IsTreatedAsTransientFailure_NotCrash()
    {
        using var h = new ProcessorHarness();
        var processor = new EventProcessor(
            new EmployeeEventTransformer(new LocationNormaliser(new Dictionary<string, string> { ["Cape Town"] = "Cape Town" })),
            new ProcessingStore(h.Db, TimeProvider.System),
            new ThrowingClient(),
            NullLogger<EventProcessor>.Instance);

        var result = await processor.ProcessAsync(ProcessorHarness.Evt());

        Assert.Equal(ProcessingOutcome.DeadLettered, result.Outcome);
        Assert.Contains("Unexpected error", h.Db.DeadLetters.Single().Reason);
    }

    private sealed class ThrowingClient : IEmployee360Client
    {
        public Task<DownstreamResult> SendAsync(Employee360Update update, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
