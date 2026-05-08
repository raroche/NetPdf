// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading;
using System.Threading.Tasks;

namespace NetPdf;

/// <summary>
/// Per Phase D D-1 — the only valid pipeline entry point for resource
/// fetches. Wraps a user-supplied <see cref="IResourceLoader"/> + applies
/// every safety check the NetPdf threat model requires:
///
/// <list type="number">
///   <item><description><b>Per-render budget</b> via <see cref="ResourceFetchContext"/>:
///   <c>MaxResourcesPerRender</c> (count cap),
///   <c>MaxTotalResourceBytes</c> (cumulative byte cap).</description></item>
///   <item><description><b>URI safety</b> via <see cref="UriSafetyValidator.Validate"/>:
///   scheme allowlist (file/data/http/https per <see cref="SecurityPolicy"/>),
///   IP blocklist for HTTP(S) (loopback, private, link-local incl.
///   AWS/GCE/Azure metadata 169.254.169.254, IPv6 ULA, IPv4-mapped),
///   <c>AllowedHosts</c> filter.</description></item>
///   <item><description><b>file: base-path check</b> when the URI returns
///   <see cref="UriSafetyValidator.SafetyOutcome.RequiresBasePathCheck"/>:
///   the resolved file path is verified to lie under
///   <see cref="ResourceFetchContext.BaseUri"/>'s directory subtree
///   (canonicalized to defeat <c>../</c> traversal).</description></item>
///   <item><description><b>Per-resource size cap</b> from
///   <see cref="SecurityPolicy.MaxResourceBytes"/> (post-fetch validation;
///   pre-fetch byte estimate not always available from the user loader).</description></item>
///   <item><description><b>MIME allowlist per <see cref="ResourceKind"/></b>
///   so an <c>&lt;img src=...&gt;</c> serving <c>text/html</c> can't
///   route through the image decoder.</description></item>
///   <item><description><b>Per-fetch timeout</b> via
///   <see cref="SecurityPolicy.ResourceTimeout"/> linked to the
///   conversion's cancellation token.</description></item>
/// </list>
///
/// <para><b>Why this wrapper exists.</b> The user-facing
/// <see cref="IResourceLoader"/> contract is intentionally narrow (raw
/// byte fetch). If any caller in the pipeline went directly to
/// <see cref="HtmlPdfOptions.ResourceLoader"/>, that caller would bypass
/// every defense above. <see cref="SafeResourceLoader"/> is the only
/// pipeline-internal entry point — when Phase 5 wires resource fetching,
/// every consumer (CSS parser for <c>url()</c> / <c>@import</c> /
/// <c>@font-face</c>, image decoder, font resolver) calls THIS, never
/// the underlying <see cref="IResourceLoader"/> directly.</para>
///
/// <para><b>NetPdf v1 ships no default loader.</b> When
/// <see cref="HtmlPdfOptions.ResourceLoader"/> is null, every fetch
/// returns a <see cref="ResourceFailure"/> immediately without invoking
/// any logic — the wrapper still exists so the contract is consistent
/// for Phase 5's wireup.</para>
/// </summary>
public sealed class SafeResourceLoader
{
    private readonly IResourceLoader? _inner;
    private readonly ResourceFetchContext _context;

    public SafeResourceLoader(IResourceLoader? inner, ResourceFetchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Per post-Task-7 review (recommendation P1 #1) — when the
        // inner loader is a SafeHttpResourceLoader, both this wrapper
        // AND the inner loader make policy-dependent decisions
        // (scheme / IP / AllowedHosts at the wrapper layer; redirect
        // hops / per-resource bytes inside the loader). If the two
        // policies diverge, redirects / AllowedHosts could be checked
        // against a different rule set than the initial URI — a
        // silent security-correctness gap.
        //
        // Reject the misconfig fail-fast at construction. The factory
        // CreateWithSafeHttp wires both with the same policy.
        // ReferenceEquals (not value equality) is intentional: callers
        // that genuinely want to share a policy SHOULD share the
        // instance; constructing two distinct SecurityPolicy objects
        // with identical fields is almost always an oversight.
        if (inner is SafeHttpResourceLoader safeHttp
            && !ReferenceEquals(safeHttp.Policy, context.Policy))
        {
            throw new ArgumentException(
                "SafeHttpResourceLoader's SecurityPolicy must be the same "
                + "instance as ResourceFetchContext.Policy. Pre-fix the two "
                + "could diverge silently — the wrapper would validate "
                + "scheme / IP / AllowedHosts against context.Policy while "
                + "the loader validated redirects + per-resource-bytes "
                + "against its own policy. Use "
                + "SafeResourceLoader.CreateWithSafeHttp(context) to "
                + "construct both with the context's policy.",
                nameof(inner));
        }
        _inner = inner; // null is valid — see class docs
        _context = context;
    }

    /// <summary>Per post-Task-7 review (recommendation P1 #1) — factory
    /// that constructs a <see cref="SafeHttpResourceLoader"/> using the
    /// <paramref name="context"/>'s <see cref="SecurityPolicy"/> + wraps
    /// it in a <see cref="SafeResourceLoader"/> bound to the same
    /// context. Single source of truth for the policy across both
    /// layers; eliminates the divergence risk entirely.
    ///
    /// <para>The returned wrapper does NOT take ownership of the
    /// returned HTTP loader's lifetime. Callers who construct via this
    /// factory should track the underlying loader separately if they
    /// need <see cref="IDisposable.Dispose"/> on shutdown — typically
    /// by reading the <c>UnderlyingHttpLoader</c> property off the
    /// returned <see cref="SafeResourceLoaderWithHttp"/> bundle. (For
    /// most v1 use cases the loader lives for the process lifetime.)</para></summary>
    public static SafeResourceLoaderWithHttp CreateWithSafeHttp(ResourceFetchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = new SafeHttpResourceLoader(context.Policy);
        var wrapper = new SafeResourceLoader(http, context);
        return new SafeResourceLoaderWithHttp(wrapper, http);
    }

    /// <summary>Fetch a resource with all the Phase D defenses applied.
    /// Returns the loaded bytes on success; a <see cref="ResourceFailure"/>
    /// shape on rejection. Slot reservation happens AFTER the URI safety
    /// + base-path checks so a fast-rejected fetch doesn't consume a
    /// budget slot — an attacker could otherwise probe the allowlist by
    /// firing N+1 obviously-invalid URIs to lock out legitimate fetches.
    /// </summary>
    public async ValueTask<SafeResourceResult> FetchAsync(Uri uri, ResourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _context.CancellationToken.ThrowIfCancellationRequested();

        // Per PR #18 Copilot review #1 — relative URIs would throw on
        // .Scheme access inside UriSafetyValidator.Validate. Reject
        // explicitly + return a typed failure rather than letting
        // System.InvalidOperationException escape the wrapper. This
        // mirrors the loader's "every error is a typed failure"
        // contract from review #7. If the caller wanted relative-to-
        // BaseUri resolution, they can do that at their layer; the
        // wrapper sees only absolute URIs.
        if (!uri.IsAbsoluteUri)
        {
            return SafeResourceResult.Failed(uri, kind,
                "relative URI; SafeResourceLoader requires an absolute URI (resolve against HtmlPdfOptions.BaseUri at the caller)");
        }

        // 1. URI safety check (scheme + IP blocklist + AllowedHosts).
        var uriVerdict = UriSafetyValidator.Validate(uri, _context.Policy);
        if (uriVerdict.Outcome == UriSafetyValidator.SafetyOutcome.Unsafe)
        {
            return SafeResourceResult.Failed(uri, kind, $"URI safety check: {uriVerdict.Reason}");
        }

        // 2. file: scheme follow-up — verify resolved path under BaseUri.
        if (uriVerdict.Outcome == UriSafetyValidator.SafetyOutcome.RequiresBasePathCheck)
        {
            if (!IsFileUriUnderBaseUri(uri, _context.BaseUri, out var pathReason))
            {
                return SafeResourceResult.Failed(uri, kind,
                    $"file: URI failed base-path check: {pathReason}");
            }
        }

        // 3. Reserve a budget slot AFTER the URI / base-path checks.
        // Per PR #18 Copilot review #3 — pre-fix the docstring claimed
        // rejected fetches consumed a slot, but the implementation
        // returned without consuming one. The behavior was correct:
        // the wrapper should NOT charge fetches that fail policy
        // checks (otherwise an attacker could exhaust the budget by
        // probing). Doc updated to match.
        // Per-resource bytes-budget is charged post-fetch via
        // TryAddBytes (the byte count is unknown until the loader
        // returns).
        var slotReason = _context.TryReserveSlot();
        if (slotReason is not null)
        {
            return SafeResourceResult.Failed(uri, kind, $"per-render budget: {slotReason}");
        }

        // 4. No user loader = nothing to fetch. Phase 5 will wire actual
        // fetching; until then every fetch resolves to "not found" so the
        // pipeline degrades gracefully rather than throwing.
        if (_inner is null)
        {
            return SafeResourceResult.Failed(uri, kind,
                "no IResourceLoader configured (NetPdf v1 ships no default loader)");
        }

        // 5. Per-fetch timeout — link the per-render cancellation token
        // with a timeout-bounded CTS so a hung loader can't sit forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_context.CancellationToken);
        timeoutCts.CancelAfter(_context.Policy.ResourceTimeout);

        ResourceResponse response;
        try
        {
            response = await _inner.LoadAsync(uri, kind, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
            && !_context.CancellationToken.IsCancellationRequested)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader timeout ({_context.Policy.ResourceTimeout.TotalSeconds}s)");
        }
        catch (OperationCanceledException) when (_context.CancellationToken.IsCancellationRequested)
        {
            // Caller cancellation — propagate up the stack so the
            // pipeline halts. NEVER swallow caller cancellation.
            throw;
        }
        // Per PR #18 review #7 — trap expected loader / I/O exceptions
        // so a misbehaving user loader can't crash the whole render.
        // For untrusted-HTML pipelines, attacker-controlled URLs +
        // attacker-controlled response behavior should land as
        // SafeResourceResult.Failed (degraded render with a diagnostic),
        // not as a thrown exception that aborts the conversion. The
        // catch list covers the standard HTTP / I/O / format-error
        // surfaces a real loader emits; everything else (e.g.,
        // OutOfMemoryException, thread abort, AccessViolationException)
        // bubbles up.
        catch (System.Net.Http.HttpRequestException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader HTTP error: {SanitizeExceptionMessage(ex.Message)}");
        }
        catch (System.IO.IOException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader I/O error: {SanitizeExceptionMessage(ex.Message)}");
        }
        catch (System.IO.InvalidDataException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader returned invalid data: {SanitizeExceptionMessage(ex.Message)}");
        }
        catch (UriFormatException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader URI format error: {SanitizeExceptionMessage(ex.Message)}");
        }
        catch (TimeoutException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader timeout: {SanitizeExceptionMessage(ex.Message)}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader socket error: {SanitizeExceptionMessage(ex.Message)}");
        }
        catch (System.Net.WebException ex)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"loader web error: {SanitizeExceptionMessage(ex.Message)}");
        }

        // Per PR #18 Copilot review #2 — IResourceLoader's contract
        // (per ResourceResponse's docstring) is that an empty Content
        // means "the resource was not found". Pre-fix this code
        // surfaced empty bytes as a successful Loaded result, which
        // pushed empty buffers into downstream image / font decoders
        // (which then either crashed or silently produced blank
        // output). Surface the not-found case as a typed failure so
        // the caller sees one clear "resource missing" diagnostic.
        if (response.Content.Length == 0)
        {
            return SafeResourceResult.Failed(uri, kind,
                "loader returned empty content (per IResourceLoader contract: not found)");
        }

        // 6. Per-resource size cap.
        if (response.Content.Length > _context.Policy.MaxResourceBytes)
        {
            return SafeResourceResult.Failed(uri, kind,
                $"resource size {response.Content.Length} exceeds per-resource cap {_context.Policy.MaxResourceBytes}");
        }

        // 7. MIME allowlist per kind. Loader can return null/empty MIME;
        // we accept that (some sources lack Content-Type) but reject any
        // explicit MIME outside the kind's allowlist.
        if (!string.IsNullOrEmpty(response.MimeType)
            && !IsMimeAllowedForKind(response.MimeType, kind))
        {
            return SafeResourceResult.Failed(uri, kind,
                $"MIME type '{response.MimeType}' not in allowlist for {kind} resource");
        }

        // 8. Charge the actual byte count against the per-render byte
        // budget (slot was already reserved at step 3).
        var bytesReason = _context.TryAddBytes(response.Content.Length);
        if (bytesReason is not null)
        {
            return SafeResourceResult.Failed(uri, kind, $"per-render budget: {bytesReason}");
        }

        return SafeResourceResult.Loaded(response);
    }

    /// <summary>Per-kind MIME allowlist. Per Phase D D-1 — declines a
    /// fetch when the loader's MIME doesn't match the requested resource
    /// kind. Defends against the "image/png Content-Type → SVG payload
    /// → SSRF" + "image-as-stylesheet" classes of polyglot attacks.
    /// Returns true when MIME is null/empty (some sources lack
    /// Content-Type; the kind-specific decoder still validates magic
    /// bytes).
    ///
    /// <para><b>Per PR #18 review #2 — SVG removed from the image
    /// allowlist.</b> SVG is XML, not a magic-byte image format
    /// <see cref="NetPdf.Pdf.Images.ImageSafetyValidator"/> can validate;
    /// it can also carry script (<c>&lt;script&gt;</c>), event handlers
    /// (<c>onload</c>), animation that mutates href / src to dangerous
    /// schemes (Phase B B-5 strips inline SVG only), external references
    /// (<c>&lt;use href&gt;</c>, <c>&lt;image href&gt;</c>), and XML
    /// parser exposure. Once Phase 5 wires resource loading, an
    /// <c>&lt;img src="evil.svg"&gt;</c> referencing a fetched SVG would
    /// land in the rendering pipeline without a dedicated sanitizer.
    /// SVG support requires its own pipeline (parse → sanitize → re-emit
    /// as a sanitized SVG, OR rasterize to PNG before insertion); until
    /// that lands as a separate task, <c>image/svg+xml</c> is rejected
    /// at the MIME gate so the attack surface stays bounded.</para></summary>
    internal static bool IsMimeAllowedForKind(string mime, ResourceKind kind)
    {
        if (string.IsNullOrEmpty(mime)) return true;
        // Strip parameters (e.g., "text/css; charset=utf-8" → "text/css").
        var semi = mime.IndexOf(';');
        var bare = (semi < 0 ? mime : mime[..semi]).Trim().ToLowerInvariant();
        return kind switch
        {
            // Per PR #18 review #2 — image/svg+xml DELIBERATELY OMITTED
            // until a static-SVG sanitizer/renderer pipeline owns
            // external SVG. Phase 5 must add explicit SVG handling
            // (parse + sanitize + safe-rasterize) before re-enabling.
            ResourceKind.Image => bare is "image/png" or "image/jpeg" or "image/gif"
                or "image/webp" or "image/bmp",
            // Fonts: standard application/font-* + font/* per RFC 8081 +
            // legacy MIME types still common in the wild.
            ResourceKind.Font => bare is "font/ttf" or "font/otf" or "font/woff"
                or "font/woff2" or "application/font-woff" or "application/font-woff2"
                or "application/x-font-ttf" or "application/x-font-otf"
                or "application/octet-stream" /* common fallback */,
            ResourceKind.Stylesheet => bare is "text/css",
            ResourceKind.Other => true, // user-defined kind; trust the caller's mapping
            _ => false,
        };
    }

    /// <summary>Verify the file: URI's resolved path lies under
    /// <paramref name="baseUri"/>'s directory subtree, after both
    /// lexical canonicalization (<c>../</c> traversal) AND symlink
    /// resolution. Defends against <c>../../etc/passwd</c> + symlink-
    /// based escapes (a symlink inside the template directory pointing
    /// to <c>/etc</c> would have passed the lexical-only check pre-fix).
    /// Per Phase D D-1 + PR #18 review #5.</summary>
    /// <remarks>
    /// <para>Symlink defense: <c>System.IO.Path.GetFullPath</c>
    /// only canonicalizes lexical traversal (<c>foo/../bar</c> →
    /// <c>bar</c>). On Linux + macOS, <c>/docs/secrets</c> can be a
    /// symlink to <c>/etc/passwd</c>; <c>GetFullPath</c> returns
    /// <c>/docs/secrets</c> unchanged + the prefix check passes
    /// (it IS under <c>/docs/</c>). The fix uses
    /// <c>System.IO.File.ResolveLinkTarget</c> with
    /// <c>returnFinalTarget: true</c> to walk the symlink chain to its
    /// real target, then re-validates the prefix. Returns false if the
    /// resolved target leaves the base subtree.</para>
    /// <para>Path comparison: <see cref="StringComparison.Ordinal"/> on
    /// POSIX file systems (case-sensitive) +
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> on Windows
    /// (typically case-insensitive). Selected via
    /// <see cref="OperatingSystem.IsWindows"/>.</para>
    /// </remarks>
    internal static bool IsFileUriUnderBaseUri(Uri uri, Uri? baseUri, out string reason)
    {
        if (baseUri is null || !baseUri.IsFile)
        {
            reason = "BaseUri is not configured as a file: URI; under-base check cannot pass";
            return false;
        }
        try
        {
            var requestedPath = System.IO.Path.GetFullPath(uri.LocalPath);
            // Anchor the base at the directory containing the BaseUri's
            // file (typical: index.html lives in /docs/, allowed reads under /docs/).
            var baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(baseUri.LocalPath))
                ?? string.Empty;
            // Append directory separator so /foo/index.html doesn't match
            // /foo-bar/secret.txt (prefix collision without separator).
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (!baseDir.EndsWith(sep)) baseDir += sep;

            // Per PR #18 review #5 — pick comparer per OS. Windows file
            // systems are typically case-insensitive (NTFS, FAT); POSIX
            // is case-sensitive. Match the OS to avoid both false
            // positives (Linux: /Foo vs /foo) and false negatives
            // (Windows: /Docs/ vs /docs/).
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // Lexical check first — cheap rejection before disk I/O.
            if (!requestedPath.StartsWith(baseDir, pathComparison))
            {
                reason = $"resolved path '{requestedPath}' is not under base directory '{baseDir}'";
                return false;
            }

            // Per PR #18 review #5 — symlink resolution. Resolve every
            // link in the requested path to its real target, then
            // re-validate against baseDir. If the real target lies
            // outside the base subtree (the symlink-escape attack),
            // reject. ResolveLinkTarget returns null when the path is
            // not a link; falls through with the lexical path
            // accepted.
            var realPath = ResolveSymlinkChain(requestedPath);
            if (!realPath.StartsWith(baseDir, pathComparison))
            {
                reason = $"resolved path '{requestedPath}' resolves through a symlink to '{realPath}', which is not under base directory '{baseDir}'";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (System.Exception ex)
        {
            reason = $"path canonicalization failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Per PR #18 review #5 — resolve every symlink in
    /// <paramref name="path"/> to its real target. Returns the
    /// canonical path with all symlinks resolved.
    /// <see cref="System.IO.File.ResolveLinkTarget"/> with
    /// <c>returnFinalTarget: true</c> walks an entire link chain in
    /// one call; if the path doesn't exist OR isn't a link, returns
    /// the input unchanged.</summary>
    private static string ResolveSymlinkChain(string path)
    {
        // Build piece-by-piece so a symlink in the middle of the path
        // (e.g., /docs/secrets/index.html where /docs/secrets is a
        // symlink) is resolved too. ResolveLinkTarget on the leaf
        // alone would miss intermediate symlinks.
        try
        {
            // FileSystemInfo.LinkTarget / ResolveLinkTarget(true)
            // walks the chain on .NET 6+. Build the path one segment
            // at a time so any intermediate symlinked directory is
            // also resolved.
            var sep = System.IO.Path.DirectorySeparatorChar;
            var altSep = System.IO.Path.AltDirectorySeparatorChar;
            var parts = path.Split(new[] { sep, altSep }, StringSplitOptions.RemoveEmptyEntries);
            // Preserve the leading slash on POSIX.
            var current = path.StartsWith(sep) || path.StartsWith(altSep)
                ? sep.ToString()
                : string.Empty;
            for (var i = 0; i < parts.Length; i++)
            {
                current = System.IO.Path.Combine(current, parts[i]);
                // Resolve the current prefix if it's a symlink.
                System.IO.FileSystemInfo? info = i == parts.Length - 1
                    ? new System.IO.FileInfo(current)
                    : new System.IO.DirectoryInfo(current);
                if (info.Exists)
                {
                    var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is not null)
                    {
                        // Replace current with the resolved real path.
                        // FullName gives the absolute resolved path.
                        current = resolved.FullName;
                    }
                }
            }
            return System.IO.Path.GetFullPath(current);
        }
        catch
        {
            // Any I/O failure during link resolution → fall back to
            // lexical path. The caller's prefix check still applied;
            // we just couldn't deepen the validation.
            return path;
        }
    }

    /// <summary>Per PR #18 review #7 — sanitize exception messages
    /// before they reach the diagnostic. Loader exceptions can carry
    /// attacker-supplied URL fragments / response bodies in their
    /// messages; without a strip step those land in
    /// <see cref="ResourceFailure.Reason"/> + then in caller logs +
    /// observability tools (the same ANSI-injection / log-poisoning
    /// surface Phase A A-6 closed for CSS diagnostics). Strip C0
    /// (except TAB / LF / CR) + DEL + C1 + cap at 200 chars.</summary>
    private static string SanitizeExceptionMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "(no message)";
        const int maxLen = 200;
        var sb = new System.Text.StringBuilder(Math.Min(message.Length, maxLen + 1));
        for (var i = 0; i < message.Length && sb.Length < maxLen; i++)
        {
            var c = message[i];
            if ((c < 0x20 && c != '\t' && c != '\n' && c != '\r') // C0 except TAB/LF/CR
                || c == 0x7F                                       // DEL
                || (c >= 0x80 && c <= 0x9F))                       // C1
            {
                sb.Append('?');
            }
            else
            {
                sb.Append(c);
            }
        }
        if (message.Length > maxLen) sb.Append("...");
        return sb.ToString();
    }
}

/// <summary>Per post-Task-7 review (recommendation P1 #1) — bundle
/// returned by <see cref="SafeResourceLoader.CreateWithSafeHttp"/>.
/// Holds the constructed wrapper + the underlying HTTP loader so the
/// caller can dispose the loader at shutdown without losing the
/// wrapper's reference.</summary>
public sealed class SafeResourceLoaderWithHttp : IDisposable
{
    /// <summary>The wrapper. Pass this to <c>HtmlPdfOptions.ResourceLoader</c>
    /// + every pipeline-internal fetch routes through here.</summary>
    public SafeResourceLoader Wrapper { get; }

    /// <summary>The underlying HTTP loader. Exposed so the caller
    /// can <see cref="IDisposable.Dispose"/> it at shutdown — the
    /// wrapper does NOT take ownership of the loader's lifecycle.</summary>
    public SafeHttpResourceLoader UnderlyingHttpLoader { get; }

    internal SafeResourceLoaderWithHttp(SafeResourceLoader wrapper, SafeHttpResourceLoader http)
    {
        Wrapper = wrapper;
        UnderlyingHttpLoader = http;
    }

    /// <summary>Disposes the underlying HTTP loader. The wrapper has
    /// no native resources of its own — only the loader's HttpClient.</summary>
    public void Dispose() => UnderlyingHttpLoader.Dispose();
}

/// <summary>Result of a <see cref="SafeResourceLoader.FetchAsync"/>.
/// Either a successful <see cref="ResourceResponse"/> (loaded bytes +
/// MIME) or a typed failure with the reason for diagnostics.</summary>
public readonly record struct SafeResourceResult(
    bool Success, ResourceResponse Response, ResourceFailure? Failure)
{
    public static SafeResourceResult Loaded(ResourceResponse response) =>
        new(true, response, null);

    public static SafeResourceResult Failed(Uri uri, ResourceKind kind, string reason) =>
        new(false, default, new ResourceFailure
        {
            Uri = uri,
            Kind = kind,
            Reason = reason,
        });
}
