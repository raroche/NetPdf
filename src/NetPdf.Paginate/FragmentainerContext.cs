// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan — per-page mutable state. One instance lives per
/// page (fragmentainer in CSS Fragmentation L3 terms); the layouter
/// reads + updates <see cref="UsedBlockSize"/> as it places content,
/// consults <see cref="RemainingBlockSize"/> to decide whether the next
/// box fits, and increments <see cref="PageIndex"/> when emitting a
/// page break.
///
/// <para><b>Block-axis vs height.</b> Per Phase 3 review fix #2 +
/// CSS Logical Properties L1 + CSS Writing Modes L3 §3, fragmentation
/// always happens along the BLOCK axis. In <c>horizontal-tb</c> the
/// block axis IS height; in <c>vertical-rl</c> / <c>vertical-lr</c>
/// the block axis is along the X axis (width). Naming the fields
/// <c>UsedBlockSize</c> / <c>BlockSize</c> instead of
/// <c>UsedHeight</c> / <c>ContentAreaHeight</c> keeps the API
/// writing-mode-agnostic + spec-aligned.
/// <see cref="ContentInlineSize"/> tracks the orthogonal
/// (cross-fragmentation) dimension.</para>
///
/// <para>Per CSS Page L3 §3, the fragmentainer is the page's
/// content area: page box minus the four page margins (which host
/// the 16 page-margin boxes — those have their OWN
/// <see cref="FragmentainerContext"/>).</para>
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

    /// <summary>Page content-area extent in the INLINE axis (CSS px).
    /// In <c>horizontal-tb</c> this is the page width; in
    /// <c>vertical-rl/lr</c> it's the page height. The orthogonal
    /// dimension to <see cref="BlockSize"/>.</summary>
    public double ContentInlineSize { get; init; }

    /// <summary>Page content-area extent in the BLOCK axis (CSS px).
    /// In <c>horizontal-tb</c> this is the page height; in
    /// <c>vertical-rl/lr</c> it's the page width. Fragmentation
    /// happens along this axis.</summary>
    public double BlockSize { get; init; }

    /// <summary>Cumulative size (CSS px) used along the block axis by
    /// emitted content on this page. Updated by the layouter as it
    /// emits each block / line / row.</summary>
    public double UsedBlockSize { get; set; }

    /// <summary>Convenience: how much block-axis space remains on this
    /// page. Compared against the next chunk's block size to decide
    /// whether to break.</summary>
    public double RemainingBlockSize => BlockSize - UsedBlockSize;

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
    /// <remarks>Per Phase 3 review fix #8 — both extents must be
    /// finite + positive. NaN / non-positive dimensions would silently
    /// corrupt every downstream cost calculation.</remarks>
    public FragmentainerContext(double contentInlineSize, double blockSize)
    {
        if (!double.IsFinite(contentInlineSize) || contentInlineSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentInlineSize),
                $"contentInlineSize must be finite + positive; got {contentInlineSize}");
        if (!double.IsFinite(blockSize) || blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"blockSize must be finite + positive; got {blockSize}");
        ContentInlineSize = contentInlineSize;
        BlockSize = blockSize;
    }

    /// <summary>Allocate a fresh context for the next page in the
    /// document. Page index advances by one;
    /// <see cref="UsedBlockSize"/> resets to zero; the
    /// <see cref="NamedStrings"/> table + <see cref="TotalPages"/> +
    /// content-area dimensions carry forward (page sizing per
    /// @page :first / :left / :right is the layouter's job — this
    /// helper assumes the same content-area as the current page).
    ///
    /// <para>Per PR #19 Copilot review #5 — note that author counters
    /// (<c>counter(page)</c>, <c>counter(section)</c>, etc.) live in
    /// <see cref="LayoutContext"/>, NOT in this class, so they are
    /// NOT propagated by Clone. The pre-fix docstring incorrectly
    /// claimed counter state carried; that's the layouter's
    /// responsibility (counters are document-scoped, not page-scoped).</para></summary>
    public FragmentainerContext Clone()
    {
        var next = new FragmentainerContext(ContentInlineSize, BlockSize)
        {
            PageIndex = PageIndex + 1,
            TotalPages = TotalPages,
            UsedBlockSize = 0,
        };
        foreach (var kvp in NamedStrings) next.NamedStrings[kvp.Key] = kvp.Value;
        return next;
    }
}
