using Employee360.Integration.Messaging;
using Employee360.Integration.Persistence;
using Employee360.Integration.Processing;

namespace Employee360.Integration.Api;

public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        // ---- Operate: health, publish ---------------------------------------------------------

        app.MapGet("/health", async (ProcessingStore store, IEventQueue queue, CancellationToken ct) =>
            Results.Ok(new
            {
                status = "Healthy",
                queueDepth = queue.Depth,
                eventsByStatus = await store.GetStatusCountsAsync(ct),
                openDeadLetters = await store.CountOpenDeadLettersAsync(ct)
            }));

        // Accepts the RAW body (not a bound model) so malformed payloads reach the pipeline
        // and get dead-lettered instead of being rejected with a 400 and lost.
        app.MapPost("/events", async (HttpRequest request, IEventQueue queue, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            await queue.EnqueueAsync(payload, ct);
            return Results.Accepted(value: new { queued = true, queueDepth = queue.Depth });
        });

        app.MapPost("/samples/publish", async (SampleCatalog samples, IEventQueue queue, CancellationToken ct) =>
        {
            var published = new List<string>();
            foreach (var name in samples.SequenceFiles())
            {
                if (!samples.TryRead(name, out var payload)) continue;
                await queue.EnqueueAsync(payload, ct);
                published.Add(name);
            }

            return Results.Accepted(value: new { published });
        });

        app.MapPost("/samples/publish/{name}", async (string name, SampleCatalog samples, IEventQueue queue, CancellationToken ct) =>
        {
            if (!samples.TryRead(name, out var payload))
                return Results.NotFound(new { error = $"No sample named '{name}'." });

            await queue.EnqueueAsync(payload, ct);
            return Results.Accepted(value: new { published = name });
        });

        // ---- Inspect: what happened to an event / an employee ----------------------------------

        app.MapGet("/events/{eventId}", async (string eventId, ProcessingStore store, CancellationToken ct) =>
        {
            var ev = await store.FindEventAsync(eventId, ct);
            if (ev is null)
                return Results.NotFound(new { error = $"No record of event '{eventId}'." });

            var deadLetters = await store.ListDeadLettersForEventAsync(eventId, ct);

            return Results.Ok(new
            {
                eventId = ev.EventId,
                employeeId = ev.EmployeeId,
                status = ev.Status.ToString(),
                attempts = ev.Attempts,
                duplicatesIgnored = ev.DuplicatesIgnored,
                detail = ev.Detail,
                correlationId = ev.CorrelationId,
                firstReceivedUtc = ev.FirstReceivedUtc,
                lastUpdatedUtc = ev.LastUpdatedUtc,
                deadLetters = deadLetters.Select(ToSummary)
            });
        });

        app.MapGet("/employees/{employeeId}", async (string employeeId, ProcessingStore store, CancellationToken ct) =>
        {
            var state = await store.FindEmployeeStateAsync(employeeId, ct);
            var events = await store.ListEventsForEmployeeAsync(employeeId, ct);

            if (state is null && events.Count == 0)
                return Results.NotFound(new { error = $"No record of employee '{employeeId}'." });

            return Results.Ok(new
            {
                employeeId,
                lastAppliedEffectiveUtc = state?.LastEffectiveUtc,
                lastAppliedEventId = state?.LastEventId,
                lastUpdatedUtc = state?.UpdatedUtc,
                events = events.Select(e => new
                {
                    eventId = e.EventId,
                    status = e.Status.ToString(),
                    attempts = e.Attempts,
                    duplicatesIgnored = e.DuplicatesIgnored,
                    detail = e.Detail
                })
            });
        });

        // ---- Operate: dead letters and replay --------------------------------------------------

        app.MapGet("/deadletters", async (bool? includeReplayed, ProcessingStore store, CancellationToken ct) =>
        {
            var items = await store.ListDeadLettersAsync(includeReplayed ?? false, ct);
            return Results.Ok(items.Select(d => new
            {
                id = d.Id,
                eventId = d.EventId,
                employeeId = d.EmployeeId,
                stage = d.Stage,
                reason = d.Reason,
                attempts = d.Attempts,
                correlationId = d.CorrelationId,
                failedUtc = d.FailedUtc,
                replayed = d.Replayed,
                payload = d.Payload
            }));
        });

        // Re-runs the stored payload through the normal pipeline (same idempotency and stale checks).
        app.MapPost("/deadletters/{id:guid}/replay", async (
            Guid id, ProcessingStore store, EventProcessor processor, CancellationToken ct) =>
        {
            var deadLetter = await store.FindDeadLetterAsync(id, ct);
            if (deadLetter is null)
                return Results.NotFound(new { error = $"No dead letter '{id}'." });

            if (deadLetter.Replayed)
                return Results.Conflict(new { error = "This dead letter has already been replayed." });

            var result = await processor.ProcessAsync(deadLetter.Payload, isReplay: true, ct);

            // Anything other than "failed again" means the problem is resolved (processed, or it turned
            // out the work was already done / superseded). A failed replay stays open and writes a new
            // dead letter describing why it failed this time.
            var resolved = result.Outcome != ProcessingOutcome.DeadLettered;
            if (resolved)
                await store.MarkDeadLetterReplayedAsync(id, ct);

            return Results.Ok(new
            {
                deadLetterId = id,
                outcome = result.Outcome.ToString(),
                detail = result.Detail,
                correlationId = result.CorrelationId,
                markedReplayed = resolved
            });
        });
    }

    private static object ToSummary(DeadLetter d) => new
    {
        id = d.Id,
        stage = d.Stage,
        reason = d.Reason,
        failedUtc = d.FailedUtc,
        replayed = d.Replayed
    };
}
