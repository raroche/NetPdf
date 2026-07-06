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

/// <summary>
/// Same-document navigation — turns <c>&lt;a href="#id"&gt;</c> anchors into internal PDF <c>/GoTo</c>
/// link annotations (the reader jumps to the target element's position). Because a link's target can
/// live on a LATER page than the link itself, this is a two-phase pass:
/// <list type="number">
///   <item><see cref="CollectDestinations"/> records every element <c>id</c> → (page, top) as the paint
///   loop visits each page (first occurrence of an id wins).</item>
///   <item><see cref="CollectInternalLinks"/> accumulates each <c>#fragment</c> anchor's page rectangle
///   + target id (deferred — the destination may not be painted yet).</item>
///   <item><see cref="ResolveInternalLinks"/> runs after ALL pages are painted: each pending link is
///   matched to a destination and emitted, or diagnosed as unresolved (never silently dropped).</item>
/// </list>
/// External (<c>http</c>/<c>https</c>/<c>mailto</c>) links stay with <see cref="LinkAnnotationCollector"/>.
/// </summary>
internal static class NavigationCollector
{
    /// <summary>A same-document link target: the page it lands on and its top in PDF points.</summary>
    internal readonly record struct Destination(PdfPage Page, double TopPt);

    /// <summary>A <c>#fragment</c> link awaiting resolution: its page + rect (PDF points) and target id.</summary>
    internal readonly record struct PendingLink(PdfPage Page, double X, double Y, double W, double H, string TargetId);

    /// <summary>Record id → destination for every fragment on this page whose source element carries a
    /// non-empty <c>id</c>. The element's FIRST-seen fragment wins (a split element anchors once), and an
    /// id already recorded on an earlier page is not overwritten (document order wins).</summary>
    public static void CollectDestinations(
        PdfPage page, IReadOnlyList<BoxFragment> fragments,
        double pageHeightPt, double contentOriginTopPx, Dictionary<string, Destination> destinations)
    {
        foreach (var frag in fragments)
        {
            var topPt = pageHeightPt - PdfUnits.PxToPt(contentOriginTopPx + frag.BlockOffset);
            RegisterFragmentTargetIds(frag.Box, page, topPt, destinations);
        }
    }

    /// <summary>Register the <c>id</c> of <paramref name="box"/> and of its NON-ATOMIC inline descendants
    /// (which do not emit their own fragment — e.g. <c>&lt;span id="summary"&gt;</c> inside a paragraph)
    /// at this fragment's top. Block-level and atomic-inline descendants get their OWN fragment (a more
    /// precise position), so the walk does not descend into them. First occurrence of an id wins.
    /// <para>Approximation: an inline-flow target resolves to its containing block/line-bearing box's
    /// top, not the exact inline glyph position.</para></summary>
    private static void RegisterFragmentTargetIds(
        Box box, PdfPage page, double topPt, Dictionary<string, Destination> destinations)
    {
        if (box.SourceElement?.GetAttribute("id") is { Length: > 0 } id && !destinations.ContainsKey(id))
            destinations[id] = new Destination(page, topPt);

        foreach (var child in box.Children)
        {
            if (child.IsInlineLevel && !child.IsAtomicInline)
                RegisterFragmentTargetIds(child, page, topPt, destinations);
        }
    }

    /// <summary>Accumulate the page rectangle of every <c>&lt;a href="#id"&gt;</c> anchor on this page into
    /// <paramref name="pending"/> (one union rect per anchor element), to be resolved after all pages are
    /// painted. Empty / non-<c>#</c> hrefs are ignored (external links are handled elsewhere).</summary>
    public static void CollectInternalLinks(
        PdfPage page, IReadOnlyList<BoxFragment> fragments,
        double pageHeightPt, double contentOriginLeftPx, double contentOriginTopPx,
        List<PendingLink> pending)
    {
        Dictionary<IElement, (string Target, double L, double T, double R, double B)>? links = null;
        foreach (var frag in fragments)
        {
            if (frag.InlineSize <= 0 || frag.BlockSize <= 0) continue;
            if (FindFragmentAnchor(frag.Box) is not { } anchor) continue;
            var l = contentOriginLeftPx + frag.InlineOffset;
            var t = contentOriginTopPx + frag.BlockOffset;
            var r = l + frag.InlineSize;
            var b = t + frag.BlockSize;
            links ??= new Dictionary<IElement, (string, double, double, double, double)>();
            if (links.TryGetValue(anchor.El, out var cur))
                links[anchor.El] = (cur.Target, Math.Min(cur.L, l), Math.Min(cur.T, t), Math.Max(cur.R, r), Math.Max(cur.B, b));
            else
                links[anchor.El] = (anchor.Target, l, t, r, b);
        }
        if (links is null) return;
        foreach (var (_, link) in links)
        {
            FragmentPainter.ToPdfRect(
                link.L, link.T, link.R - link.L, link.B - link.T, pageHeightPt,
                out var x, out var y, out var w, out var h);
            pending.Add(new PendingLink(page, x, y, w, h, link.Target));
        }
    }

    /// <summary>Resolve every accumulated <c>#fragment</c> link against the collected destinations. A
    /// matched link becomes a <c>/GoTo</c> annotation; an unmatched one is diagnosed once
    /// (<see cref="DiagnosticCodes.LinkFragmentUnresolved001"/>) — the link text still renders.</summary>
    public static void ResolveInternalLinks(
        IReadOnlyList<PendingLink> pending, IReadOnlyDictionary<string, Destination> destinations,
        IDiagnosticsSink? diagnostics, ref bool unresolvedReported)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            var link = pending[i];
            if (destinations.TryGetValue(link.TargetId, out var dest))
            {
                link.Page.AddInternalLinkAnnotation(link.X, link.Y, link.W, link.H, dest.Page.PageRef, dest.TopPt);
            }
            else if (!unresolvedReported)
            {
                diagnostics?.Emit(new Diagnostic(DiagnosticCodes.LinkFragmentUnresolved001,
                    "A same-document link (href=\"#…\") pointed at an id that no element in the document "
                    + "declares, so no clickable jump was emitted. The link text still renders.",
                    DiagnosticSeverity.Warning));
                unresolvedReported = true;
            }
        }
    }

    /// <summary>Walk the box's ancestor chain (including itself) for the nearest <c>&lt;a&gt;</c> whose
    /// href is a same-document <c>#fragment</c>; returns the anchor element + the PERCENT-DECODED target id
    /// (without the leading <c>#</c>), or <see langword="null"/>. An empty fragment (bare <c>#</c>) is
    /// ignored. The fragment is UTF-8 percent-decoded (browser fragment-navigation semantics) so
    /// <c>href="#r%C3%A9sum%C3%A9"</c> matches <c>id="résumé"</c> and <c>href="#sec%2F2"</c> matches
    /// <c>id="sec/2"</c>.</summary>
    private static (IElement El, string Target)? FindFragmentAnchor(Box? box)
    {
        for (var b = box; b is not null; b = b.Parent)
        {
            if (b.SourceElement is { } el
                && string.Equals(el.LocalName, "a", StringComparison.OrdinalIgnoreCase))
            {
                var href = el.GetAttribute("href")?.Trim();
                if (!string.IsNullOrEmpty(href) && href.StartsWith('#') && href.Length > 1)
                    return (el, DecodeFragment(href.Substring(1)));
            }
        }
        return null;
    }

    /// <summary>UTF-8 percent-decode a URL fragment (browser fragment-navigation semantics). Malformed
    /// escapes are left as-is (<c>Uri.UnescapeDataString</c> does not throw).</summary>
    private static string DecodeFragment(string raw) =>
        raw.Contains('%') ? Uri.UnescapeDataString(raw) : raw;
}
