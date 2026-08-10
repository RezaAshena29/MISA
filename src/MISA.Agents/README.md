# MISA.Agents

## Purpose

Provides routing and future MAF integration seams for agent orchestration.

## Key File

- `MafAgentRouter.cs`

## Responsibilities

- Resolve initial route from user intent and session context.
- Keep deterministic behavior for parity while MAF adapters are introduced incrementally.

## MAF Readiness

- This module remains the primary location for future workflow adapters and capability registries.
- Akka stays runtime owner in current phase.

## Verification

```bash
dotnet build MISA_Agentic.slnx -c Release
```
