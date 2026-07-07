# Deploying NetPdf securely (untrusted-HTML API)

This is the operational hardening guide for the **hostile deployment** — an API that accepts
arbitrary customer HTML and returns a PDF. It is the companion to the
[secure-usage README section](../../README.md#running-netpdf-on-untrusted-html) and
[SECURITY.md](../../SECURITY.md).

NetPdf is structurally immune to several whole bug classes (no JavaScript engine, no browser, no
process spawning, no reflection-driven deserialization) and ships a mature in-process defense set
(SSRF choke point + IP blocklist + DNS-pin, XXE-hardened SVG, pre-decode font/image validation,
layered DoS caps, a PDF-output preflight denylist). **But the library cannot substitute for OS-level
isolation** against a memory-safety 0-day in a native decoder (Skia / HarfBuzz — threat-model class
V5). Defense in depth is required.

## 1. In-process configuration (necessary, not sufficient)

```csharp
var pdf = HtmlPdf.Convert(untrustedHtml, new HtmlPdfOptions
{
    SecurityPolicy = SecurityPolicy.UntrustedHtml, // http/https/file/data all OFF; tight budgets
    ResourceLoader = null,                         // no ambient network / filesystem fetch at all
    Timeout = TimeSpan.FromSeconds(10),            // bound render time (also honors a CancellationToken)
    // BaseUri: leave null — nothing to resolve relative refs against.
});
```

`SecurityPolicy.UntrustedHtml` also caps pages (500) and output size (50 MiB). Tune these
for your workload; keep them as low as your legitimate documents allow.

## 2. OS-level isolation (required for untrusted HTML)

Run each conversion in a **locked-down container** (or an equivalent sandbox). Recommended baseline:

- **No network egress.** Drop all outbound networking (`--network none`, or a deny-all egress policy).
  This is the ultimate SSRF backstop even if a future resource-loading misconfiguration slips through.
- **Read-only, minimal filesystem.** `--read-only` root, a small `tmpfs` for scratch, no host mounts.
  This backstops the local-file-disclosure class (V2) and the symlink-TOCTOU residual below.
- **Memory / CPU / PID limits.** `--memory`, `--cpus`, `--pids-limit` so a decompression/parse bomb or
  a pathological document can't exhaust the host (V7).
- **Drop privileges + capabilities.** Non-root user, `--cap-drop=ALL`, `--security-opt=no-new-privileges`.
- **seccomp / AppArmor.** A restrictive seccomp profile shrinks the kernel attack surface a native
  0-day would need (V5).
- **Ephemeral, one-shot.** Prefer a fresh container per request (or a pool with strict resets); never
  reuse process state across tenants.

Keep the native dependencies patched: subscribe to upstream security advisories for HarfBuzz, libwebp,
libjpeg-turbo, and libpng, and bump the affected NuGet package promptly when a fix ships.

## 3. Accepted residuals (documented, not fixed)

These are known, low-severity residuals accepted for v1; the isolation above is their backstop:

- **Symlink TOCTOU (file:).** Under `SafeDefault`, a `file:` URI's resolved path is validated to lie
  under `BaseUri` with symlink resolution (`File.ResolveLinkTarget(returnFinalTarget: true)`), but a
  time-of-check/time-of-use race remains theoretically possible if the filesystem is mutated between
  the check and the read. Mitigation: a **read-only filesystem** (§2), or simply `UntrustedHtml`
  (which disables `file:` entirely). Not applicable to the untrusted-HTML use case.
- **`AllowedHosts` wildcard scope.** The optional `AllowedHosts` allowlist supports a single-label
  wildcard (`*.example.com` matches `a.example.com`, not `a.b.example.com`). This is a deliberate
  narrow scope; author entries precisely. `AllowedHosts` only applies when HTTP(S) is enabled (i.e.
  not under `UntrustedHtml`).

## 4. Checklist

- [ ] `SecurityPolicy.UntrustedHtml` + `ResourceLoader = null` + a `Timeout`.
- [ ] Container: `--network none`, `--read-only` (+ `tmpfs`), `--memory` / `--cpus` / `--pids-limit`.
- [ ] Non-root, `--cap-drop=ALL`, `--security-opt=no-new-privileges`, seccomp profile.
- [ ] One-shot / reset-between-tenants execution.
- [ ] Native dependencies on their patched floors; CI vulnerable-scan green.
