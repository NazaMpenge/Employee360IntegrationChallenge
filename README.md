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

## Status

- [x] Step 1: solution scaffold, event parsing, validation, location normalisation, mapping
- [x] Step 2: persistence (SQLite), idempotency, stale-event check, dead-letter store
- [x] Step 3: downstream client with retries, mock Employee 360 API
- [ ] Step 4: event queue + worker, inspection/replay endpoints
- [ ] Step 5: remaining tests, sample events, pipeline YAML
