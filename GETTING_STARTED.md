# Getting Started

## Prerequisites

- .NET SDK 10.0.x
- Azure Functions Core Tools (for local function hosting when needed)

## Build

```bash
dotnet build MISA_Agentic.slnx -c Release
```

## Test

```bash
dotnet test MISA_Agentic.slnx -c Release
```

## Run MISA Functions Locally

```bash
cd src/MISA.Functions
dotnet run --configuration Release -- --port 7257
```

Default local endpoint:

- `http://localhost:7257/api/irt/chat`

## Local Validation Quick Check

1. Confirm health endpoint:
   - `GET /api/irt/health`
2. Post a sample chat request:
   - `POST /api/irt/chat`
3. Confirm SSE frames include `event:` and `data:`.

## Notes

- The runtime currently uses Akka-first orchestration with MAF-ready seams.
- API and SSE compatibility is intentionally strict to protect existing integrations.
