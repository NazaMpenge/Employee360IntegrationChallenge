using Employee360.Integration.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Persistence;

/// <summary>
/// All reads/writes of integration state in one place. Each method saves exactly once,
/// so the idempotency record and the employee state change together or not at all.
/// </summary>
public sealed class ProcessingStore
{
    private readonly EmployeeIntegrationDbContext _db;
    private readonly TimeProvider _time;

    public ProcessingStore(EmployeeIntegrationDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public Task<ProcessedEvent?> FindEventAsync(string eventId, CancellationToken ct) =>
        _db.ProcessedEvents.FirstOrDefaultAsync(e => e.EventId == eventId, ct);

    public Task<EmployeeState?> FindEmployeeStateAsync(string employeeId, CancellationToken ct) =>
        _db.EmployeeStates.FirstOrDefaultAsync(s => s.EmployeeId == employeeId, ct);

    /// <summary>Claims the event (or re-claims it on a retry/replay) and counts the attempt.</summary>
    public async Task<ProcessedEvent> RecordAttemptAsync(
        string eventId, string? employeeId, string correlationId, CancellationToken ct)
    {
        var ev = await FindEventAsync(eventId, ct);
        if (ev is null)
        {
            ev = new ProcessedEvent { EventId = eventId, FirstReceivedUtc = Now };
            _db.ProcessedEvents.Add(ev);
        }

        ev.EmployeeId = employeeId;
        ev.Status = EventStatus.Received;
        ev.Attempts++;
        ev.CorrelationId = correlationId;
        ev.Detail = null;
        ev.LastUpdatedUtc = Now;

        await _db.SaveChangesAsync(ct);
        return ev;
    }

    /// <summary>Marks the event delivered and advances the employee's high-water mark atomically.</summary>
    public async Task RecordSuccessAsync(ProcessedEvent ev, Employee360Update update, CancellationToken ct)
    {
        ev.Status = EventStatus.Succeeded;
        ev.Detail = "Delivered to Employee 360.";
        ev.LastUpdatedUtc = Now;

        var state = await FindEmployeeStateAsync(update.EmployeeId, ct);
        if (state is null)
        {
            state = new EmployeeState { EmployeeId = update.EmployeeId };
            _db.EmployeeStates.Add(state);
        }

        state.LastEffectiveUtc = update.EffectiveFromUtc.UtcDateTime;
        state.LastEventId = update.SourceEventId;
        state.UpdatedUtc = Now;

        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordSupersededAsync(ProcessedEvent ev, string detail, CancellationToken ct)
    {
        ev.Status = EventStatus.Superseded;
        ev.Detail = detail;
        ev.LastUpdatedUtc = Now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordDuplicateAsync(ProcessedEvent ev, CancellationToken ct)
    {
        ev.DuplicatesIgnored++;
        ev.LastUpdatedUtc = Now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes a dead-letter row. If the event has a usable id, its idempotency record is
    /// also set to DeadLettered so a redelivery does not create a second dead letter.
    /// </summary>
    public async Task<DeadLetter> RecordDeadLetterAsync(
        string? eventId, string? employeeId, string stage, string reason,
        string payload, string correlationId, CancellationToken ct)
    {
        ProcessedEvent? ev = null;
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            ev = await FindEventAsync(eventId, ct);
            if (ev is null)
            {
                ev = new ProcessedEvent { EventId = eventId, FirstReceivedUtc = Now };
                _db.ProcessedEvents.Add(ev);
            }

            ev.EmployeeId ??= employeeId;
            ev.Status = EventStatus.DeadLettered;
            ev.Detail = $"{stage}: {reason}";
            ev.CorrelationId = correlationId;
            ev.LastUpdatedUtc = Now;
        }

        var deadLetter = new DeadLetter
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            EmployeeId = employeeId,
            Stage = stage,
            Reason = reason,
            Payload = payload,
            CorrelationId = correlationId,
            Attempts = ev?.Attempts ?? 0,
            FailedUtc = Now
        };
        _db.DeadLetters.Add(deadLetter);

        await _db.SaveChangesAsync(ct);
        return deadLetter;
    }

    // ---- Read side (used by the inspection endpoints) ---------------------------------------

    public Task<List<ProcessedEvent>> ListEventsForEmployeeAsync(string employeeId, CancellationToken ct) =>
        _db.ProcessedEvents.AsNoTracking()
            .Where(e => e.EmployeeId == employeeId)
            .OrderBy(e => e.FirstReceivedUtc)
            .ToListAsync(ct);

    public Task<List<DeadLetter>> ListDeadLettersForEventAsync(string eventId, CancellationToken ct) =>
        _db.DeadLetters.AsNoTracking()
            .Where(d => d.EventId == eventId)
            .OrderBy(d => d.FailedUtc)
            .ToListAsync(ct);

    public async Task<List<DeadLetter>> ListDeadLettersAsync(bool includeReplayed, CancellationToken ct)
    {
        var query = _db.DeadLetters.AsNoTracking();
        if (!includeReplayed) query = query.Where(d => !d.Replayed);
        return await query.OrderByDescending(d => d.FailedUtc).Take(200).ToListAsync(ct);
    }

    public Task<DeadLetter?> FindDeadLetterAsync(Guid id, CancellationToken ct) =>
        _db.DeadLetters.FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<int> CountOpenDeadLettersAsync(CancellationToken ct) =>
        _db.DeadLetters.CountAsync(d => !d.Replayed, ct);

    public async Task<Dictionary<string, int>> GetStatusCountsAsync(CancellationToken ct)
    {
        // Grouped in memory: the table is small here, and it avoids relying on SQL translation
        // of a column that has a value converter.
        var statuses = await _db.ProcessedEvents.AsNoTracking().Select(e => e.Status).ToListAsync(ct);
        return statuses.GroupBy(s => s.ToString()).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<bool> MarkDeadLetterReplayedAsync(Guid id, CancellationToken ct)
    {
        var deadLetter = await _db.DeadLetters.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (deadLetter is null) return false;

        deadLetter.Replayed = true;
        deadLetter.ReplayedUtc = Now;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
