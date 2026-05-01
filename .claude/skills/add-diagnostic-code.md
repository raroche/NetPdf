---
name: add-diagnostic-code
description: Add a new stable diagnostic code to NetPdf — registry doc, constant, test. Use when emitting a new warning/info from any pipeline stage.
---

# Skill: add-diagnostic-code

A diagnostic code is a stable `CATEGORY-FEATURE-CONDITION-001` identifier emitted via `IDiagnosticsSink` or aggregated into `PdfRenderResult.Warnings`. Once published, the meaning of a code never changes; new conditions get new codes.

## Inputs (ask the user if not provided)
- **Code**: `<CATEGORY>-<FEATURE>-<CONDITION>-001`. Categories in use: `HTML`, `CSS`, `FONT`, `RES`, `PDF`, `PAGINATION`, `INTERNAL`. Use `001` suffix unless this is a follow-up to an existing code.
- **Severity**: `Info`, `Warning`, or `Error`.
- **Message template**: human-readable, may include `{0}` placeholders.
- **When emitted**: the condition that triggers it (e.g., "encountered `position: sticky`").

## Steps

1. **Update [docs/diagnostics-codes.md](../../docs/diagnostics-codes.md).**
   - Find the right category section (`## CSS-*`, `## FONT-*`, etc.).
   - Add a row: `| `<code>` | <Severity> | <Message describing when emitted> |`.
   - Keep alphabetical-by-code order within section.

2. **Add the constant.** If `src/NetPdf/Diagnostics/DiagnosticCodes.cs` doesn't exist, create it with the Apache-2.0 header and a `public static class DiagnosticCodes`. Add:
   ```csharp
   public const string <PascalCaseName> = "<CODE>";
   ```
   Group constants by category with `// region`/`// endregion` comments.

3. **Add a unit test.** In `tests/NetPdf.UnitTests/Diagnostics/DiagnosticCodesTests.cs` (create if needed):
   - Test that the constant value matches the doc.
   - When the emission point is implemented, add an integration test asserting the code emits at the right call site with the expected severity.

4. **Update the compatibility matrix** at [docs/compatibility-matrix.md](../../docs/compatibility-matrix.md) if this code is tied to an unsupported feature there. Add the code reference next to the feature row.

5. **Build + test:**
   ```
   dotnet build NetPdf.slnx -c Release
   dotnet test tests/NetPdf.UnitTests/NetPdf.UnitTests.csproj -c Release
   ```
   Both must pass.

## Output

The new code is now part of the public stable API. Document why it was added in the commit message body.

## Style notes

- **One code per condition.** Don't reuse a code with different severity.
- **Match existing wording.** Read 5 nearby codes for tone before writing the new one.
- **No PII / paths in messages.** Source location goes in `Diagnostic.Location`, not the message.
