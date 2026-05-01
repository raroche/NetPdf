# Secrets & Credentials

What credentials this project uses, where they live, and how to handle them. **Never paste a credential in chat or commit one to the repo.**

## Active secrets

### `NUGET_API_KEY` — nuget.org publish key

- **Purpose:** authorizes `dotnet nuget push` to publish packages matching the glob `NetPdf*` to nuget.org.
- **Glob scope:** `NetPdf*` — covers `NetPdf` (main package) and all `NetPdf.Languages.*` packs planned for Phase 5.
- **Stored in:** GitHub Actions repository secret on `raroche/NetPdf`. Manage at https://github.com/raroche/NetPdf/settings/secrets/actions .
- **Rotation policy:** rotate on any of:
  - Suspected exposure (chat paste, terminal screenshare, log leak, etc.).
  - Annual review (key has a 365-day expiry by default).
  - Departure of any contributor who had access (n/a today; solo project).
- **How to rotate:** delete the existing key at https://www.nuget.org/account/apikeys , create a new one with the same `NetPdf*` glob, update the GitHub Actions secret value, push a test publish from the workflow.

### `nuget.org` package ID `NetPdf` — reservation

- **Status:** ✅ reserved on `2026-05-01` via the placeholder package `NetPdf 0.0.1-phase0`.
- **Visibility:** version marked **unlisted** — the ID is held but the placeholder doesn't appear in nuget.org search results until the real `1.0.0` ships.
- **Owner:** `raroche` (the only owner; add more contributors on launch if needed).
- **Implications:** anyone trying to publish `NetPdf` to nuget.org now will be blocked by the ID conflict. Phase 5's release workflow pushes the real `1.0.0` against this ID.
- **Prefix reservation** (`NetPdf.*` for the Languages packs): apply for this on nuget.org after `1.0.0` ships. Auto-approved for established package owners with the parent ID. See https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation .

## Future secrets (set up in later phases)

These don't exist yet; capturing here so they're not forgotten.

| Secret | When | Purpose |
|---|---|---|
| `GH_PAT` | Phase 5 | Personal access token if the release workflow needs to push to a separate public repo (the orphan-branch playbook). May not be needed if we just flip the existing repo public. |
| `DOCKER_HUB_TOKEN` | Phase 5 | If we publish the pinned-Chrome reference Docker image to a registry rather than building it in CI. Optional. |
| `CODE_SIGN_CERT` | Post-v1 | If we sign NuGet packages with an Authenticode cert. Not in scope for v1.0. |

## Local development — when do you need a secret on your laptop?

**Almost never.** Between Phase 0 and Phase 5's CI release workflow, all package publishes run in GitHub Actions using the `NUGET_API_KEY` secret. You only need the key on your laptop if you want to do an emergency manual push.

If that case comes up, store the key out-of-band — **not** in `.zshrc` (commits sometimes leak), **not** in a tracked file, **not** in shell history.

Recommended on macOS:

```bash
# One-time: store in Keychain (encrypted at rest, doesn't show in env/ps).
security add-generic-password -s "nuget.org" -a "raroche" -w "<KEY>"

# Use without ever exposing the value:
dotnet nuget push artifacts/NetPdf.X.Y.Z.nupkg \
  --api-key "$(security find-generic-password -s nuget.org -a raroche -w)" \
  --source https://api.nuget.org/v3/index.json
```

Recommended on Linux: an env-var file at `~/.nuget-key` with mode `600`, sourced from your shell rc. Never check into git.

## Things that are NOT secrets

These are public; documenting so we don't accidentally treat them as sensitive:

- The repository URL (`https://github.com/raroche/NetPdf`).
- Package names, version numbers, the Apache-2.0 license text.
- Code itself — it'll all be public at `1.0.0` launch.
- Test corpus content (the corpus files are intentionally public-domain or fair-use).
- Diagnostic codes, compatibility matrix entries.

## Things to NEVER paste in chat / commit / log

- Any string starting with `oy2` followed by alphanumerics — that's a NuGet API key prefix.
- Strings starting with `ghp_`, `gho_`, `ghs_`, `ghu_`, `ghr_` — GitHub token prefixes.
- Anything labeled "secret," "token," "key," "password" without verifying it's a public artifact.

If a credential is exposed in chat (as happened with the initial NuGet key during Phase 0 setup), treat it as compromised immediately and rotate. The chat transcript is a permanent record; the fact that you trust the channel doesn't change that.

## Reporting an exposure

For solo development, the workflow is simple: rotate immediately, log the rotation in this doc's history (via git commit), no further notification needed.

If contributors are added post-v1, this section becomes a security-reporting protocol (e.g., private security advisory on GitHub).

---

Last reviewed: 2026-05-01 (Phase 0).
