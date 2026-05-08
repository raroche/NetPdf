// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan — per-page mutable state. One instance lives per
/// page (fragmentainer in CSS Fragmentation L3 terms); the layouter
/// reads + updates <see cref="UsedHeight"/> as it places content,
/// consults <see cref="RemainingHeight"/> to decide whether the next
/// box fits, and increments <see cref="PageIndex"/> when emitting a
/// page break.
///
/// <para>Per CSS Page L3 §3, the fragmentainer is the page's
/// content area: page box minus the four page margins (which host
/// the 16 page-margin boxes — those have their OWN
/// <see cref="FragmentainerContext"/>). The
/// <see cref="ContentAreaWidth"/> / <see cref="ContentAreaHeight"/>
/// fields capture this rect; the layouter never reads the outer
/// page box from here.</para>
///
/// <para>State that's per-DOCUMENT (not per-page) lives in
/// <see cref="LayoutContext"/> instead — counters, named-strings
/// table, running-element registry. Per-page state stays in this
/// class so a page break is a clean swap: dispose this instance,
/// allocate the next.</para>
/// </summary>
internal sealed class FragmentainerContext
{
    /// <summary>0-based index of the current page. Drives
    /// <c>counter(page)</c> + the @page :first / :left / :right /
    /// :blank pseudo selection.</summary>
    public int PageIndex { get; set; }

    /// <summary>Total expected page count, 0 when not yet known.
    /// Drives <c>counter(pages)</c>. Set after the optimizer commits
    /// the final page sequence — early-page layout can't know this
    /// in advance.</summary>
    public int TotalPages { get; set; }

    /// <summary>Page content-area width (CSS px). The fragmentainer's
    /// horizontal extent — page width minus left + right margins.</summary>
    public double ContentAreaWidth { get; init; }

    /// <summary>Page content-area height (CSS px). The fragmentainer's
    /// vertical extent — page height minus top + bottom margins.</summary>
    public double ContentAreaHeight { get; init; }

    /// <summary>Cumulative height (CSS px) used by emitted content on
    /// this page. Updated by the layouter as it emits each block /
    /// line / row. Always &lt;= <see cref="ContentAreaHeight"/> +
    /// per-element overflow tolerance.</summary>
    public double UsedHeight { get; set; }

    /// <summary>Convenience: how much vertical space remains on this
    /// page. Compared against the next chunk's height to decide
    /// whether to break.</summary>
    public double RemainingHeight => ContentAreaHeight - UsedHeight;

    /// <summary>Per Phase 3 plan §"Page-margin boxes &amp; running
    /// elements" — named strings set by <c>string-set: name content</c>
    /// during element layout; pulled by <c>content: string(name)</c>
    /// in page-margin boxes. Per CSS GCPM L3 §6 the value is
    /// element-scoped at set time + persists across pages until
    /// re-set, so this map carries forward when a new
    /// <see cref="FragmentainerContext"/> is allocated for the next
    /// page (the new instance receives a copy of the prior page's
    /// final state).</summary>
    public Dictionary<string, string> NamedStrings { get; } = new();

    /// <summary>Float-manager state placeholder. The float manager
    /// (Phase 3 Task 8) tracks left + right floats per BFC; this
    /// field reserves the slot so the layouter signature stays
    /// stable when float support lands.</summary>
    public object? FloatManagerState { get; set; }

    /// <summary>Construct a new context for a fresh page. Use
    /// <see cref="Clone"/> when transitioning to the next page in a
    /// document so name-string + counter state carries forward.</summary>
    public FragmentainerContext(double contentAreaWidth, double contentAreaHeight)
    {
        ContentAreaWidth = contentAreaWidth;
        ContentAreaHeight = contentAreaHeight;
    }

    /// <summary>Allocate a fresh context for the next page in the
    /// document. Page index advances by one;
    /// <see cref="UsedHeight"/> resets to zero; named-string state +
    /// page-content-area dimensions carry forward (page sizing per
    /// @page :first / :left / :right is the layouter's job — this
    /// helper assumes the same content-area as the current page).</summary>
    public FragmentainerContext Clone()
    {
        var next = new FragmentainerContext(ContentAreaWidth, ContentAreaHeight)
        {
            PageIndex = PageIndex + 1,
            TotalPages = TotalPages,
            UsedHeight = 0,
        };
        foreach (var kvp in NamedStrings) next.NamedStrings[kvp.Key] = kvp.Value;
        return next;
    }
}
