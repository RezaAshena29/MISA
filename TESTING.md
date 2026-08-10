# Testing Guide

## Run Full Suite

```bash
dotnet test MISA_Agentic.slnx -c Release
```

## Coverage-Oriented Run

```bash
dotnet test MISA_Agentic.slnx -c Release \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=opencover \
  /p:CoverletOutput=./coverage/
```

## Key Test Classes

- `ChatFunctionsSseTests`
- `SseContractParityTests`
- `DecisioningServiceTests`
- `KnowledgeServiceTests`
- `GovernanceGuardTests`
- `DurableSessionStoreTests`

## Focus Areas

- Contract parity for request routes and SSE wire events.
- Clarification route behavior for insufficient input.
- Illustration route fanout/fanin and ranked output.
- Session continuity and durability behavior.
- Guardrail enforcement and safe output masking.

## Baseline Expectation

- Current architecture baseline: `23/23` tests passing in Release mode.

## Suggested Additions for Agentic Expansion

- Route-level tests for each named agent stage handoff.
- Partial-failure fanout/fanin resilience tests.
- Event ordering tests for multi-agent execution traces.
