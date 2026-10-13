using Employee360.Integration.Processing;

namespace Employee360.Integration.Messaging;

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
                    break; 
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Unhandled error processing a message. Payload={Payload}", payload);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {

        }

        _logger.LogInformation("Event worker stopped.");
    }
}
