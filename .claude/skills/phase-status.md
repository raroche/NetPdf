---
name: phase-status
description: Show the current NetPdf phase, build/test status, and what to do next. Run at the start of any session.
---

# Skill: phase-status

Give Roland a 30-second orientation on where the project is and what to do next.

## Steps

1. **Determine the active phase.** Read these in order until one resolves:
   - `build/version.json` — has `"phase"` field if Phase 0 is current.
   - `CHANGELOG.md` — the most recent unreleased section's `Phase N` heading.
   - The latest git tag (e.g., `0.1.0-alpha` → Phase 1 just completed → Phase 2 next).
2. **Read the active phase doc** at `docs/phases/phase-<N>-*.md`. Focus on:
   - Goal statement.
   - Work breakdown table.
   - Exit criteria.
3. **Summarize repo state** by running in parallel:
   - `dotnet build NetPdf.slnx -c Release 2>&1 | tail -5` — extract `Build succeeded` / `Build FAILED` and error/warning counts.
   - `dotnet test NetPdf.slnx -c Release --nologo --no-build 2>&1 | grep -E "(Passed|Failed|Total)" | tail -10` — extract pass/fail counts.
   - `git status --short` — outstanding changes.
   - `git log --oneline -5` — recent activity.
4. **List uncompleted Phase N work breakdown items.** Cross-reference the phase doc's Work breakdown table against recent commits (heuristic: a commit message containing the task description likely means it's done).
5. **Print a one-screen report** in this format:

```
NetPdf — Phase <N>: <name>
Tagged: <last-release-tag>
Status: build <ok|FAIL>, tests <X>/<Y> passing, <K> commits since last tag

Active phase doc: docs/phases/phase-<N>-<slug>.md
Work breakdown — uncompleted (best-guess):
  □ <task 1>
  □ <task 2>
  ...

Exit criteria — outstanding:
  □ <criterion 1>
  □ <criterion 2>
  ...

Suggested next action: <task or check>
```

## Output

A short status report. Do NOT make changes; this is read-only.
