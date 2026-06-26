// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>Phase 4 links (PR 4) — turn <c>&lt;a href&gt;</c> elements into PDF <c>/Link</c> annotations
/// (CSS has no link concept; this is an HTML-semantics pass). For each page, every fragment whose box (or
/// an ancestor) is an <c>&lt;a&gt;</c> with a non-empty <c>href</c> contributes its border-box rect; the
/// union per anchor becomes ONE link annotation over that page (a single-line link is exact; a multi-line
/// link uses its bounding box — a documented first-cut approximation, vs one annotation per line). Only
/// HTTP(S)/mailto/absolute-style hrefs are emitted as URI actions; in-document <c>#fragment</c> links are a
/// follow-up (they need a name→destination map).</summary>
internal static class LinkAnnotationCollector
{
    public static void AddLinks(
        PdfPage page, IReadOnlyList<BoxFragment> fragments,
        double pageHeightPt, double contentOriginLeftPx, double contentOriginTopPx)
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
            FragmentPainter.ToPdfRect(
                link.L, link.T, link.R - link.L, link.B - link.T, pageHeightPt,
                out var x, out var y, out var w, out var h);
            page.AddUriLinkAnnotation(x, y, w, h, link.Href);
        }
    }

    /// <summary>Walk the box's ancestor chain (including itself) for the nearest <c>&lt;a&gt;</c> with a
    /// usable URI href. Returns the anchor element + href, or <see langword="null"/>.</summary>
    private static (IElement El, string Href)? FindAnchor(Box? box)
    {
        for (var b = box; b is not null; b = b.Parent)
        {
            if (b.SourceElement is { } el
                && string.Equals(el.LocalName, "a", StringComparison.OrdinalIgnoreCase))
            {
                var href = el.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href) && !href!.StartsWith('#'))
                    return (el, href.Trim());
            }
        }
        return null;
    }
}
