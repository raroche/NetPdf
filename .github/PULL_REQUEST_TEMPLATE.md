<!-- Thanks for contributing to NetPdf! Please fill in the sections below. -->

## What & why

<!-- What does this change do, and what problem / issue / deferral does it address? -->

Closes #

## How

<!-- A short summary of the approach. Note any spec sections (W3C / ISO / UAX) the algorithm follows. -->

## Checklist

- [ ] Builds clean: `dotnet build NetPdf.slnx -c Release`
- [ ] Tests pass: `dotnet test NetPdf.slnx -c Release --nologo`
- [ ] Added/updated **both** unit and integration coverage for the behavior changed
- [ ] Byte-identity goldens/snapshots are unchanged, **or** intentionally re-pinned with a reason below
- [ ] No banned dependencies added (or a reviewed `docs/legal/dependency-dossier.md` entry is included)
- [ ] AOT-clean: no reflection in core paths
- [ ] Deterministic: same input → same PDF bytes
- [ ] New unsupported-feature paths emit a diagnostic code (no silent content drops)
- [ ] Apache-2.0 header on any new source files

## Golden re-pin justification (if any)

<!-- If snapshot/golden output changed, explain why the new output is correct. -->
