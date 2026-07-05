<!--
Copyright 2026 Roland Aroche and NetPdf contributors.
Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
-->

# NetPdf release runbook (v1.0.0 and onward)

The actionable maintainer checklist for cutting a NetPdf release. Phase-5 task 23.
The mechanics (packing, the six packages, the publish job) are automated by
[`.github/workflows/release.yml`](../.github/workflows/release.yml) (task 24); this
runbook is the human sequence around it.

> **Why a runbook and not a button.** Tagging `v1.0.0`, promoting the CHANGELOG, and
> publishing to NuGet.org are **irreversible, outward-facing** actions. They are the
> maintainer's to take, on purpose — this file makes the sequence repeatable and the
> guards explicit, but does not fire any of them.

## One-time setup (before the first publish)

- [ ] **NuGet API key.** Create a scoped push key on nuget.org for the `NetPdf` +
      `NetPdf.Languages.*` package IDs.
- [ ] **GitHub Environment.** Create an environment named `nuget-release`
      (Settings → Environments). Add the key as the `NUGET_API_KEY` secret **on that
      environment**, and add a required reviewer so the publish job pauses for a manual
      approval before it pushes.
- [ ] **Pages / benchmark remainders** (independent of publish, tracked in
      [phase-5-packaging-and-release.md](phases/phase-5-packaging-and-release.md)):
      enable GitHub Pages + `NETPDF_PAGES_ENABLED=true`; commit the Linux Chrome
      reference PNGs + a `linux-x64` benchmark baseline so the visual-regression and
      benchmark gates flip from neutral to enforcing.

## Cutting a release

1. **Branch.** `git checkout -b release/X.Y.Z` from `main`.
2. **Bump the version in the SAME commit** across the three surfaces the
   `ReleaseVersionParityTests` guard pins together — a mismatch fails the build:
   - `Directory.Build.props` — set `<VersionPrefix>X.Y.Z</VersionPrefix>` and clear
     `<VersionSuffix>` (empty for a stable release; e.g. `rc1` only for pre-releases).
   - `build/version.json` — set `"version": "X.Y.Z"` (and update `phase` / `lastUpdated`).
   - `CHANGELOG.md` — promote `## [Unreleased]` to `## [X.Y.Z] — <date>`, then start a
     fresh empty `## [Unreleased]`. Re-base the link-reference footer so `[Unreleased]`
     compares `X.Y.Z...HEAD` and add the `[X.Y.Z]: …/compare/<prev>...X.Y.Z` link (the
     second `ReleaseVersionParityTests` fact enforces this footer shape).
3. **Package validation baseline (v1.0.1+ only).** For the first release (`1.0.0`) leave
   `PackageValidationBaselineVersion` unset — there is no prior package to diff against.
   From `1.0.1` on, set `<PackageValidationBaselineVersion>1.0.0</PackageValidationBaselineVersion>`
   (or the last shipped version) in `Directory.Build.props` so the build fails on an
   accidental public-API break vs. the last release.
4. **Open the release PR.** Run the full CI matrix; every enforcing gate must be green
   (`linux-x64`, `windows-x64`, `macos-arm64`, security, dependency-scan,
   visual-regression, benchmark).
5. **Merge** the PR to `main`.
6. **Tag.** `git tag vX.Y.Z <merge-commit> && git push origin vX.Y.Z`. The tag version
   MUST equal the packed version — `release.yml` fails the publish if they disagree.
7. **Approve the publish.** The `release` workflow runs `validate` (build + test + pack
   the six packages + verify the exact package/symbol set at the tag version + upload
   artifacts) with NO environment, then holds the `publish` job on the `nuget-release`
   environment's required-reviewer gate. Review the `validate` run's verified artifact
   list, then approve — `publish` downloads those exact artifacts and pushes both the
   `.nupkg` and `.snupkg` with `--skip-duplicate` (idempotent). Approval therefore
   happens *after* validation, never before.
8. **Verify on nuget.org.** Confirm all six packages (`NetPdf`, `NetPdf.Languages.European`,
   `NetPdf.Languages.Cjk`, `NetPdf.Languages.Indic`, `NetPdf.Languages.Arabic`,
   `NetPdf.Languages.All`) are listed at `X.Y.Z`. Spot-check one in NuGet Package Explorer:
   Apache-2.0 SPDX license expression, bundled README + NOTICE + THIRD-PARTY-NOTICES,
   Source Link metadata in the PDB, source embedded in the `.snupkg`.
9. **GitHub release.** Publish a release for `vX.Y.Z` with notes generated from the
   promoted `[X.Y.Z]` CHANGELOG section.
10. **v1.0 launch only — public flip.** Push the clean orphan-branch initial commit to
    the fresh public repo and make it public (see
    [phase-5-packaging-and-release.md](phases/phase-5-packaging-and-release.md)).

## Rollback

NuGet packages cannot be deleted, only **unlisted** (Manage → Unlist) — a published
version is permanent. If a release is broken, unlist it and ship a fixed `X.Y.(Z+1)`;
never attempt to re-push the same version with different bytes.
