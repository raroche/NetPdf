// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// THE single page-progression model (CSS Page 3 §3.1 + CSS Fragmentation L3 §3.1) — the one place
/// that classifies a 1-based page number into recto/verso (page-NUMBER parity) and physical
/// left/right. Both <see cref="AtPageRules.PageSelectorContext.IsRightPage"/> (the
/// <c>@page :left</c>/<c>:right</c> selectors) and <c>PdfRenderPipeline.PageNumberHasParity</c> (the
/// forced-break blank-page insertion) delegate here, so the two can't drift (PR #219 review [P2 #5]).
/// </summary>
/// <remarks>
/// Page 1 is a RECTO — the side the page progression starts (CSS Page 3 §3.1: "in documents with a
/// left-to-right page progression the first page of the document is a right page, and vice versa").
/// So <c>recto</c> = an ODD page number and <c>verso</c> = an EVEN one: a page-NUMBER parity that does
/// NOT depend on the page direction. The PHYSICAL side a recto denotes DOES flip with direction — the
/// recto is the physical RIGHT page in LTR and the physical LEFT page in RTL — so <c>:left</c> /
/// <c>:right</c> swap with <see cref="IsRtl"/> while recto / verso never do. A forced
/// <c>break-before: &lt;verso side&gt;</c> on the first content makes page 1 a verso
/// (<see cref="StartsOnVerso"/>), shifting every page's parity by one (CSS Page 3 §3.6, the leading
/// empty page is suppressed).
/// </remarks>
/// <param name="StartsOnVerso">The first printed page is a verso (even progression position) rather
/// than the recto-first default — a forced verso starting side. Shifts every page's parity by one.</param>
/// <param name="IsRtl">The page progression is right-to-left, so the physical left / right sides swap
/// (the recto is the physical LEFT page). Recto / verso parity is unaffected.</param>
public readonly record struct PageProgression(bool StartsOnVerso = false, bool IsRtl = false)
{
    /// <summary>Is the 1-based <paramref name="pageNumber"/> a RECTO (the side the progression
    /// starts)? recto = odd page number unless the document starts on a verso (then it flips). A
    /// page-NUMBER parity — direction-INDEPENDENT (recto / verso never swap with <see cref="IsRtl"/>).</summary>
    public bool IsRecto(int pageNumber) => ((pageNumber + (StartsOnVerso ? 1 : 0)) & 1) == 1;

    /// <summary>Is the 1-based <paramref name="pageNumber"/> a physical RIGHT page (matches
    /// <c>@page :right</c>)? The recto is the physical right page in LTR and the physical left page in
    /// RTL, so right = recto XOR <see cref="IsRtl"/>.</summary>
    public bool IsRightPage(int pageNumber) => IsRecto(pageNumber) != IsRtl;
}
