using Employee360.Integration.Persistence;
using Employee360.Integration.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Configuration, not code: connection string and location aliases come from appsettings.
var connectionString = builder.Configuration.GetConnectionString("Integration")
    ?? "Data Source=data/integration.db";

builder.Services.AddDbContext<EmployeeIntegrationDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ProcessingStore>();

builder.Services.Configure<LocationOptions>(builder.Configuration.GetSection(LocationOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new LocationNormaliser(sp.GetRequiredService<IOptions<LocationOptions>>().Value.Aliases));
builder.Services.AddSingleton<EmployeeEventTransformer>();

var app = builder.Build();

// Keep setup simple for the exercise: create the SQLite file/schema on startup.
// In production this would be a migration run by the pipeline, not app startup.
using (var scope = app.Services.CreateScope())
{
    Directory.CreateDirectory("data");
    scope.ServiceProvider.GetRequiredService<EmployeeIntegrationDbContext>().Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program> later.
public partial class Program;
