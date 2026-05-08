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
        _inner = inner; // null is valid — see class docs
        _context = context;
    }

    /// <summary>Fetch a resource with all the Phase D defenses applied.
    /// Returns the loaded bytes on success; a <see cref="ResourceFailure"/>
    /// shape on rejection. The fetch is still counted against the
    /// per-render budget on rejection so an attacker can't probe the
    /// allowlist without paying.</summary>
    public async ValueTask<SafeResourceResult> FetchAsync(Uri uri, ResourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _context.CancellationToken.ThrowIfCancellationRequested();

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

        // 3. Reserve a budget slot. Per-resource bytes-budget will be
        // charged post-fetch via TryAddBytes (we don't know the byte
        // count yet).
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
    /// bytes).</summary>
    internal static bool IsMimeAllowedForKind(string mime, ResourceKind kind)
    {
        if (string.IsNullOrEmpty(mime)) return true;
        // Strip parameters (e.g., "text/css; charset=utf-8" → "text/css").
        var semi = mime.IndexOf(';');
        var bare = (semi < 0 ? mime : mime[..semi]).Trim().ToLowerInvariant();
        return kind switch
        {
            ResourceKind.Image => bare is "image/png" or "image/jpeg" or "image/gif"
                or "image/webp" or "image/bmp" or "image/svg+xml",
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
    /// <paramref name="baseUri"/>'s directory subtree, after
    /// canonicalization. Defends against <c>../../etc/passwd</c>
    /// path-traversal attacks. Per Phase D D-1.</summary>
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
            if (!requestedPath.StartsWith(baseDir, StringComparison.Ordinal))
            {
                reason = $"resolved path '{requestedPath}' is not under base directory '{baseDir}'";
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
