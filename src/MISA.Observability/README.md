# MISA.Observability

## Purpose

Centralizes telemetry wiring for traces and metrics across the MISA agentic runtime.

## Key File

- `ObservabilityServiceCollectionExtensions.cs`

## Responsibilities

- Register OpenTelemetry instrumentation.
- Keep trace/metric setup consistent across modules.

## Verification

```bash
dotnet build MISA_Agentic.slnx -c Release
```
