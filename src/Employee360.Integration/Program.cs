var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Placeholder until the pipeline is wired up in later steps.
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program> later.
public partial class Program;
