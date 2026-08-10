# MISA.Contracts

## Purpose

Contains transport-level DTOs and wire-contract definitions for API and SSE communication.

## Key File

- `ChatContracts.cs`

## Responsibilities

- Route constants and endpoint contract names.
- SSE event type mapping and wire names.
- Chat request/response envelope DTOs.
- Columns payload and UDM patch DTOs.

## Contract Rule

- Changes in this module require parity review against existing gateway/UI consumers.

## Verification

```bash
dotnet test MISA_Agentic.slnx -c Release --filter "ClassName=MISA.ArchitectureTests.ChatFunctionsSseTests"
```
