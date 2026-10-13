using System.Threading.Channels;

namespace Employee360.Integration.Messaging;

public interface IEventQueue
{
    int Depth { get; }

    ValueTask EnqueueAsync(string payload, CancellationToken ct = default);

    IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct);

    void Complete();
}

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
