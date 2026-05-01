# NetPdf Phase Execution Guides

Per-phase execution detail for building NetPdf. Each guide is **self-contained** — a Claude session (or any contributor) opening this repo cold can pick up the next phase from these docs without needing the original plan or prior conversation.

## How to use these docs

When starting a phase:
1. Read the phase's doc end-to-end before writing code.
2. Re-read `../clean-room-policy.md` (the legal contract) and `../legal/dependency-dossier.md` (what deps are pre-approved).
3. Check `../compatibility-matrix.md` for which CSS features ship in this phase and which produce diagnostics.
4. Follow the work breakdown in order. Each subtask has its own checkpoint; commit at each.
5. Verify against the phase's exit criteria before tagging the release.

## Phase index

| # | Doc | Status | Tagged release |
|---|---|---|---|
| **Phase 0** | [phase-0-architecture-lock.md](phase-0-architecture-lock.md) | ✅ done | `0.0.1-phase0` |
| **Phase 1** | [phase-1-pdf-writer-and-text.md](phase-1-pdf-writer-and-text.md) | ⏳ next | `0.1.0-alpha` |
| **Phase 2** | [phase-2-css-engine.md](phase-2-css-engine.md) | ⏳ pending | `0.3.0-alpha` |
| **Phase 3** | [phase-3-layout-and-pagination.md](phase-3-layout-and-pagination.md) | ⏳ pending | `0.7.0-beta` |
| **Phase 4** | [phase-4-visual-parity.md](phase-4-visual-parity.md) | ⏳ pending | `0.9.0-rc1` |
| **Phase 5** | [phase-5-packaging-and-release.md](phase-5-packaging-and-release.md) | ⏳ pending | `1.0.0` |

## Doc structure

Every phase doc follows the same skeleton so they're skimmable:

- **Goal** — one paragraph
- **Time estimate** — Claude Opus 4.7 high + Roland-in-the-loop
- **Tagged release** — version + what's in it
- **Prerequisites** — repo state required to start
- **Deliverables** — concrete artifacts
- **Spec references** — W3C / ISO / UAX URLs to consult
- **Work breakdown** — ordered subtasks with mini-estimates
- **Implementation notes** — constraints, design choices, AOT/perf rules
- **Test plan** — how each subtask is verified
- **Exit criteria** — definition of "phase done"
- **Common pitfalls** — known traps
- **Hand-off** — what state Phase N+1 starts in

## Cross-cutting rules (apply to every phase)

These are repeated in each phase doc; capturing them once here for reference:

1. **Clean-room.** Algorithms come from W3C/ISO/UAX specs, not from reading other implementations' code. If you read another implementation for understanding, leave a one-line note in a file-level comment.
2. **Banned deps.** No `System.Drawing`, no browser engines, no AGPL/copyleft, no revenue-capped libraries. The `NetPdf.BannedAnalyzer` Roslyn analyzer enforces this at compile time.
3. **AOT-clean.** No reflection in core paths. Source generators where dynamic registration would otherwise be needed. AOT smoke test gates CI.
4. **Determinism.** Identical input → identical PDF bytes. No PRNG, no `DateTime.Now` in shipped code, optional frozen `/CreationDate`.
5. **C# 14 / .NET 10.** Use `Span`, `ReadOnlySpan`, `IBufferWriter<byte>`, `ArrayPool<T>`, `FrozenDictionary`, `SearchValues<char>`, `[InlineArray]`, `required`, `init`, primary constructors, file-scoped namespaces, `ref struct` enumerators. Forbid LINQ in hot paths (Roslyn analyzer enforced).
6. **Apache-2.0 file headers.** Every source file starts with the standard Apache-2.0 boilerplate header (`Copyright 2026 Roland Aroche and NetPdf contributors. Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.`).
7. **Diagnostics.** Unsupported features emit a stable diagnostic code from `docs/diagnostics-codes.md`, never silent corruption.

## Coordination

The plan file at `~/.claude/plans/i-want-to-build-parallel-eagle.md` is the high-level architectural reference. These phase docs are the per-phase execution detail. If they ever disagree, **the phase doc wins for execution detail and the plan wins for architectural decisions**; flag conflicts in a PR.
