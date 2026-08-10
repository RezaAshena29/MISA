# MISA.Infrastructure

## Purpose

Implements infrastructure concerns for guards, state persistence, and runtime support services.

## Key File

- `GuardrailsAndSessionStore.cs`

## Responsibilities

- Inbound prompt safety guard.
- Outbound response masking/sanitization.
- Session persistence modes (in-memory and durable options).

## Verification

```bash
dotnet test MISA_Agentic.slnx -c Release --filter "ClassName=MISA.ArchitectureTests.GovernanceGuardTests|ClassName=MISA.ArchitectureTests.DurableSessionStoreTests"
```
