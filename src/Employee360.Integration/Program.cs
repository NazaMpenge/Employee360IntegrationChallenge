using Employee360.Integration.Api;
using Employee360.Integration.Downstream;
using Employee360.Integration.Messaging;
using Employee360.Integration.Persistence;
using Employee360.Integration.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// Configuration, not code: connection string, downstream URL/retry settings and
// location aliases all come from appsettings (and env vars / user-secrets for secrets).
var connectionString = builder.Configuration.GetConnectionString("Integration")
    ?? "Data Source=data/integration.db";

builder.Services.AddDbContext<EmployeeIntegrationDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ProcessingStore>();

builder.Services.Configure<LocationOptions>(builder.Configuration.GetSection(LocationOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new LocationNormaliser(sp.GetRequiredService<IOptions<LocationOptions>>().Value.Aliases));
builder.Services.AddSingleton<EmployeeEventTransformer>();

// Downstream Employee 360 client: HttpClient + Polly retry/timeout pipeline.
builder.Services.Configure<Employee360ApiOptions>(builder.Configuration.GetSection(Employee360ApiOptions.SectionName));
builder.Services.AddSingleton<ResiliencePipeline<HttpResponseMessage>>(sp =>
    ResiliencePipelines.CreateEmployee360Pipeline(
        sp.GetRequiredService<IOptions<Employee360ApiOptions>>().Value,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Employee360.Downstream")));

builder.Services.AddHttpClient<IEmployee360Client, Employee360HttpClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<Employee360ApiOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan; // timeouts are owned by the Polly pipeline, per attempt
    if (!string.IsNullOrWhiteSpace(options.ApiKey))
        http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
});

builder.Services.AddScoped<EventProcessor>();

// Messaging: in-memory stand-in for Service Bus, drained by a background worker.
builder.Services.Configure<SamplesOptions>(builder.Configuration.GetSection(SamplesOptions.SectionName));
builder.Services.AddSingleton<SampleCatalog>();
builder.Services.AddSingleton<IEventQueue, InMemoryEventQueue>();
builder.Services.AddHostedService<EventWorker>();

var app = builder.Build();

// Keep setup simple for the exercise: create the SQLite file/schema on startup.
// In production this would be a migration run by the pipeline, not app startup.
using (var scope = app.Services.CreateScope())
{
    Directory.CreateDirectory("data");
    scope.ServiceProvider.GetRequiredService<EmployeeIntegrationDbContext>().Database.EnsureCreated();
}

app.MapIntegrationEndpoints();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program> later.
public partial class Program;
