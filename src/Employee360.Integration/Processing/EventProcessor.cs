using Employee360.Integration.Downstream;
using Employee360.Integration.Persistence;

namespace Employee360.Integration.Processing;

public enum ProcessingOutcome
{
    Processed,
    Duplicate,
    Superseded,
    DeadLettered
}

public sealed record ProcessingResult(ProcessingOutcome Outcome, string? EventId, string CorrelationId, string Detail);

/// <summary>
/// Handles ONE message end to end. Every message ends in exactly one outcome and this
/// method never throws for bad data, so one bad event cannot stop the ones behind it.
///
/// Order matters:
///   parse -> idempotency check -> validate/transform -> stale check -> downstream -> record.
/// The idempotency check comes before validation so a redelivered *invalid* event does not
/// create a second dead letter.
/// </summary>
public sealed class EventProcessor
{
    private readonly EmployeeEventTransformer _transformer;
    private readonly ProcessingStore _store;
    private readonly IEmployee360Client _client;
    private readonly ILogger<EventProcessor> _logger;

    public EventProcessor(
        EmployeeEventTransformer transformer,
        ProcessingStore store,
        IEmployee360Client client,
        ILogger<EventProcessor> logger)
    {
        _transformer = transformer;
        _store = store;
        _client = client;
        _logger = logger;
    }

    /// <param name="isReplay">True when an operator is deliberately re-running a dead-lettered event.</param>
    public async Task<ProcessingResult> ProcessAsync(string? payload, bool isReplay = false, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        var raw = payload ?? string.Empty;

        // 1. Parse
        var parsed = EventParser.Parse(payload);
        if (!parsed.Success)
            return await DeadLetterAsync(null, null, "Parse", parsed.Error!, raw, correlationId, ct);

        var evt = parsed.Event!;
        var eventId = evt.EventId?.Trim();

        // 2. Idempotency: have we already dealt with this event?
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            var existing = await _store.FindEventAsync(eventId, ct);
            if (existing is not null && IsAlreadyHandled(existing, isReplay))
            {
                await _store.RecordDuplicateAsync(existing, ct);
                _logger.LogInformation(
                    "Duplicate delivery ignored. EventId={EventId} Status={Status} CorrelationId={CorrelationId}",
                    eventId, existing.Status, correlationId);
                return new(ProcessingOutcome.Duplicate, eventId, correlationId,
                    $"Already handled (status {existing.Status}); no action taken.");
            }
        }

        // 3. Validate, normalise, map
        var transformed = _transformer.Transform(evt);
        if (!transformed.Success)
        {
            var reason = string.Join("; ", transformed.Errors.Select(e => $"{e.Code}: {e.Message}"));
            return await DeadLetterAsync(eventId, evt.EmployeeId?.Trim(), "Validation", reason, raw, correlationId, ct);
        }

        var update = transformed.Update!;

        // 4. Claim the event, then reject it if a newer update was already applied
        var record = await _store.RecordAttemptAsync(update.SourceEventId, update.EmployeeId, correlationId, ct);

        var state = await _store.FindEmployeeStateAsync(update.EmployeeId, ct);
        if (state is not null && update.EffectiveFromUtc.UtcDateTime < state.LastEffectiveUtc)
        {
            var detail = $"Effective {update.EffectiveFromUtc:O} is older than already-applied " +
                         $"{state.LastEffectiveUtc:O} (event {state.LastEventId}); not applied.";
            await _store.RecordSupersededAsync(record, detail, ct);
            _logger.LogWarning(
                "Stale event skipped. EventId={EventId} EmployeeId={EmployeeId} CorrelationId={CorrelationId} {Detail}",
                update.SourceEventId, update.EmployeeId, correlationId, detail);
            return new(ProcessingOutcome.Superseded, update.SourceEventId, correlationId, detail);
        }

        // 5. Call downstream
        DownstreamResult result;
        try
        {
            result = await _client.SendAsync(update, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new(DownstreamOutcome.TransientFailure, null, $"Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }

        if (result.Outcome == DownstreamOutcome.Success)
        {
            await _store.RecordSuccessAsync(record, update, ct);
            _logger.LogInformation(
                "Event processed. EventId={EventId} EmployeeId={EmployeeId} CorrelationId={CorrelationId}",
                update.SourceEventId, update.EmployeeId, correlationId);
            return new(ProcessingOutcome.Processed, update.SourceEventId, correlationId, "Delivered to Employee 360.");
        }

        var failure = $"{result.Outcome} (HTTP {result.StatusCode?.ToString() ?? "n/a"}): {result.Detail}";
        return await DeadLetterAsync(update.SourceEventId, update.EmployeeId, "Downstream", failure, raw, correlationId, ct);
    }

    private static bool IsAlreadyHandled(ProcessedEvent existing, bool isReplay) => existing.Status switch
    {
        EventStatus.Succeeded or EventStatus.Superseded => true,
        // A dead-lettered event is only re-run on purpose (replay), never by a plain redelivery.
        EventStatus.DeadLettered => !isReplay,
        // Received = in flight or crashed part-way. Re-running is safe because the downstream
        // call is idempotent (step 3 sends the eventId as an idempotency key).
        _ => false
    };

    private async Task<ProcessingResult> DeadLetterAsync(
        string? eventId, string? employeeId, string stage, string reason,
        string payload, string correlationId, CancellationToken ct)
    {
        var deadLetter = await _store.RecordDeadLetterAsync(eventId, employeeId, stage, reason, payload, correlationId, ct);
        _logger.LogError(
            "Event dead-lettered. DeadLetterId={DeadLetterId} EventId={EventId} Stage={Stage} CorrelationId={CorrelationId} Reason={Reason}",
            deadLetter.Id, eventId, stage, correlationId, reason);
        return new(ProcessingOutcome.DeadLettered, eventId, correlationId, $"{stage}: {reason}");
    }
}
