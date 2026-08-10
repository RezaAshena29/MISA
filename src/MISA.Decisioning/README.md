# MISA.Decisioning

## Purpose

Provides deterministic decisioning and recommendation ranking for illustration scenarios.

## Key File

- `RuleBasedDecisioningService.cs`

## Responsibilities

- Build recommendation table candidates.
- Apply rank/scoring logic for selected scenario.
- Produce explanation-ready recommendation metadata.

## Verification

```bash
dotnet test MISA_Agentic.slnx -c Release --filter "ClassName=MISA.ArchitectureTests.DecisioningServiceTests"
```
