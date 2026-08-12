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

## MCP Configuration Templates

MCP rollout templates are provided for environment-specific configuration:

- `appsettings.Development.json`
- `appsettings.SIT.json`

Both templates configure:

- `Misa:Mcp` for global MCP broker behavior (enable flag, base URL, allowlist, and timeouts).
- `Misa:Mcp:Knowledge` for knowledge-route MCP decorator behavior.

Recommended rollout flow:

1. Keep Development disabled (`Enabled=false`) while validating fallback behavior.
2. Enable SIT in shadow/validation mode first and verify telemetry plus SSE parity.
3. Promote to higher environments only after parity and SLO checks pass.
