// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Layout.Layouters;
using NetPdf.Pdf;

namespace NetPdf.Rendering;

/// <summary>Phase 4 outlines (PR 4) — turn <c>&lt;h1&gt;</c>–<c>&lt;h6&gt;</c> headings into the PDF document
/// outline (the reader's bookmarks panel). Headings are block-level, so each surfaces as a box fragment
/// whose <see cref="NetPdf.Layout.Boxes.Box.SourceElement"/> is the heading element; its title is the
/// heading's text content and its destination is the heading's top on its page. <see cref="PdfDocument"/>
/// nests them by level into the <c>/Outlines</c> tree at save time.</summary>
internal static class OutlineCollector
{
    public static void Collect(
        PdfDocument document, PdfPage page, IReadOnlyList<BoxFragment> fragments,
        double pageHeightPt, double contentOriginTopPx, HashSet<IElement> seen)
    {
        foreach (var frag in fragments)
        {
            if (frag.Box.SourceElement is not { } el) continue;
            var level = HeadingLevel(el.LocalName);
            if (level == 0) continue;
            if (!seen.Add(el)) continue; // the heading's FIRST fragment wins (a split heading bookmarks once)
            var title = el.TextContent?.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            var topPt = pageHeightPt - PdfUnits.PxToPt(contentOriginTopPx + frag.BlockOffset);
            document.AddOutlineHeading(level, title, page, topPt);
        }
    }

    private static int HeadingLevel(string? localName) => localName?.ToLowerInvariant() switch
    {
        "h1" => 1, "h2" => 2, "h3" => 3, "h4" => 4, "h5" => 5, "h6" => 6, _ => 0,
    };
}
