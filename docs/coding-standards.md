# NetPdf Coding Standards

How we write code in this repo. Skim once when joining the project; revisit during code review. The standards complement (don't duplicate) the legal contract in [`clean-room-policy.md`](clean-room-policy.md).

## Core principles

### SOLID

- **S — Single Responsibility.** Each class has one reason to change. `PdfBoolean` knows how to emit `true`/`false`; nothing else. `PdfWriter` writes bytes and tracks position; nothing else. If a class touches three unrelated concerns, split it.
- **O — Open/Closed.** Open for extension, closed for modification. New PDF object types are added by subclassing `PdfObject`, not by editing existing types. New diagnostic codes are added to the registry, not by altering emit sites.
- **L — Liskov Substitution.** Any subtype must work everywhere its base type does. A new `PdfObject` subclass must respect the `WriteTo(PdfWriter)` contract — it produces bytes conforming to the spec without throwing, mutating the writer beyond its position, or leaking state.
- **I — Interface Segregation.** Small, focused interfaces. `IResourceLoader`, `IFontResolver`, `IDiagnosticsSink` are each one method. Don't merge them into a god-interface that consumers must mostly stub.
- **D — Dependency Inversion.** Depend on abstractions, not concretions. Layouters depend on `IBreakResolver`, not on a specific paginator implementation; the paint pipeline depends on `DisplayCommand` IR, not on `NetPdf.Pdf` types.

### DRY — with the rule of three

**Don't Repeat Yourself**, but **don't pre-abstract**. Three similar pieces of code is the threshold to consider extracting a helper. Two is a coincidence. One is just code.

- Copying 5 lines once: leave it.
- Copying the same 5 lines a third time: factor it.
- Premature abstractions are worse than duplication — they obscure intent and lock in a shape we don't yet understand.

This is from the project's commit style and applies project-wide. When in doubt, write the duplicated code, and refactor when the third occurrence appears.

### YAGNI

**You Aren't Gonna Need It.** Don't build features the current task doesn't require. The plan is detailed; trust it. If a future phase needs something, that phase will build it.

- Don't add error handling for impossible cases.
- Don't add config knobs nobody asked for.
- Don't add abstractions "in case we need to swap implementations later."
- Don't ship dead code.

### Composition over inheritance

Inheritance is reserved for true type hierarchies (`PdfObject` and its subclasses where each subclass IS-A PDF object). For "extends behavior" relationships, compose: pass services in, hold them as fields, delegate. Avoids the deep-inheritance debugging tax.

## Project-specific best practices

### File header (mandatory)

Every source file (`.cs`) starts with exactly:

```csharp
// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
```

A Roslyn analyzer (planned: `NETPDF0010`) will enforce this. Files without the header fail the build.

### Internal-by-default

Default visibility is `internal`. Types become `public` only when they're part of the consumer-facing API in the `NetPdf` namespace at the facade. The PDF byte writer, layouters, paint pipeline are all internal. Tests reach internals via `[InternalsVisibleTo("NetPdf.UnitTests")]`.

### AOT discipline

The library must be Native AOT-compatible. In core paths:

- ❌ No `Type.GetType(string)`, `Activator.CreateInstance`, reflection.
- ❌ No `dynamic`, no `IDynamicMetaObjectProvider`.
- ❌ No `JsonSerializer` with reflection-based serialization (use source generators if needed).
- ✅ `MemoryMarshal`, `Unsafe`, `RuntimeHelpers` — these are AOT-safe.
- ✅ Source generators for things that would otherwise need reflection (CSS property tables, font table parsers, etc.).

The `tests/NetPdf.AotSmoke` project is the gate. CI publishes it with `-p:PublishAot=true` and runs the resulting binary on every PR.

### Determinism

Same input must produce byte-equal output. Always.

- ❌ No `DateTime.Now`, `DateTime.UtcNow`, `Environment.TickCount` in shipped code.
- ❌ No `Random`, no PRNG.
- ❌ No `Dictionary<TKey, TValue>` ordering assumed (use `OrderedDictionary<TKey, TValue>` from .NET 9+ for byte-deterministic emission).
- ❌ No `Guid.NewGuid()` — use a deterministic ID derived from content (e.g., SHA-256 of bytes).
- ✅ Optional frozen `/CreationDate` via `FeatureFlags.DeterministicTimestamps`.
- ✅ Stable object numbering, fixed compression level, deterministic font subsetter prefix algorithm.

A unit test asserts byte-equality of repeated renders for every emit-producing component.

### Performance / allocation discipline

The plan's gates are `≤ 200 ms p50` for a 3-page invoice and `≤ 1.5 s p50` for a 20-page report. To hit these:

- ✅ `Span<T>` / `ReadOnlySpan<T>` pervasive in tokenizers, parsers, selectors, byte writers.
- ✅ `IBufferWriter<byte>` for streaming byte output (zero-copy).
- ✅ `ArrayPool<T>` for temporary buffers (paint commands, glyph buffers, line builders).
- ✅ `stackalloc` for ≤ 256-element buffers in hot paths.
- ✅ `FrozenDictionary<TKey, TValue>` for read-mostly lookup tables built at startup.
- ✅ `SearchValues<char>` for tokenizer character classification.
- ✅ String interning for repeated property names, tag names, font family names.
- ✅ `[InlineArray(N)]` for cache-friendly fixed-size structs.
- ❌ **No LINQ in hot paths.** A Roslyn analyzer enforces this. Cold paths (CLI, configuration) may use LINQ for clarity.
- ❌ No `IEnumerable<T>` allocations in hot paths — use `Span<T>` / `ReadOnlySpan<T>` or `ref struct` enumerators.

Profile with BenchmarkDotNet (`tests/NetPdf.Benchmarks`) before assuming a change improves performance. Allocations matter as much as wall time.

### Validate at boundaries, trust internals

- **Validate input** at public API surface: `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException`, `ArgumentOutOfRangeException`. Catch invalid input as early as possible.
- **Trust internal callers.** Internal methods don't need to re-validate what the public API already checked. Repeated validation is overhead and obscures the validation contract.
- **`InvalidOperationException`** for state errors (calling a method when the object isn't in a valid state for that call).
- **Don't catch and rethrow** without enriching. If you have nothing to add, let the exception propagate.

### Comments — default to none

The system rule and the project rule: **default to writing no comments**. Identifiers explain WHAT the code does. Only comment to explain WHY when the reason isn't obvious from the code: a hidden constraint, a subtle invariant, a workaround for a specific bug, behavior that would surprise a reader.

- ❌ `// Increment counter` (the code does that already).
- ❌ Multi-paragraph docstrings on internal helpers.
- ✅ `// Per ISO 32000-2 §7.5.4: each xref entry shall be exactly 20 bytes.`
- ✅ `// Algorithm understanding informed by reading WeasyPrint's table-fixup logic; no code copied.`

Public API types get XML doc comments (`<summary>`, `<param>`, `<returns>`) because they ship in the NuGet package and IDE tooling consumes them.

## Naming conventions

- **`PascalCase`** for types, public/internal members, constants.
- **`camelCase`** for parameters and locals.
- **`_camelCase`** for private fields.
- **`I` prefix** for interfaces (.NET convention).
- **No Hungarian notation** — no `_strName`, `iCount`, `bIsActive`. The type system covers it.
- **Verbs for methods** (`Convert`, `WriteTo`, `Emit`), **nouns for properties** (`Position`, `Count`, `Diagnostics`).
- **Avoid abbreviations** unless they're domain-canonical (`Pdf`, `Html`, `Css`, `Bidi`, `UAX`, `CJK` are fine; `Cfg`, `Mgr`, `Doc` are not).
- **Symmetric naming** for paired operations: `Allocate`/`Free`, `Push`/`Pop`, `Open`/`Close`.

## Error handling

- **Throw at boundaries, not for control flow.** Exceptions are for exceptional conditions; don't use them for parse failures in hot paths (return `bool` + `out` instead).
- **Argument exceptions for invalid input**: `ArgumentNullException.ThrowIfNull(arg)`, `ArgumentException("...", nameof(arg))`, `ArgumentOutOfRangeException`.
- **`InvalidOperationException` for state errors**: calling a method on a not-yet-initialized object, calling `Save` after `Dispose`, etc.
- **Custom exceptions are rare.** We have `HtmlPdfException` for the public API surface (with a `Code` property tied to the diagnostic registry). Internal code uses BCL exceptions.

## Testing patterns

### The dual-layer rule (non-negotiable)

Every shipped functionality must have **both** unit-test coverage **and** integration-test coverage. Tests land in the same commit/PR as the functionality — never deferred. The dual layer is what makes the codebase safe to modify as it grows.

- **Unit layer.** Lives in `tests/NetPdf.UnitTests/`. Each source file with non-trivial logic gets a mirrored test file in the parallel layout (`src/X/Y.cs` ↔ `tests/NetPdf.UnitTests/X/YTests.cs`). Exercises the type's behavior in isolation: contract, edge cases, invariants, validation, determinism property.
- **Integration layer.** Lives in scenario test projects: `NetPdf.RenderingCorpus`, `NetPdf.PdfValidation`, `NetPdf.LayoutSnapshots`, `NetPdf.PaginationGolden`, `NetPdf.RealDocuments`, `NetPdf.W3cConformance`. Any feature that crosses a project boundary or composes multiple components also gets an integration test exercising the feature through its public/integration surface end-to-end.

Both layers are required because they catch different failure modes. Unit tests catch regressions inside a class; integration tests catch composition bugs (the kind that pass every unit suite but break when the components meet). A feature is not "done" — and a PR is not ready to merge — until both layers cover it.

### Naming

Test names describe behavior, not implementation:

- ✅ `Empty_array_writes_brackets`
- ✅ `NaN_throws`
- ✅ `Negative_infinity_throws`
- ❌ `Test1`
- ❌ `TestPdfArrayWriteTo`

Underscore-separated `Verb_state_expected` style. Reads like a sentence in the test runner output.

### Structure

- **One test class per source class** (parallel layout: `src/NetPdf.Pdf/Objects/PdfArray.cs` ↔ `tests/NetPdf.UnitTests/Pdf/Objects/PdfArrayTests.cs`).
- **Arrange-Act-Assert** is implicit; don't add comments labeling the sections.
- **`[Theory]` + `[InlineData]`** for parameterized tests.
- **One logical assertion per test.** Multiple `Assert.X` calls are fine if they all check the same behavior; if you find yourself testing two unrelated behaviors, split into two tests.
- **Test names + assertion messages** are the documentation. Don't add `// expected: ...` comments.

### What to test (unit layer)

- **Public/internal API behaviors.** Every method has tests covering its contract.
- **Edge cases.** Empty inputs, max/min values, invalid arguments, boundary conditions.
- **Validation.** Every guard / `ArgumentException` / `InvalidOperationException` has a test that triggers it.
- **Determinism property.** For every byte-emitting component: same input → same bytes.
- **Invariants.** For tagged unions, state machines, and similar — assert the invariant holds across all reachable states.

### What to test (integration layer)

- **Cross-component composition.** Layout → Paginate → Paint → Pdf bytes; ensure the seams hold.
- **Specs as gates.** UAX reference test data for bidi/line-break/segmentation; W3C CSS test suites for layout.
- **Real-world corpus.** `tests/NetPdf.RealDocuments/Corpus/` files render correctly.
- **Determinism end-to-end.** Render the same input twice through the full pipeline; SHA-256 the bytes; assert equal.
- **External-tool validation.** PDFium / qpdf parse + check the output; pixel-diff against pinned Chromium reference.

### What NOT to test

- Trivial getters/setters with no logic.
- Reflection-based behavior (we don't use reflection).
- Implementation details — refactors should not break tests when behavior is unchanged.

## Project conventions

### Project structure

```
src/<Project>/                           — Source code
  <Subarea>/                             — Topical subfolder (e.g., Objects/, Bidi/, Shaping/)
    <Type>.cs                            — One type per file (rare exceptions for tiny enums or records)
tests/NetPdf.UnitTests/<Project>/        — Mirror layout for unit tests
  <Subarea>/
    <Type>Tests.cs
```

One type per file unless the types are trivially small and tightly related (e.g., a record + its enum companion). When in doubt: separate files.

### Roslyn analyzers we follow

Already wired up via `Directory.Build.props` and `.editorconfig`:

- `Nullable` warnings → errors.
- `TreatWarningsAsErrors=true`.
- IL2026 / IL3050 (AOT trim/dynamic-code warnings) → errors.
- File-scoped namespaces preferred (`csharp_style_namespace_declarations = file_scoped:warning`).
- Banned-deps analyzer (planned: `NETPDF0002`) — fails build on banned namespace reference.
- LINQ-in-hot-path analyzer (planned: `NETPDF0001`) — fails build for `using System.Linq` in flagged paths.
- Apache-2.0 header analyzer (planned: `NETPDF0010`).

When the analyzers ship, they enforce these standards mechanically — so write code that already passes them.

## Code review checklist

For PRs that touch source code:

- [ ] Apache-2.0 file header on every new file.
- [ ] Public types have XML doc comments; internal types have minimal comments (WHY only).
- [ ] **Unit tests** added/updated for every behavioral change.
- [ ] **Integration tests** added/updated when the change crosses a project boundary or composes multiple components.
- [ ] No LINQ in hot paths.
- [ ] No reflection / dynamic / `Activator.CreateInstance` in core paths.
- [ ] Determinism preserved (no `DateTime.Now`, `Random`, ordering assumptions).
- [ ] Argument validation at the public API; trust internal callers.
- [ ] DRY violations (3+ copies) factored; no premature abstractions.
- [ ] No banned-list dependencies introduced.
- [ ] If a new dependency: dossier entry merged first.
- [ ] Build passes on all target frameworks.
- [ ] AOT smoke test still passes.

## When in doubt

The system prompt's general guidance is the tiebreaker:

- Don't add features beyond what the task requires.
- Don't add error handling for impossible cases.
- Don't add comments explaining what well-named code already says.
- Don't pre-abstract.
- Validate at boundaries, trust internals.
- Test behaviors, not implementation details.

If a code-review comment cites "best practice" without referencing this doc, push back: either the practice belongs here (then add it via PR) or it doesn't apply (then ignore it).

---

Last reviewed: 2026-05-01 (added dual-layer testing rule).
