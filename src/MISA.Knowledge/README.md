# MISA.Knowledge

## Purpose

Provides supporting knowledge responses used by reasoning and illustration output composition.

## Key File

- `KnowledgeService.cs`

## Responsibilities

- Resolve deterministic knowledge snippets for current route.
- Provide supporting context for recommendation explanations.

## Verification

```bash
dotnet test MISA_Agentic.slnx -c Release --filter "ClassName=MISA.ArchitectureTests.KnowledgeServiceTests"
```
