# NetPdf Project Skills

Project-scoped skills callable via the `Skill` tool. They encode the recurring "how do I do X in this codebase" patterns so they don't need to be re-derived each session.

## Available skills

| Skill | When to use |
|---|---|
| [`phase-status`](phase-status.md) | At the start of any session — show current phase, build/test state, what's next. |
| [`add-diagnostic-code`](add-diagnostic-code.md) | Adding a new stable diagnostic code (e.g., `CSS-FOO-UNSUPPORTED-001`). |
| [`add-corpus-sample`](add-corpus-sample.md) | Adding a new HTML test sample to `tests/NetPdf.RealDocuments/Corpus/`. |
| [`add-css-property`](add-css-property.md) | Adding a new CSS property to the property table (Phase 2+). |
| [`render-corpus`](render-corpus.md) | Render every corpus file end-to-end and run visual-regression diff (Phase 3+). |
| [`bench`](bench.md) | Run BenchmarkDotNet and compare to baseline; flag regressions. |
| [`aot-check`](aot-check.md) | Verify Native AOT publish + execution works. Run before/after risky changes. |
| [`uax-test`](uax-test.md) | Validate a UAX algorithm implementation (UAX #9, #14, #29) against UCD reference test data. |

## How to invoke

In Claude Code: `/<skill-name>` or via the `Skill` tool.

## How to add a new skill

1. Drop a `.md` file in this directory with frontmatter:
   ```yaml
   ---
   name: my-skill
   description: One-line description shown in skill listings.
   ---
   ```
2. Body is the prompt Claude follows when invoked.
3. Add a row to this README's table.

Keep skills focused. If a skill grows beyond ~150 lines it's probably two skills.
