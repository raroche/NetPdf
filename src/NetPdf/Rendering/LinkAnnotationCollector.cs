// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>Phase 4 links (PR 4) — turn <c>&lt;a href&gt;</c> elements into PDF <c>/Link</c> annotations
/// (an HTML-semantics pass). For each page, every fragment whose box (or an ancestor) is an
/// <c>&lt;a&gt;</c> with a usable href contributes its border-box rect; the union per anchor becomes ONE
/// annotation. The href is gated by <see cref="LinkUriPolicy"/> — only <c>http</c>/<c>https</c>/<c>mailto</c>
/// (and relatives that resolve against a safe http(s) base) emit a URI action; everything else
/// (<c>file:</c>, <c>data:</c>, <c>javascript:</c>, custom, unresolved relatives) is dropped + diagnosed
/// (PR-229 review [P1]).</summary>
internal static class LinkAnnotationCollector
{
    public static void AddLinks(
        PdfPage page, IReadOnlyList<BoxFragment> fragments,
        double pageHeightPt, double contentOriginLeftPx, double contentOriginTopPx,
        Uri? baseUri, IDiagnosticsSink? diagnostics, ref bool schemeReported)
    {
        Dictionary<IElement, (string Href, double L, double T, double R, double B)>? links = null;
        foreach (var frag in fragments)
        {
            if (frag.InlineSize <= 0 || frag.BlockSize <= 0) continue;
            if (FindAnchor(frag.Box) is not { } anchor) continue;
            var l = contentOriginLeftPx + frag.InlineOffset;
            var t = contentOriginTopPx + frag.BlockOffset;
            var r = l + frag.InlineSize;
            var b = t + frag.BlockSize;
            links ??= new Dictionary<IElement, (string, double, double, double, double)>();
            if (links.TryGetValue(anchor.El, out var cur))
                links[anchor.El] = (cur.Href, Math.Min(cur.L, l), Math.Min(cur.T, t), Math.Max(cur.R, r), Math.Max(cur.B, b));
            else
                links[anchor.El] = (anchor.Href, l, t, r, b);
        }
        if (links is null) return;
        foreach (var (_, link) in links)
        {
            if (!LinkUriPolicy.TryResolve(link.Href, baseUri, out var uri))
            {
                if (!schemeReported)
                {
                    diagnostics?.Emit(new Diagnostic(DiagnosticCodes.LinkUriUnsupported001,
                        "A hyperlink href was not emitted as a clickable annotation because its URI scheme is "
                        + "not on the safe allowlist (http / https / mailto); file: / data: / javascript: / "
                        + "custom schemes + unresolved relatives are blocked. The link text still renders.",
                        DiagnosticSeverity.Warning));
                    schemeReported = true;
                }
                continue;
            }
            FragmentPainter.ToPdfRect(
                link.L, link.T, link.R - link.L, link.B - link.T, pageHeightPt,
                out var x, out var y, out var w, out var h);
            page.AddUriLinkAnnotation(x, y, w, h, uri);
        }
    }

    /// <summary>Walk the box's ancestor chain (including itself) for the nearest <c>&lt;a&gt;</c> with a
    /// non-empty, non-<c>#fragment</c> href. Returns the anchor element + href, or <see langword="null"/>.</summary>
    private static (IElement El, string Href)? FindAnchor(Box? box)
    {
        for (var b = box; b is not null; b = b.Parent)
        {
            if (b.SourceElement is { } el
                && string.Equals(el.LocalName, "a", StringComparison.OrdinalIgnoreCase))
            {
                var href = el.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href) && !href!.TrimStart().StartsWith('#'))
                    return (el, href.Trim());
            }
        }
        return null;
    }
}

/// <summary>Phase 4 links (PR 4 review [P1]) — the hyperlink URI safety policy: a central allowlist that
/// decides whether an <c>&lt;a href&gt;</c> may become a PDF <c>/URI</c> action. Only
/// <c>http</c> / <c>https</c> / <c>mailto</c> absolute URIs (and relatives that resolve against a safe
/// http(s) base) are allowed; <c>file:</c> / <c>data:</c> / <c>javascript:</c> / <c>ftp:</c> / custom
/// schemes and base-less relatives are rejected.</summary>
internal static class LinkUriPolicy
{
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>Resolve + validate <paramref name="href"/>. On success <paramref name="uri"/> is the
    /// absolute URI to emit. Returns <see langword="false"/> (rejected) for a blocked scheme or an
    /// unresolved relative.</summary>
    public static bool TryResolve(string href, Uri? baseUri, out string uri)
    {
        uri = string.Empty;
        var h = href.Trim();
        if (h.Length == 0) return false;

        var scheme = SchemeOf(h);
        if (scheme is not null)
        {
            if (!Allowed.Contains(scheme)) return false; // explicit but blocked scheme
            uri = h;
            return true;
        }

        // Relative (no scheme): resolve ONLY against a safe http(s) base.
        if (baseUri is not null
            && (baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(baseUri, h, out var abs)
            && Allowed.Contains(abs.Scheme))
        {
            uri = abs.ToString();
            return true;
        }
        return false;
    }

    /// <summary>The URI scheme of <paramref name="s"/> (lowercased) if it begins with a valid
    /// <c>scheme:</c> per RFC 3986 (<c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</c>), else null —
    /// a string whose first <c>:</c> follows a <c>/</c>, <c>?</c>, or <c>#</c> is a relative reference,
    /// not a scheme.</summary>
    private static string? SchemeOf(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ':')
                return i == 0 ? null : s.Substring(0, i).ToLowerInvariant();
            if (c is '/' or '?' or '#') return null; // a path/query/fragment before any ':' → relative
            var ok = i == 0
                ? char.IsAsciiLetter(c)
                : (char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
            if (!ok) return null;
        }
        return null;
    }
}
