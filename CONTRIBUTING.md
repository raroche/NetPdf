# Contributing to NetPdf

Thanks for your interest in NetPdf — a pure C# / .NET 10 HTML+CSS-to-PDF rendering engine (Apache-2.0). This
guide covers how to build, test, and submit changes.

## Prerequisites

- **.NET 10 SDK** (the repo targets `net10.0` / C# 14).
- A platform NetPdf supports: Linux, macOS, or Windows (x64 or arm64).
- On slim Linux containers, install **fontconfig** (Skia's raster fallback links it) — see the README's
  "Linux slim containers" note.

## Build, test, run

```bash
dotnet build NetPdf.slnx -c Release
dotnet test  NetPdf.slnx -c Release --nologo
dotnet run --project samples/invoice-cli/InvoiceCli.csproj -c Release -- input.html out.pdf
```

Note the solution is `NetPdf.slnx` (the .NET 10 XML solution format), not a classic `.sln`.

## Non-negotiable rules

Every change must honor these — they are enforced by review and CI:

1. **Clean-room development.** Implement algorithms from W3C / ISO / UAX specs, never by copying another
   engine's source. If you read another implementation for *understanding*, note it in a one-line comment.
   See [`docs/clean-room-policy.md`](docs/clean-room-policy.md).
2. **No banned dependencies.** No `System.Drawing`, no browser engines, no AGPL/copyleft, no revenue-capped
   libraries. A new dependency needs a reviewed entry in
   [`docs/legal/dependency-dossier.md`](docs/legal/dependency-dossier.md) before the `PackageVersion` lands.
3. **AOT-clean.** No reflection in core paths (`Activator.CreateInstance`, `Type.GetType(string)`, runtime
   codegen). Use source generators where dynamic registration would otherwise be needed.
4. **Determinism.** Same input → same PDF bytes. No PRNG, no `DateTime.Now` in shipped code.
5. **Apache-2.0 file header** on every source file (see any existing `.cs`).
6. **Diagnostics, not silent corruption.** Unsupported features emit a stable code from
   [`docs/diagnostics-codes.md`](docs/diagnostics-codes.md) — never drop content silently.

Full coding standards (SOLID, DRY-with-rule-of-three, YAGNI, naming, testing) live in
[`docs/coding-standards.md`](docs/coding-standards.md).

## Tests

Every shipped behavior gets **both** a unit test and an integration test. Snapshot / golden byte-identity
tests guard rendering output — if your change intentionally alters output, re-pin the affected goldens and
explain why in the PR. Run the full suite (`dotnet test NetPdf.slnx -c Release`) before opening a PR.

## Pull requests

- Keep PRs focused; one logical change per PR.
- Fill in the pull-request template checklist.
- Reference the issue or `docs/deferrals.md` item you're addressing, if any.
- CI must be green (build, tests, AOT smoke, benchmark and security gates).

## Reporting bugs / requesting features

Open an issue using the templates. For **security vulnerabilities**, do **not** open a public issue — follow
[`SECURITY.md`](SECURITY.md) (GitHub → *Security* tab → *Report a vulnerability*).

## License

By contributing, you agree that your contributions are licensed under the Apache License, Version 2.0.
