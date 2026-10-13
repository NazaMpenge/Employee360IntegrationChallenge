using System.Threading.Channels;

namespace Employee360.Integration.Messaging;

/// <summary>
/// Stand-in for Azure Service Bus. The worker depends on this interface only, so swapping in a
/// real broker means writing a new implementation (and moving to peek-lock + abandon/dead-letter),
/// not rewriting the processing logic.
/// </summary>
public interface IEventQueue
{
    /// <summary>Approximate number of messages waiting, or -1 if the queue cannot say.</summary>
    int Depth { get; }

    ValueTask EnqueueAsync(string payload, CancellationToken ct = default);

    IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct);

    /// <summary>No more messages will arrive; readers finish once the queue is drained.</summary>
    void Complete();
}

/// <summary>
/// A single-reader channel: messages are handled strictly in arrival order, which keeps the
/// stale-event logic simple for this exercise. Payloads are raw strings on purpose - the queue
/// must accept malformed messages so the processor can dead-letter them rather than lose them.
/// </summary>
public sealed class InMemoryEventQueue : IEventQueue
{
    private readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public int Depth => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    public ValueTask EnqueueAsync(string payload, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(payload, ct);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.TryComplete();
}
