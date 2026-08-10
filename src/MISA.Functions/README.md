# MISA.Functions

## Purpose

Hosts the HTTP API surface for MISA agentic chat using Azure Functions isolated worker.

## Key Files

- `Program.cs`: dependency registration and startup pipeline.
- `ChatFunctions.cs`: chat/session/health endpoints and SSE framing.

## Responsibilities

- Accept and validate incoming request payloads.
- Invoke application pipeline and stream SSE frames.
- Preserve existing route and wire contract compatibility.

## Verification

```bash
cd src/MISA.Functions
dotnet run --configuration Release -- --port 7257
```

- Verify `GET /api/irt/health`.
- Verify `POST /api/irt/chat` emits valid SSE framing.
