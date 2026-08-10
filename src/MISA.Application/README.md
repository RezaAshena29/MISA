# MISA.Application

## Purpose

Defines application-layer pipeline and contracts used by orchestration and adapters.

## Key File

- `ChatPipelineAndContracts.cs`

## Responsibilities

- Pipeline orchestration abstraction (`IChatPipeline`).
- Runtime abstraction (`IAgentExecutionRuntime`).
- Session interfaces and guardrail abstractions.
- Decisioning and knowledge service contracts.

## Notes

- Keep this layer free of infrastructure and transport concerns.
- New agent-stage interfaces should be introduced here first when expanding orchestration.

## Verification

```bash
dotnet build MISA_Agentic.slnx -c Release
```
