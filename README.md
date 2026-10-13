# Employee 360 Integration

A small C#/.NET service that consumes `EmployeeUpdated` events from an HR system (D365),
validates and transforms them, and reliably passes them to a downstream Employee 360 REST API.

> Work in progress - built step by step. See "Status" below.

## First-time setup

```bash
dotnet new sln -n Employee360Integration
dotnet sln add src/Employee360.Integration/Employee360.Integration.csproj
dotnet sln add src/Employee360.MockApi/Employee360.MockApi.csproj
dotnet sln add tests/Employee360.Tests/Employee360.Tests.csproj
dotnet build
dotnet test
```

## Mock Employee 360 API (port 5100)

```bash
dotnet run --project src/Employee360.MockApi
```

Make the next N requests fail, then watch the integration retry:

```bash
curl -X POST localhost:5100/admin/faults -H "Content-Type: application/json" -d '{"mode":"error500","times":2}'
# modes: error500, unavailable503, badrequest400, timeout
curl localhost:5100/admin/stats     # what actually reached Employee 360
curl -X POST localhost:5100/admin/reset
```

Employee `EMP66666` is permanently rejected (422) - the "cannot be processed" poison case.
Every update must carry an `Idempotency-Key` header; a repeated key is acknowledged but not re-applied.

## Run it

Run both from the repo root (paths such as `data/` and `samples/events` are relative to it).

```bash
dotnet run --project src/Employee360.MockApi        # terminal 1 -> http://localhost:5100
dotnet run --project src/Employee360.Integration    # terminal 2 -> http://localhost:5200
```

### Walkthrough

```bash
# 1. Publish the whole scenario sequence (see samples/events/README.md)
curl -X POST localhost:5200/samples/publish

# 2. What happened to a specific event?
curl localhost:5200/events/EVT-10452-001      # status Succeeded; send 02 again and duplicatesIgnored rises
curl localhost:5200/events/EVT-10452-002      # Superseded (older event arrived after a newer one)
curl localhost:5200/employees/EMP10452        # last applied state + every event seen for this employee

# 3. What reached Employee 360? Duplicates and stale events never did.
curl localhost:5100/admin/stats

# 4. Failures and replay
curl localhost:5200/deadletters               # malformed, missing id, unknown location, rejected by downstream
curl -X POST localhost:5200/deadletters/<id>/replay

# 5. Downstream outage: break Employee 360, publish, watch the retries in the log
curl -X POST localhost:5100/admin/faults -H "Content-Type: application/json" -d '{"mode":"error500","times":2}'
curl -X POST localhost:5200/samples/publish/11-fault-demo.json
curl localhost:5200/health
```

### Endpoints (integration service)

| Method | Path | Purpose |
|---|---|---|
| POST | `/events` | Queue a raw event body (malformed bodies are accepted and dead-lettered) |
| GET | `/events/{eventId}` | Outcome of one event: status, attempts, duplicates ignored, dead letters |
| GET | `/employees/{employeeId}` | Last applied state and the events seen for the employee |
| GET | `/deadletters` | Failed events with payload, stage and reason (`?includeReplayed=true` for all) |
| POST | `/deadletters/{id}/replay` | Re-run a dead-lettered event through the normal pipeline |
| GET | `/health` | Queue depth, event counts by status, open dead letters |
| POST | `/samples/publish[/{name}]` | Publish the synthetic scenario files |

## Status

- [x] Step 1: solution scaffold, event parsing, validation, location normalisation, mapping
- [x] Step 2: persistence (SQLite), idempotency, stale-event check, dead-letter store
- [x] Step 3: downstream client with retries, mock Employee 360 API
- [x] Step 4: event queue + worker, inspection/replay endpoints, sample events
- [ ] Step 5: remaining tests, sample events, pipeline YAML
