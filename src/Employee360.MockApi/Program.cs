using Employee360.MockApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<MockState>();
var app = builder.Build();

// ---- The "Employee 360" API the integration talks to -------------------------------------

app.MapPut("/api/employees/{id}", async (
    string id, EmployeeUpdateRequest body, HttpContext ctx, MockState state, ILogger<MockState> logger) =>
{
    state.CountRequest();

    // Injected faults come first so they hit exactly the next N requests.
    var fault = state.TakeFault();
    if (fault is not null)
    {
        logger.LogWarning("Injecting fault '{Fault}' for {EmployeeId}", fault, id);
        switch (fault)
        {
            case "error500":
                return Results.Problem(statusCode: 500, title: "Injected internal server error");
            case "unavailable503":
                return Results.Problem(statusCode: 503, title: "Injected service unavailable");
            case "badrequest400":
                return Results.Problem(statusCode: 400, title: "Injected bad request");
            case "timeout":
                try { await Task.Delay(TimeSpan.FromSeconds(60), ctx.RequestAborted); }
                catch (OperationCanceledException) { /* the caller gave up, as intended */ }
                return Results.StatusCode(504);
        }
    }

    if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required");

    if (!string.Equals(id, body.EmployeeId, StringComparison.OrdinalIgnoreCase))
        return Results.Problem(statusCode: 400, title: "Employee id in route and body do not match");

    if (MockState.PoisonEmployeeIds.Contains(id))
        return Results.Problem(statusCode: 422, title: $"Employee {id} rejected by master data rules");

    var applied = state.Apply(key.ToString(), body);
    if (!applied)
    {
        ctx.Response.Headers["Idempotent-Replay"] = "true";
        return Results.Ok(new { status = "AlreadyApplied", employeeId = id });
    }

    logger.LogInformation("Applied update for {EmployeeId} from event {EventId}", id, body.SourceEventId);
    return Results.Ok(new { status = "Applied", employeeId = id });
});

app.MapGet("/api/employees/{id}", (string id, MockState state) =>
    state.Find(id) is { } employee ? Results.Ok(employee) : Results.NotFound());

// ---- Test controls: make the next requests fail, and see what actually reached us ----------

app.MapPost("/admin/faults", (FaultRequest request, MockState state) =>
{
    if (!MockState.FaultModes.Contains(request.Mode))
        return Results.BadRequest(new { error = $"Unknown mode. Use one of: {string.Join(", ", MockState.FaultModes)}" });

    state.QueueFaults(request.Mode, request.Times);
    return Results.Ok(state.Stats());
});

app.MapGet("/admin/stats", (MockState state) => Results.Ok(state.Stats()));

app.MapPost("/admin/reset", (MockState state) =>
{
    state.Reset();
    return Results.Ok(state.Stats());
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();
