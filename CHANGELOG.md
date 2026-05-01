# Changelog

All notable changes to NetPdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Phase 0 — Legal & architecture lock
- Initial scaffolding.
- Apache-2.0 license established.
- Clean-room policy documented (`docs/clean-room-policy.md`).
- Dependency dossier opened (`docs/legal/dependency-dossier.md`).
- Compatibility matrix published (`docs/compatibility-matrix.md`).
- Diagnostics code registry started (`docs/diagnostics-codes.md`).
- PDF spec notes & errata index opened (`docs/pdf-spec-notes.md`).
- Per-phase execution guides under `docs/phases/`.
- Project-scoped skills under `.claude/skills/`.
- `CLAUDE.md` session-bootstrap added at repo root for any future Claude session.
- Secrets-and-credentials policy documented (`docs/secrets-and-credentials.md`).
- Public API surface frozen: `HtmlPdf`, `HtmlPdfOptions`, `PdfRenderResult`, `IResourceLoader`, `IFontResolver`, `IDiagnosticsSink`, `FeatureFlags`, `SecurityPolicy`, `CachePolicy`.
- Solution scaffolding for nine source projects and eleven test projects.
- NuGet package ID `NetPdf` reserved on nuget.org via `0.0.1-phase0` placeholder (unlisted).
- `NUGET_API_KEY` set up as GitHub Actions repository secret with `NetPdf*` glob scope.

[Unreleased]: https://example.invalid/NetPdf/compare/HEAD
