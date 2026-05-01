---
name: uax-test
description: Validate a UAX algorithm implementation (UAX #9 Bidi, #14 Line Break, #29 Segmentation) against UCD reference test data. Phase 1+.
---

# Skill: uax-test

The Unicode Standard Annexes describe normative algorithms whose conformance is measured against reference test data published by the Unicode Consortium. NetPdf must pass these test files **100%** for the algorithms to be considered correct.

## Inputs (ask the user if not provided)

- **Which UAX**: `9` (Bidi), `14` (Line Break), `29` (Word/Grapheme/Sentence Break), or a specific subset.

## Reference test files (per UAX, downloaded from `https://www.unicode.org/Public/UCD/latest/ucd/auxiliary/`)

| UAX | Test files |
|---|---|
| #9 (Bidi) | `BidiTest.txt`, `BidiCharacterTest.txt` |
| #14 (Line Break) | `LineBreakTest.txt` |
| #29 (Segmentation) | `GraphemeBreakTest.txt`, `WordBreakTest.txt`, `SentenceBreakTest.txt` |

These files live in `src/NetPdf.Text/Data/UcdTests/` (not committed to git as they're large; downloaded by a setup script). Each test file has the format:
```
÷ <code-point-list> ÷    # comment describing the case
```
where `÷` indicates a break and `×` indicates no-break between adjacent code points.

## Steps

1. **Verify UCD test files are present** at `src/NetPdf.Text/Data/UcdTests/`. If not:
   ```
   bash scripts/fetch-ucd-tests.sh
   ```
   (This script may need to be created — it downloads from `https://www.unicode.org/Public/UCD/latest/ucd/auxiliary/` and `https://www.unicode.org/Public/UCD/latest/ucd/`.)

2. **Run the UAX-specific test** in `tests/NetPdf.UnitTests/Text/`:
   ```
   dotnet test tests/NetPdf.UnitTests/NetPdf.UnitTests.csproj -c Release \
     --filter "FullyQualifiedName~Uax<N>" --logger "console;verbosity=normal"
   ```

3. **Parse the test output.** For each failing case:
   - Print the source line from the test file (it has a comment describing the case).
   - Print the expected vs actual break sequence.
   - Note the `# Char:` notation that identifies the failing transition.

4. **Aggregate** by failure type. The test files have thousands of cases; the failures usually cluster around specific Unicode ranges or transitions. Bucket failures by the property class involved (e.g., for Bidi: failures in RLO/LRO context, in implicit-level resolution, etc.).

5. **Report:**
   ```
   UAX #<N> conformance:
     Total cases:        <T>
     Passing:            <P> (<P/T*100>%)
     Failing:            <F>
   
   Top failure clusters:
     1. <pattern>: <count> cases
        Example: <line-from-test-file>
     2. <pattern>: <count> cases
        ...
   ```

6. **Suggest the most likely fix.** UAX failures usually trace to:
   - A property table built from outdated UCD data → re-run the source generator with current UCD.
   - A rule misapplication (e.g., L1 vs L2 in Bidi) → cite the spec section.
   - An edge case in the state machine (line break has many) → add a unit test that captures the case and fix.

## Output

Report only. No code changes. If failures exist, suggest specific spec sections to re-read and example test cases to step through.

## Acceptance gate

For Phase 1 tag (`0.1.0-alpha`):
- UAX #9: **100% pass** on `BidiTest.txt` and `BidiCharacterTest.txt`.
- UAX #14: **100% pass** on `LineBreakTest.txt`.
- UAX #29: **100% pass** on `GraphemeBreakTest.txt` and `WordBreakTest.txt`.

`SentenceBreakTest.txt` is a stretch goal for Phase 1; required by Phase 3.

## Style notes

- These tests are the contract. "We pass 99%" is not acceptable for Phase 1 release; the failing 1% will produce silent rendering bugs in production.
- When UCD data updates (every 6 months or so), re-run this skill to catch regressions in our property tables.
- Don't fudge the test data. Add ignored-test markers only with a documented spec interpretation in `docs/pdf-spec-notes.md` or its UAX equivalent.
