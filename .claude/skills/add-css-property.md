---
name: add-css-property
description: Add a new CSS property to NetPdf's property table — properties.json, source-generated PropertyId enum, ComputedStyle accessor, parser hook, test. Phase 2+.
---

# Skill: add-css-property

NetPdf's CSS property table is the single source of truth for every property's name, type, default value, inheritance behavior, and parser/computed-value hooks. The source generator (`NetPdf.SourceGen`) reads `properties.json` and emits a `PropertyId` enum, a `FrozenDictionary<string, PropertyId>` lookup, and per-property typed accessors on `ComputedStyle`.

## Inputs (ask the user if not provided)

- **Name**: CSS name as it appears in stylesheets (e.g., `padding-top`, `letter-spacing`).
- **Type**: one of `length`, `length_percent`, `color`, `keyword`, `integer`, `number`, `string`, `url`, `image`, `gradient`, `shadow_list`, `transform_list`, `font_family_list`, `custom`.
- **Default value**: spec-defined initial value (e.g., `0`, `auto`, `currentcolor`).
- **Inherits**: `true` or `false`.
- **Applies to**: `all`, `block_level`, `inline_level`, `flex_items`, `grid_items`, `table_*`, etc. (per-spec).
- **Computed-value notes**: any non-trivial resolution behavior (e.g., percentages resolve against parent width).

## Steps

1. **Append to `properties.json`** (will live at `src/NetPdf.SourceGen/properties.json` once Phase 2 starts; create if not yet present). Schema:
   ```json
   {
     "name": "padding-top",
     "id": "PaddingTop",
     "type": "length_percent",
     "default": "0",
     "inherit": false,
     "applies_to": "all_block_inline_replaced",
     "computed_notes": "percentage resolves against containing block width"
   }
   ```
   Keep the array sorted by `name` for diff-friendliness.

2. **Rebuild the source generator output:**
   ```
   dotnet build src/NetPdf.SourceGen/NetPdf.SourceGen.csproj -c Release
   dotnet build src/NetPdf.Css/NetPdf.Css.csproj -c Release
   ```
   The generator emits new entries in `PropertyId`, `PropertyMetadata`, and the appropriate `ComputedStyle` accessor.

3. **Wire the parser hook** if the property has non-trivial parsing. Add a method in `src/NetPdf.Css/Parser/ValueParsers.cs`:
   ```csharp
   internal static PaddingTopValue ParsePaddingTop(ReadOnlySpan<TokenSpan> tokens) { ... }
   ```
   Register it in the property metadata table (the source generator picks up the registration by convention).

4. **Wire the computed-value resolver** if non-trivial. Add a method in `src/NetPdf.Css/ComputedValues/Resolvers.cs`:
   ```csharp
   internal static long ResolvePaddingTop(in SpecifiedValue specified, in ResolutionContext ctx) { ... }
   ```

5. **Add unit tests.** In `tests/NetPdf.UnitTests/Css/Properties/PaddingTopTests.cs`:
   - Parse: every accepted value form (px, em, %, calc, var).
   - Compute: percentage resolution, calc resolution, var fallback.
   - Cascade: inheritance behavior matches `inherit` flag.

6. **Add a snapshot test** in `tests/NetPdf.LayoutSnapshots/` if the property affects layout (most do). A minimal HTML+CSS sample → expected `BoxTree` JSON.

7. **Update the compatibility matrix** at [docs/compatibility-matrix.md](../../docs/compatibility-matrix.md) if this is a notable property (most aren't — only flag if it implements a new CSS feature, not just a new property).

8. **Build + test:**
   ```
   dotnet build NetPdf.slnx -c Release
   dotnet test NetPdf.slnx -c Release
   ```

## Output

The property is now parsed, computed, and exposed via `ComputedStyle`. Layouters can read it via the typed accessor.

## Style notes

- **Don't invent new types.** If your property's value doesn't fit existing types, push back: probably the type list is incomplete and we should expand it intentionally.
- **Computed-value resolution must be deterministic.** Same input → same output, every run.
- **Test specificity edge cases.** A property with `inherit: false` but interacting with `currentcolor` (which inherits via the `color` property) has subtle behavior.
