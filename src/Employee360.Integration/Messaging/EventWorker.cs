using Employee360.Integration.Processing;

namespace Employee360.Integration.Messaging;

/// <summary>
/// Pulls messages off the queue and hands each one to <see cref="EventProcessor"/> in its own DI scope
/// (the processor and its DbContext are scoped; this worker is a singleton).
/// The loop must never die because of one message.
/// </summary>
public sealed class EventWorker : BackgroundService
{
    private readonly IEventQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EventWorker> _logger;

    public EventWorker(IEventQueue queue, IServiceScopeFactory scopes, ILogger<EventWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Event worker started.");

        try
        {
            await foreach (var payload in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<EventProcessor>();
                    await processor.ProcessAsync(payload, isReplay: false, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // shutting down
                }
                catch (Exception ex)
                {
                    // The processor already handles bad data, so reaching here means infrastructure
                    // trouble (e.g. the database is unavailable). Keep the loop alive and keep the
                    // payload in the log. With a real broker the message would be abandoned and
                    // redelivered; with this in-memory queue it is lost, which is a deliberate
                    // simplification.
                    _logger.LogCritical(ex, "Unhandled error processing a message. Payload={Payload}", payload);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }

        _logger.LogInformation("Event worker stopped.");
    }
}
