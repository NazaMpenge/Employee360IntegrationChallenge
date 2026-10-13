using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Employee360.Integration.Contracts;
using Employee360.Integration.Downstream;
using Employee360.Integration.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Employee360.Tests;

/// <summary>
/// Stand-in for the Employee 360 HTTP API. Records every update it is sent, can be told to fail the
/// next N calls, and refuses EMP66666 permanently - the same behaviour as the mock API project.
/// </summary>
public sealed class E2EClient : IEmployee360Client
{
    private int _failuresRemaining;

    public ConcurrentQueue<Employee360Update> Calls { get; } = new();

    public void FailNext(int count) => Interlocked.Exchange(ref _failuresRemaining, count);

    public Task<DownstreamResult> SendAsync(Employee360Update update, CancellationToken ct)
    {
        Calls.Enqueue(update);

        if (update.EmployeeId == "EMP66666")
            return Task.FromResult(new DownstreamResult(
                DownstreamOutcome.PermanentFailure, 422, "Employee rejected by master data rules"));

        if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            return Task.FromResult(new DownstreamResult(
                DownstreamOutcome.TransientFailure, 503, "Simulated Employee 360 outage"));

        return Task.FromResult(DownstreamResult.Succeeded(200));
    }
}

/// <summary>
/// Boots the REAL application in memory (real DI, queue, background worker, endpoints, EF Core and
/// SQLite) and swaps only the two things that touch the outside world: the database file and the
/// downstream HTTP client. Each test gets its own instance, so tests cannot affect each other.
/// </summary>
public sealed class IntegrationAppFactory : WebApplicationFactory<Program>
{
    // A file database rather than in-memory: the worker and the API read/write at the same time,
    // and a file in WAL mode handles that the way the real app does.
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.db");

    public E2EClient Downstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<EmployeeIntegrationDbContext>>();
            services.AddDbContext<EmployeeIntegrationDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));

            services.RemoveAll<IEmployee360Client>();
            services.AddSingleton<IEmployee360Client>(Downstream);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* best effort cleanup */ }
        }
    }
}

public class EndToEndTests
{
    // ---- helpers ------------------------------------------------------------------------------

    private static StringContent Body(string payload) => new(payload, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The event's current status, or null while the worker has not recorded it yet.</summary>
    private static async Task<string?> StatusOfAsync(HttpClient client, string eventId)
    {
        var response = await client.GetAsync($"/events/{eventId}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("status").GetString();
    }

    // The worker is asynchronous, so tests wait for an observable outcome instead of sleeping blindly.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {description}");
            await Task.Delay(25);
        }
    }

    // ---- tests --------------------------------------------------------------------------------

    [Fact]
    public async Task PostedEvent_IsProcessedByTheWorker_AndVisibleThroughTheApi()
    {
        using var factory = new IntegrationAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/events", Body(ProcessorHarness.Evt("EVT-E2E-1", location: "CPT")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await WaitUntilAsync(async () => await StatusOfAsync(client, "EVT-E2E-1") == "Succeeded", "EVT-E2E-1 to succeed");

        var ev = await GetJsonAsync(client, "/events/EVT-E2E-1");
        Assert.Equal(1, ev.GetProperty("attempts").GetInt32());
        Assert.Equal(0, ev.GetProperty("duplicatesIgnored").GetInt32());

        var employee = await GetJsonAsync(client, "/employees/EMP10452");
        Assert.Equal("EVT-E2E-1", employee.GetProperty("lastAppliedEventId").GetString());

        var sent = Assert.Single(factory.Downstream.Calls);
        Assert.Equal("Cape Town", sent.Location); // CPT was normalised on the way through
    }

    [Fact]
    public async Task SameEventPostedTwice_IsDeliveredDownstreamOnce_AndTheDuplicateIsCounted()
    {
        using var factory = new IntegrationAppFactory();
        using var client = factory.CreateClient();
        var payload = ProcessorHarness.Evt("EVT-E2E-DUP");

        await client.PostAsync("/events", Body(payload));
        await WaitUntilAsync(async () => await StatusOfAsync(client, "EVT-E2E-DUP") == "Succeeded", "first delivery to succeed");

        await client.PostAsync("/events", Body(payload)); // the broker delivers it again
        await WaitUntilAsync(async () =>
            (await GetJsonAsync(client, "/events/EVT-E2E-DUP")).GetProperty("duplicatesIgnored").GetInt32() == 1,
            "the duplicate to be recorded");

        Assert.Single(factory.Downstream.Calls); // exactly one downstream update, no matter how often it arrived
    }

    [Fact]
    public async Task MalformedBody_IsAccepted_DeadLettered_AndDoesNotBlockLaterEvents()
    {
        using var factory = new IntegrationAppFactory();
        using var client = factory.CreateClient();

        var bad = await client.PostAsync("/events", Body("{ \"eventId\": \"EVT-BROKEN\", \"employeeId\":"));
        Assert.Equal(HttpStatusCode.Accepted, bad.StatusCode); // accepted, not rejected, so nothing is lost

        await client.PostAsync("/events", Body(ProcessorHarness.Evt("EVT-E2E-AFTER-BAD")));

        await WaitUntilAsync(async () => await StatusOfAsync(client, "EVT-E2E-AFTER-BAD") == "Succeeded",
            "the event after the malformed one to succeed");

        var deadLetters = await GetJsonAsync(client, "/deadletters");
        var only = Assert.Single(deadLetters.EnumerateArray());
        Assert.Equal("Parse", only.GetProperty("stage").GetString());
        Assert.Contains("EVT-BROKEN", only.GetProperty("payload").GetString()); // raw payload kept for replay
    }

    [Fact]
    public async Task DeadLetteredEvent_CanBeReplayedThroughTheApi_OnceTheOutageIsOver()
    {
        using var factory = new IntegrationAppFactory();
        using var client = factory.CreateClient();
        factory.Downstream.FailNext(1); // Employee 360 is down for the first call only

        await client.PostAsync("/events", Body(ProcessorHarness.Evt("EVT-E2E-REPLAY")));
        await WaitUntilAsync(async () => await StatusOfAsync(client, "EVT-E2E-REPLAY") == "DeadLettered",
            "the event to be dead-lettered");

        var deadLetters = await GetJsonAsync(client, "/deadletters");
        var failed = Assert.Single(deadLetters.EnumerateArray());
        Assert.Equal("Downstream", failed.GetProperty("stage").GetString());
        var id = failed.GetProperty("id").GetGuid();

        var replayResponse = await client.PostAsync($"/deadletters/{id}/replay", content: null);
        replayResponse.EnsureSuccessStatusCode();
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Processed", replay.GetProperty("outcome").GetString());
        Assert.True(replay.GetProperty("markedReplayed").GetBoolean());
        Assert.Equal("Succeeded", await StatusOfAsync(client, "EVT-E2E-REPLAY"));
        Assert.Empty((await GetJsonAsync(client, "/deadletters")).EnumerateArray()); // no longer open

        // A second replay of the same dead letter is refused rather than sent again.
        var again = await client.PostAsync($"/deadletters/{id}/replay", content: null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task SampleScenarioSequence_ProducesTheExpectedOutcomeForEveryAwkwardCase()
    {
        using var factory = new IntegrationAppFactory();
        using var client = factory.CreateClient();

        var publish = await client.PostAsync("/samples/publish", content: null);
        Assert.Equal(HttpStatusCode.Accepted, publish.StatusCode);

        // The worker handles messages in order, so once the LAST sample file is done, everything before it is too.
        await WaitUntilAsync(async () => await StatusOfAsync(client, "EVT-10490-001") == "Succeeded",
            "the final sample event to succeed");

        // Valid, CPT alias, newer update, and the good event after all the bad ones.
        // The duplicate, stale, malformed, invalid and rejected events never produced a successful update.
        var health = await GetJsonAsync(client, "/health");
        var byStatus = health.GetProperty("eventsByStatus");
        Assert.Equal(4, byStatus.GetProperty("Succeeded").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("Superseded").GetInt32());      // EVT-10452-002 arrived after 003
        Assert.Equal(3, byStatus.GetProperty("DeadLettered").GetInt32());    // missing id, unknown location, downstream 422
        Assert.Equal(4, health.GetProperty("openDeadLetters").GetInt32());   // those three plus the malformed payload

        var duplicate = await GetJsonAsync(client, "/events/EVT-10452-001");
        Assert.Equal(1, duplicate.GetProperty("duplicatesIgnored").GetInt32());

        // Downstream saw exactly five calls, in order. The duplicate and the stale event never reached it.
        Assert.Equal(
            new[] { "EMP10452", "EMP10471", "EMP10452", "EMP66666", "EMP10490" },
            factory.Downstream.Calls.Select(c => c.EmployeeId));
        Assert.Equal("Cape Town", factory.Downstream.Calls.Single(c => c.EmployeeId == "EMP10471").Location);
    }
}
