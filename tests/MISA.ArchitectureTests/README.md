# MISA.ArchitectureTests

## Purpose

Validates architecture and contract parity guarantees for MISA_Agentic.

## Test Coverage Areas

- SSE contract and framing parity.
- Chat endpoint behavior.
- Decisioning and knowledge behavior.
- Guardrail enforcement.
- Durable session behavior.

## Run All Tests

```bash
dotnet test MISA_Agentic.slnx -c Release
```

## Run Specific Classes

```bash
dotnet test MISA_Agentic.slnx -c Release --filter "ClassName=MISA.ArchitectureTests.SseContractParityTests"
dotnet test MISA_Agentic.slnx -c Release --filter "ClassName=MISA.ArchitectureTests.ChatFunctionsSseTests"
```
