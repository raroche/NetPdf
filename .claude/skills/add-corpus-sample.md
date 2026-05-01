---
name: add-corpus-sample
description: Add a new HTML test sample to tests/NetPdf.RealDocuments/Corpus/. Wires the file into existence checks and (post-Phase 4) visual regression.
---

# Skill: add-corpus-sample

Add an HTML document to the test corpus so it's exercised on every test run. The corpus drives layout, pagination, and visual-regression validation across phases.

## Inputs (ask the user if not provided)

- **Source**: a file path or pasted HTML content.
- **Category**: `Invoices`, `Statements`, `Contracts`, `Reports`, `Certificates`, `Catalogs`, or `DenseTables`. Create the folder if it doesn't exist.
- **Phase target**: which phase should render this correctly? (Most: Phase 3 = `0.7.0-beta`. Visual fidelity polish: Phase 4 = `0.9.0-rc1`.)
- **Notes**: what CSS features the sample exercises (one short paragraph).

## Steps

1. **Pick a filename.** `<NN>-<short-slug>.html` where `<NN>` continues the existing numbering in the category folder (`01`, `02`, ...). Keep slug short and descriptive: `01-classic-pure-css.html`, `04-anvil-running-elements.html`.

2. **Save the file** to `tests/NetPdf.RealDocuments/Corpus/<Category>/<NN>-<slug>.html`.
   - If the source is pasted content, fix obvious paste artifacts (e.g., markdown `[text](url)` should be HTML `<a href="url">text</a>`).
   - Strip non-essential analytics scripts but keep `<script>` tags that the corpus is specifically meant to demonstrate (e.g., Tailwind via CDN).

3. **Update the category README** at `tests/NetPdf.RealDocuments/Corpus/<Category>/README.md`:
   - Add a section `### \`<NN>-<slug>.html\``.
   - List CSS features exercised.
   - Note the target phase.

4. **Wire into the existence smoke test** at `tests/NetPdf.RealDocuments/PhaseZeroSmoke.cs`:
   - Add a new `[InlineData("Corpus/<Category>/<NN>-<slug>.html", 1024)]` line to `Corpus_File_Exists_And_Is_Non_Trivial`.
   - The 1024-byte minimum is a sanity check; bump if the file is larger than that.

5. **Verify the corpus is copied to test output.** The category folder is matched by the existing wildcard in `NetPdf.RealDocuments.csproj` (`<None Include="Corpus\**\*.*" CopyToOutputDirectory="PreserveNewest" />`). No csproj change needed unless adding a new top-level category folder (then it's already covered).

6. **Build + test:**
   ```
   dotnet test tests/NetPdf.RealDocuments/NetPdf.RealDocuments.csproj -c Release
   ```
   Verify the new `[InlineData]` smoke check passes.

7. **(Phase 4+ only) Generate a Chromium reference PNG.** Run the pinned-Chrome Docker image's reference generator on the new file; commit the resulting PNG to `tests/NetPdf.RenderingCorpus/References/<Category>/<NN>-<slug>.png`.

## Output

The corpus has a new file with documentation. Future test runs verify it exists and (post-Phase 4) renders within visual-regression tolerance.

## Style notes

- **Keep external resources stable.** A sample that depends on a third-party CDN that may go offline is brittle. Prefer inlining critical resources (`data:` URIs for small images, inline `<style>`).
- **One sample, one purpose.** A sample that exercises 20 features at once is hard to triage when it breaks. Prefer focused samples; combine in dedicated stress-test samples only.
- **Document Tailwind/JS-required limitations.** If a sample requires JS to render correctly (Tailwind via CDN), the README must say so — it's a known limitation, not a regression.
