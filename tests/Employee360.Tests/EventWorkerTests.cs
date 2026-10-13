using Employee360.Integration.Downstream;
using Employee360.Integration.Messaging;
using Employee360.Integration.Persistence;
using Employee360.Integration.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Employee360.Tests;

public class EventWorkerTests
{
    [Fact]
    public async Task BadMessage_DoesNotStopTheWorker_AndLaterMessagesAreProcessed()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var client = new FakeEmployee360Client();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<EmployeeIntegrationDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ProcessingStore>();
        services.AddSingleton(new LocationNormaliser(new Dictionary<string, string> { ["Cape Town"] = "Cape Town" }));
        services.AddSingleton<EmployeeEventTransformer>();
        services.AddSingleton<IEmployee360Client>(client);
        services.AddScoped<EventProcessor>();
        services.AddSingleton<IEventQueue, InMemoryEventQueue>();

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<EmployeeIntegrationDbContext>().Database.EnsureCreated();

        var queue = provider.GetRequiredService<IEventQueue>();
        var worker = new EventWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EventWorker>.Instance);

        // Queue the messages, then close the queue so the worker finishes after draining it.
        // That makes the test deterministic: no sleeping or polling.
        await queue.EnqueueAsync("{ definitely not json");
        await queue.EnqueueAsync(ProcessorHarness.Evt("EVT-AFTER-BAD"));
        queue.Complete();

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(client.Calls);
        using var verifyScope = provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmployeeIntegrationDbContext>();
        Assert.Equal("Parse", db.DeadLetters.Single().Stage);
        Assert.Equal(EventStatus.Succeeded, db.ProcessedEvents.Single(e => e.EventId == "EVT-AFTER-BAD").Status);
    }

    [Fact]
    public async Task Queue_DeliversMessagesInArrivalOrder()
    {
        var queue = new InMemoryEventQueue();
        await queue.EnqueueAsync("first");
        await queue.EnqueueAsync("second");
        await queue.EnqueueAsync("third");
        queue.Complete();

        var received = new List<string>();
        await foreach (var message in queue.ReadAllAsync(CancellationToken.None))
            received.Add(message);

        Assert.Equal(new[] { "first", "second", "third" }, received);
    }
}
