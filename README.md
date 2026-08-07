# MISA_Agentic

MISA_Agentic is the new enterprise C#/.NET 10 foundation for migrating the current Python IRT runtime to a Microsoft Agent Framework and Akka-based architecture while preserving existing gateway and UI contracts.

## Solution Modules

- src/MISA.Domain
- src/MISA.Application
- src/MISA.Contracts
- src/MISA.Infrastructure
- src/MISA.Decisioning
- src/MISA.Orchestration.Akka
- src/MISA.Agents
- src/MISA.Knowledge
- src/MISA.Observability
- src/MISA.Functions

## Current Stage

Phase 0/1/2 implementation baseline complete:

- Clean architecture module scaffold and .NET 10 solution foundation.
- Microsoft Agent Framework + Akka cluster package baseline integrated.
- Deterministic route-aware orchestration with SSE contract-compatible event streaming.
- Rule-based decisioning and deterministic knowledge responses implemented.
- Durable session store option added (in-memory default, file-backed mode available).
- Governance guardrails hardened (prompt length, inline secret pattern blocking, outbound masking).
- OpenTelemetry tracing and metrics wired for application, orchestration, decisioning, and knowledge modules.

## Validation Baseline

- Full solution build passes in Release mode.
- Architecture and parity test suite passes (23/23).
- Endpoint-level SSE framing tests validate both `event:` and `data:` output shapes.

## Recommended Next Enhancements

1. Add integration parity harness that diffs C# SSE streams against captured Python runtime fixtures.
2. Move session durability from file mode to shared store mode (Redis or database) for multi-instance deployments.
3. Replace deterministic route resolver with full MAF orchestration workflows when governance workflow policies are finalized.