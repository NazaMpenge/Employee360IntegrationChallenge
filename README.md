# Employee 360 Integration

A small C#/.NET service that consumes `EmployeeUpdated` events from an HR system (D365),
validates and transforms them, and reliably passes them to a downstream Employee 360 REST API.

> Work in progress - built step by step. See "Status" below.

## First-time setup

```bash
dotnet new sln -n Employee360Integration
dotnet sln add src/Employee360.Integration/Employee360.Integration.csproj
dotnet sln add tests/Employee360.Tests/Employee360.Tests.csproj
dotnet build
dotnet test
```

## Status

- [x] Step 1: solution scaffold, event parsing, validation, location normalisation, mapping
- [x] Step 2: persistence (SQLite), idempotency, stale-event check, dead-letter store
- [ ] Step 3: downstream client with retries, mock Employee 360 API
- [ ] Step 4: event queue + worker, inspection/replay endpoints
- [ ] Step 5: remaining tests, sample events, pipeline YAML
