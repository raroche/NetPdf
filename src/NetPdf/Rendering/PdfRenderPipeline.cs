// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using NetPdf.Css.PagedMedia;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Pdf;
using NetPdf.Phase2;
using NetPdf.Shaping;

namespace NetPdf.Rendering;

/// <summary>
/// The internal HTML → PDF rendering engine the public <see cref="HtmlPdf"/>
/// facade drives. Composes the existing <see cref="Phase2Pipeline"/> (parse →
/// cascade → box tree) with fragmentainer-aware layout
/// (<see cref="BlockLayouter"/>), the <see cref="FragmentPainter"/> paint bridge,
/// and the <see cref="PdfDocument"/> byte writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Single-page output painting <c>background-color</c> fills +
/// <c>border-*</c> edges (<see cref="FragmentPainter"/>) and shaped text glyphs
/// (<see cref="TextPainter"/>, cycle 5a-2-ii). Text subsets + embeds the exact font
/// program layout shaped — the shaper is kept alive past paint so glyph ids match.
/// Text-free content stays byte-deterministic; the default <see cref="SystemFontResolver"/>
/// text path is not deterministic-for-text until a bundled fallback font ships (TODO in
/// <c>deferrals.md#layout-to-pdf-pipeline</c>), but a fixed <c>IFontResolver</c> is fully
/// reproducible.
/// </para>
/// <para>
/// <b>Single page.</b> The multi-page driver (looping <see cref="BlockLayouter.AttemptLayout"/>
/// over continuations, a page per fragmentainer) is TODO 3. Until then, content
/// that overflows the first page is reported via
/// <c>PDF-CONTENT-OVERFLOW-TRUNCATED-001</c> rather than dropped silently.
/// </para>
/// </remarks>
internal static class PdfRenderPipeline
{
    /// <summary>The product of one conversion: the PDF bytes, the page count,
    /// and every diagnostic emitted across all stages.</summary>
    internal readonly record struct RenderOutcome(
        byte[] Pdf,
        int PageCount,
        IReadOnlyList<Diagnostic> Diagnostics);

    public static async Task<RenderOutcome> RenderAsync(
        string html,
        HtmlPdfOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(options);

        // One hub collects every stage's diagnostics for ConvertDetailed while
        // forwarding each live to the caller's sink (if any).
        var diagnostics = new CollectingDiagnosticsSink(options.Diagnostics);

        using var phase2 = await Phase2Pipeline.RunFromHtmlAsync(
            html, options, new PublicDiagnosticsSinkAdapter(diagnostics), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Page geometry: layout works in CSS px on the content area (page minus
        // margins); the px → pt scale + y-flip happen in the painter / emit step.
        var pageSize = options.PageSize;
        var margins = options.Margins;

        // CSS Paged Media (Phase 3 Task 21). Applicability is filtered with the SAME media
        // context the cascade used (PDF output is print), so a `media="screen"` sheet or
        // `@media screen` block never affects the page. Selectors (`:first`/`:left`/`:right`)
        // + page-margin boxes are later cycles.
        var media = Phase2Pipeline.BuildMediaContext(options);

        // `@page { size }` (cycle 2) overrides the page size when PreferCssPageSize (default on). The
        // LAYOUT baseline uses the BARE-`@page`-only size (post-PR-#184 review F2) — NOT the context-free
        // bare + `:first` resolve — so a `@page :first { size }` doesn't drive EVERY page's fragmentation
        // (the per-page PAINT applies `:first` / `:left` / named size on its page). For a single-page
        // document the body is still painted at the per-page (`:first`) MediaBox.
        if (options.PreferCssPageSize
            && AtPageSizeResolver.ResolveBare(phase2.Sheets, media) is { } cssSize)
        {
            pageSize = new PageSize(cssSize.WidthPx, cssSize.HeightPx);
        }

        // `@page { margin… }` (cycle 1) overrides the option's page margins, per side — again the
        // BARE-only baseline (review F2), so the body fragments against the bare margins (the per-page
        // PAINT applies selector margins). Percentage margins resolve against the RESOLVED page box —
        // `pageSize` already reflects any bare `@page { size }` override above — so
        // `@page { size: A5; margin: 10% }` computes its percentages against A5. Media applicability
        // still uses the original context.
        var pageMargins = AtPageMarginResolver.ResolveBare(
            phase2.Sheets, media, pageSize.WidthPx, pageSize.HeightPx);
        if (pageMargins.HasAny)
        {
            margins = new PageMargins(
                TopPx: pageMargins.TopPx ?? margins.TopPx,
                RightPx: pageMargins.RightPx ?? margins.RightPx,
                BottomPx: pageMargins.BottomPx ?? margins.BottomPx,
                LeftPx: pageMargins.LeftPx ?? margins.LeftPx);
        }

        var contentInlinePx = Math.Max(0, pageSize.WidthPx - margins.LeftPx - margins.RightPx);
        var contentBlockPx = Math.Max(0, pageSize.HeightPx - margins.TopPx - margins.BottomPx);

        // Body context-dependent cycle — resolve the deferred font-/viewport-relative body lengths
        // (`2em` / `5vw` / `calc(2em + 10px)`) IN PLACE now that the page box is final: the cascade
        // deferred them (LengthResolver), BoxBuilder resolved each element's font-size, and the
        // layouters read LengthPx slots. Percentage terms stay deferred (containing-block base —
        // deferrals.md). The viewport bases are the PAGE box (incl. any `@page size` override),
        // matching the margin-box painter's convention.
        DeferredLengthResolver.ResolveTreeInPlace(
            phase2.BoxRoot, pageSize.WidthPx, pageSize.HeightPx);

        // Page margin boxes resolve EARLY (margin-box-bg-image cycle) — they're sheet-based
        // (no layout dependency), and the image prefetch below needs their background-image
        // urls. The UNION across every @page selector (cycle 6) — so a `@page :left { … background-image }`
        // box's image is prefetched too — feeds the prefetch + the "does the document have margin boxes
        // at all" check; the actual per-page boxes (honoring `:first`/`:left`/`:right`/`:blank`) are
        // re-resolved per page in the paint loop below. Layout/paint still happen at assembly time below.
        var marginBoxesUnion = AtPageMarginBoxResolver.ResolveAll(phase2.Sheets, media);
        // Margin-box background-image urls (PrintBackgrounds-gated like the body's — PR #166
        // review P1: no fetch for backgrounds that will not paint). The unsupported-form
        // diagnostic stays with the painter's Layout (the rect pass); silent null here.
        List<string>? marginBoxImageUrls = null;
        if (options.PrintBackgrounds && !marginBoxesUnion.IsDefaultOrEmpty)
        {
            var noopReported = true;   // suppress the duplicate here — Layout surfaces it.
            foreach (var mb in marginBoxesUnion)
            {
                if (PageMarginBoxPainter.TryReadBackgroundImageUrl(mb.Declarations, diagnostics, ref noopReported)
                    is { } mbUrl)
                {
                    (marginBoxImageUrls ??= new()).Add(mbUrl);
                }
            }
        }

        // Body image pipeline (img-pipeline + bg-image cycles): prefetch + decode every image
        // reference (an <img src>, a background-image url, a margin box's background-image)
        // BEFORE layout — `data:` URIs decode inline with no loader (the self-contained
        // default; other schemes go through HtmlPdfOptions.ResourceLoader under
        // SecurityPolicy). The sizing pre-pass then writes each replaced box's §10.3.2 used
        // width/height into its slots (CSS declared > HTML width/height attribute > intrinsic,
        // aspect-ratio completed) so layout sizes it like any explicit-size block. Failures
        // surface RES-LOAD-FAILED-001 / IMG-DECODE-FAILED-001 and the element lays out
        // unpainted.
        var imageCache = await ImageResourceCache.PrefetchAsync(
            phase2.BoxRoot, phase2.Cascade, options, diagnostics, cancellationToken,
            marginBoxImageUrls)
            .ConfigureAwait(false);
        ReplacedSizeResolver.ResolveTreeInPlace(phase2.BoxRoot, imageCache);

        // Keep the shaper alive PAST paint (method-scoped using): the TextPainter subsets the
        // SAME font program layout shaped, so the shaped glyph ids index the same font.
        using var shaper = new HarfBuzzShaperResolver(options.FontResolver ?? new SystemFontResolver());
        var fontResolutionFailed = false;
        var clippedOrTruncated = false;

        // PHASE A — the multi-page driver (Task 21 / layout→PDF cycle 3). Lay out one
        // fragmentainer per page, resuming via the continuation, accumulating each page's
        // fragments. LAYOUT-ALL-THEN-PAINT (docs/design/multi-page-driver.md §4.5) so
        // counter(pages) (the document total) is known before any page paints (cycle 4). The
        // LayoutRetryCoordinator drives each page Strict (clean breaks) → … → LastResort, so
        // block content overflowing the page now FLOWS onto the next page instead of
        // force-overflowing a single page.
        var pageFragments = new List<IReadOnlyList<BoxFragment>>();
        LayoutContinuation? continuation = null;
        // Cross-COMPONENT per-conversion grid measure cache (measurement-cache cycle) —
        // allocated ONCE here + wired onto every page's root layout context, so a grid's
        // PreMeasureGridRowExtent pre-grow + its GridLayouter emission Resolve (and
        // successive page dispatches) shape each content-determined cell ONCE rather than
        // re-shaping per site + per page. A measured CONTENT extent is deterministic for its
        // keyed inputs, so output is byte-identical.
        var gridMeasureCache = new GridMeasurementCache();
        // Per `multi-page-allocation-churn` — one cross-page table measurement cache, so a table
        // that fragments across pages is fully measured (column split + cell shaping) ONCE rather
        // than re-shaped per page by the subtree-extent pass (the O(n²) churn). Page-invariant +
        // deterministic, so output is byte-identical.
        var tableMeasureCache = new TableMeasurementCache();
        // Per `inline-only-block-line-splitting` (PR #220 review [P2]) — one cross-page cache so a
        // paragraph that splits across pages is shaped (text + inline-block atomic content) ONCE rather
        // than re-shaped on every resume page. Page-invariant + deterministic, so output is byte-identical.
        var inlineOnlyMeasureCache = new InlineOnlyMeasurementCache();
        // CSS Fragmentation L3 §4.2 — orphans/widows are inherited; resolved ONCE here as
        // the document-level defaults for the resolver (per-paragraph overrides await line
        // splitting — `inline-only-block-line-splitting`). BoxBuilder roots the tree at a
        // SYNTHETIC box carrying the initial default 2, so reading IT would always yield 2
        // (PR #207 review [P2]) — read the BODY box instead (it inherits html/:root + its own
        // value, the usual place print CSS sets orphans/widows), falling back to <html> then 2.
        var fragRoot = ResolveDocumentRootForInheritedProps(phase2.BoxRoot);
        var documentOrphans = fragRoot.Style.ReadOrphansOrDefault();
        var documentWidows = fragRoot.Style.ReadWidowsOrDefault();
        // The document's block-level direction — for RTL, the physical `left` / `right` page parities
        // swap (recto / verso don't); see PageNumberHasParity.
        var documentIsRtl = fragRoot.Style.IsRtl();
        // CSS Page L3 §3.6 + review [P1 #4] — the document's STARTING page side. A forced break-before
        // on the FIRST in-flow content (incl. <html> / <body>'s OWN break-before — PR #219 review [P1 #3])
        // that wants a VERSO (even) starting page selects it WITHOUT a leading blank (the forced break is
        // a no-op at the fragmentainer start per §3.1, so it's read from the box tree here). It shifts
        // every page's parity by one so page 1 is verso instead of the recto default. Direction-aware: a
        // `left` start is verso in LTR but recto in RTL. Walk from the SYNTHETIC root so <html> / <body>
        // contribute (their break-before propagates to the document start, §3.1.1).
        var firstStartParity = FirstContentForcedStartParity(phase2.BoxRoot);
        var firstPageParityOffset =
            firstStartParity != PageParity.Any && !PageNumberHasParity(1, firstStartParity, documentIsRtl)
                ? 1 : 0;
        try
        {
            for (var pageIndex = 0; ; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sink = new ListFragmentSink();
                var paginate = new PaginateToPublicDiagnosticsAdapter(diagnostics);
                LayoutAttemptResult result;
                // CSS Page L3 §3.4.1 — the page-parity (left/right/recto/verso) a forced break on
                // this page demands the resumed content land on; drives blank-page insertion below.
                var forcedParityThisPage = PageParity.Any;
                using (var layouter = new BlockLayouter(
                    rootBox: phase2.BoxRoot,
                    sink: sink,
                    incomingContinuation: continuation,
                    diagnostics: paginate,
                    shaperResolver: shaper))
                {
                    var fragmentainer = new FragmentainerContext(contentInlinePx, contentBlockPx)
                    {
                        PageIndex = pageIndex,
                    };
                    var layout = new LayoutContext(fragmentainer)
                    {
                        GridMeasureCache = gridMeasureCache,
                        TableMeasureCache = tableMeasureCache,
                        InlineOnlyMeasureCache = inlineOnlyMeasureCache,
                    };
                    // Document-level orphans/widows (resolved once above) drive the resolver
                    // instead of the hardcoded 2/2.
                    using var breaks = new BreakResolver(
                        orphansRequired: documentOrphans,
                        widowsRequired: documentWidows);
                    // Drive each page through the retry coordinator: Strict (clean breaks)
                    // first, only escalating (DropAvoidInside → LastResort) when a constraint
                    // genuinely can't be met. A single direct LastResort attempt would instead
                    // force-overflow the remainder onto one page rather than paginating.
                    var coordinator = new LayoutRetryCoordinator(diagnostics: paginate, fragmentSink: sink);
                    result = coordinator.Run(layouter, fragmentainer, ref layout, breaks, cancellationToken);
                    // Capture the forced-break parity from the committed attempt before the layouter
                    // is disposed (a forced break commits on the Strict attempt — no retry).
                    forcedParityThisPage = layouter.ForcedBreakParityForNextPage;
                }

                // Skip the trailing EMPTY resume page ONLY when it's TERMINAL — the forced-overflow
                // continuation protocol's sentinel: an empty page that ALSO reports it's done
                // (AllDone, or PageComplete with no further continuation; the resume-page subtree
                // measure isn't yet resume-aware — docs/design/multi-page-driver.md). An empty page
                // that STILL carries a real continuation is NOT this sentinel (it would be a future
                // `@page :blank` / forced-parity / margin-box-only page) — keep it: fall through to
                // add it + advance below, so the driver never silently drops a legitimate blank
                // page. The MaxPages cap guards forward progress against a non-advancing loop.
                // (An empty document still gets one page emitted below.)
                var isTerminalEmptyResume = sink.Fragments.Count == 0
                    && (result.Outcome != LayoutAttemptOutcome.PageComplete
                        || result.Continuation is null);
                if (pageIndex > 0 && isTerminalEmptyResume)
                {
                    break;
                }

                // A fragment outside the content box — wider than the content area (we paginate
                // the block axis, not the inline axis), or a negative / absolute offset — is
                // still clipped; surface it once. Block content that flows onto further pages
                // is NOT overflow now.
                if (FragmentsOverflowContentRect(sink.Fragments, contentInlinePx, contentBlockPx))
                {
                    clippedOrTruncated = true;
                }

                pageFragments.Add(sink.Fragments);

                if (result.Outcome != LayoutAttemptOutcome.PageComplete || result.Continuation is null)
                {
                    break;
                }
                continuation = result.Continuation;

                // CSS Page L3 §3.4.1 — forced-parity blank-page insertion. When this page ended on a
                // forced `break-before/after: left | right | recto | verso`, the resumed content must
                // land on a page of that parity. The content resumes on the next page (1-based number
                // = pageIndex + 2); if that page is the wrong parity, insert a blank `@page :blank`
                // (a 0-fragment page) first so the content lands on the correct side. Consecutive
                // pages alternate parity, so one blank always fixes it. Direction-aware (the physical
                // left/right swap in RTL — see PageNumberHasParity).
                if (forcedParityThisPage != PageParity.Any
                    && !PageNumberHasParity(
                        pageIndex + 2 + firstPageParityOffset, forcedParityThisPage, documentIsRtl))
                {
                    pageFragments.Add(System.Array.Empty<BoxFragment>());
                    pageIndex++;   // the blank consumes a page index; the content resumes after it.
                    if (pageIndex + 1 >= MaxPages)
                    {
                        clippedOrTruncated = true;
                        break;
                    }
                }

                // Forward progress is guaranteed by the forced-overflow penalty; this cap
                // backstops a layouter that fails to advance, so we never spin forever.
                if (pageIndex + 1 >= MaxPages)
                {
                    clippedOrTruncated = true;
                    break;
                }
            }
        }
        catch (FontResolutionException ex)
        {
            // Text shaping runs DURING layout, so a font that can't be resolved (no matching
            // face) or whose bytes are unsafe / WOFF-wrapped surfaces here — not at paint time
            // (the async-resolver case is a separate NotSupportedException already handled at
            // the inline-layout seam). Degrade to a valid PDF — keeping any pages laid out
            // before the failure — plus a diagnostic, rather than failing the whole conversion
            // (CLAUDE.md #7, post-PR-#127 review P1). A bundled fallback font (cycle 5b) makes
            // the default path never reach this.
            EmitTextFontUnresolved(diagnostics, ex.Message);
            fontResolutionFailed = true;
        }

        // Always emit at least one page (an empty document, or a font failure before page 1,
        // would otherwise produce a page-less — invalid — PDF).
        if (pageFragments.Count == 0)
        {
            pageFragments.Add(System.Array.Empty<BoxFragment>());
        }

        // The overflow/clip diagnostic is emitted AFTER the paint loop (post-PR-#184 review F3): the
        // per-page-geometry overflow check needs each page's resolved size/margins, which the paint loop
        // computes. `clippedOrTruncated` is already set here for inline-overflow / the page-cap; the paint
        // loop adds per-page-geometry overflow before the single emission.

        cancellationToken.ThrowIfCancellationRequested();

        // Emit: page size (CSS px) → /MediaBox (PDF pt). ONE PdfDocument + MediaBox for the
        // whole run; the document + shaper are shared across pages. (NOTE: text is currently
        // subset PER PAGE in the loop below, so a font shared across pages is NOT yet deduped —
        // a size/efficiency follow-up, deferrals.md#layout-to-pdf-pipeline.) The painters read
        // the box-tree styles, so they run before phase2 is disposed (method end).
        var document = new PdfDocument(PdfVersionString(options.EmittedPdfVersion));
        // Phase 4 links (PR 4) — the HTML→PDF path intentionally emits <a href> hyperlink annotations, so
        // opt into the active-content preflight's narrow /URI-action allowance (JS / Launch / embedded
        // files stay blocked).
        document.AllowUriLinkAnnotations = true;
        if (!string.IsNullOrEmpty(options.Title)) document.Title = options.Title;
        if (!string.IsNullOrEmpty(options.Author)) document.Author = options.Author;
        var mediaBox = new MediaBoxSize(
            PdfUnits.PxToPt(pageSize.WidthPx),
            PdfUnits.PxToPt(pageSize.HeightPx));

        // Page margin boxes (running headers/footers, Task 21): resolved from the @page rules +
        // the generated-content context (string()/element()). The boxes + page-context declarations
        // are re-resolved PER PAGE in the paint loop (cycle 6) so each page honors its
        // `:first`/`:left`/`:right`/`:blank` selectors; only the document-wide pieces (does the doc
        // have margin boxes at all, the inheritance-root element + its style) are computed here. The
        // root element box is the attr() host + the top of the inheritance chain (root → page context
        // → margin box); element-free documents skip margin boxes. `var` keeps the resolver/collector
        // return types from leaking into this method's locals.
        var marginRootElBox = marginBoxesUnion.IsDefaultOrEmpty ? null : FindRootElementBox(phase2.BoxRoot);
        var marginHost = marginRootElBox?.SourceElement;
        var hasMarginBoxes = marginHost is not null;
        var marginRootStyle = marginRootElBox?.Style;
        var totalPages = pageFragments.Count;

        // Cache the per-page margin-box resolution by selector context (post-PR-#178 review P3): which
        // @page rules apply to a page depends ONLY on (first, parity, blank, named-page), so at most a
        // handful of distinct contexts exist across the whole document — resolve each once instead of
        // re-walking the stylesheets (× specificity tiers, × 2 for boxes + declarations) for every page.
        var marginBoxCache = hasMarginBoxes
            ? new Dictionary<(bool First, bool Right, bool Blank, string? Name),
                (ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox> Boxes,
                 ImmutableArray<NetPdf.Css.Parser.CssDeclaration> Decls)>()
            : null;

        // CSS-BACKGROUND-IMAGE-UNSUPPORTED-001 is a once-per-RENDER diagnostic (matching the body's
        // FragmentPainter flag), so this lives OUTSIDE the per-page loop — a margin box with an
        // unsupported background-image variant is diagnosed once for the document, not once per page
        // (PR #176 Copilot review).
        var marginVariantReported = false;

        // Cross-page running content (cycle 5): one MarginContentContext PER PAGE for the named strings
        // (CSS GCPM L3) — per page, string(name)/first reads the FIRST string-set on that page (or the
        // value carried forward from earlier pages if the page sets nothing), and string(name, last)
        // reads the LAST on the page (or carried). Map each laid-out element to its first page from the
        // per-page fragment lists; CollectPerPage buckets the string-set assignments by that page (an
        // inline setter falls back to its nearest rendered ancestor's page) and carries each named
        // string forward. (element() running content stays whole-document this cycle — cycle 5b.)
        var marginContexts = System.Array.Empty<CssContentList.MarginContentContext>();
        if (hasMarginBoxes)
        {
            var elementToPage = new Dictionary<IElement, int>();
            for (var p = 0; p < pageFragments.Count; p++)
            {
                foreach (var frag in pageFragments[p])
                {
                    if (frag.Box.SourceElement is { } el) elementToPage.TryAdd(el, p);
                }
            }
            marginContexts = MarginContentCollector.CollectPerPage(
                marginHost!, phase2.Cascade, elementToPage, totalPages);
        }

        // Per-page @page GEOMETRY (per-page-geometry cycle): a page's `:left`/`:right`/`:blank`/named
        // selectors can set its OWN margins + size (CSS Page 3 — duplex margins, named-page sizes). The
        // size/margin resolvers are now context-aware; each distinct page context's geometry is resolved
        // ONCE (cached by the same key the margin boxes use). FIRST CUT (documented approximation): the
        // per-page geometry drives the PAINT only — the page's MediaBox, its margin boxes, and the body's
        // paint offsets — while the BODY stays FRAGMENTED against the document-level (bare-@page) content
        // area (true per-page fragmentation needs an iterative layout, deferrals.md). For a page whose
        // geometry equals the document default (every page when no per-page-specific rule exists) this is
        // byte-identical. The fall-back base is the CONFIGURED options (not the doc-level overrides), so an
        // unspecified side matches the document default exactly.
        var pageGeometryCache =
            new Dictionary<(bool First, bool Right, bool Blank, string? Name),
                (PageSize Size, PageMargins Margins, MediaBoxSize Box)>();
        (PageSize Size, PageMargins Margins, MediaBoxSize Box) ResolvePageGeometry(
            AtPageRules.PageSelectorContext ctx)
        {
            var key = (ctx.IsFirstPage, ctx.IsRightPage, ctx.IsBlank, ctx.AssignedPageName);
            if (pageGeometryCache.TryGetValue(key, out var cached)) return cached;
            var sz = options.PageSize;
            if (options.PreferCssPageSize
                && AtPageSizeResolver.Resolve(phase2.Sheets, media, ctx) is { } cs)
                sz = new PageSize(cs.WidthPx, cs.HeightPx);
            var mg = options.Margins;
            var pm = AtPageMarginResolver.Resolve(phase2.Sheets, media, sz.WidthPx, sz.HeightPx, ctx);
            if (pm.HasAny)
                mg = new PageMargins(
                    TopPx: pm.TopPx ?? mg.TopPx, RightPx: pm.RightPx ?? mg.RightPx,
                    BottomPx: pm.BottomPx ?? mg.BottomPx, LeftPx: pm.LeftPx ?? mg.LeftPx);
            var result = (sz, mg,
                new MediaBoxSize(PdfUnits.PxToPt(sz.WidthPx), PdfUnits.PxToPt(sz.HeightPx)));
            pageGeometryCache[key] = result;
            return result;
        }

        // PHASE B — paint each laid-out page (cycle 3/4). Pages are page-local (a fresh
        // fragmentainer per page); per-page variation is the page-number counters + (per-page-geometry
        // cycle) each page's own MediaBox / margins.
        // Font-dedup-across-pages: ONE text-paint session spans every page — it collects all pages'
        // glyphs (one HarfBuzz shaping pass) and, in Finish() after the loop, subsets + embeds each font
        // ONCE from the cross-page union (so a shared font is embedded once, not once per page). The
        // background/border/image painting stays per page inside the loop; the per-page text replay in
        // Finish() appends AFTER it, preserving the "text over backgrounds" layering.
        var textSession = new TextPainter.TextPaintSession(
            shaper, mediaBox.HeightPts, margins.LeftPx, margins.TopPx, diagnostics,
            imageCache.TextShadowBoxes);
        // Phase 4 outlines (PR 4): <h1>–<h6> → the document outline. Collected in page order (= document
        // order) so the level-nesting builds the right tree; the seen-set dedups a split heading.
        var seenHeadings = new System.Collections.Generic.HashSet<AngleSharp.Dom.IElement>();
        for (var pageIndex = 0; pageIndex < pageFragments.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bodyFragments = pageFragments[pageIndex];

            // The page's selector context (cycle 6 + 7): first-page + parity + blank + the used `page`
            // name of its first content box. Drives BOTH the per-page geometry AND the margin-box
            // selectors. The :left/:right parity reflects the document direction + the forced first-page
            // side, consistent with the forced-break parity (PageNumberHasParity).
            var pageName = FirstContentPageName(bodyFragments);
            var pageCtx = new AtPageRules.PageSelectorContext(
                pageIndex, IsBlank: bodyFragments.Count == 0,
                AssignedPageName: pageName.Length == 0 ? null : pageName,
                StartsOnVerso: firstPageParityOffset == 1, IsRtl: documentIsRtl);

            // Per-page geometry (size + margins + MediaBox) — equals the document default when no
            // per-page-specific @page rule applies (then byte-identical to the pre-cycle output).
            var (ppSize, ppMargins, ppMediaBox) = ResolvePageGeometry(pageCtx);

            // Per-page-geometry overflow (review F3): the body fragments against the bare-@page content
            // area (the layout baseline), so a page with SMALLER per-page geometry (a `:left`/named page
            // with a smaller size or larger margins) can clip content that fit the bare area. Check this
            // page's fragments against its OWN content rect; the single overflow diagnostic is emitted
            // after the loop. (Equal to the bare check when the page's geometry matches the baseline.)
            var ppContentInlinePx = Math.Max(0, ppSize.WidthPx - ppMargins.LeftPx - ppMargins.RightPx);
            var ppContentBlockPx = Math.Max(0, ppSize.HeightPx - ppMargins.TopPx - ppMargins.BottomPx);
            if (FragmentsOverflowContentRect(bodyFragments, ppContentInlinePx, ppContentBlockPx))
            {
                clippedOrTruncated = true;
            }

            var page = document.AddPage(ppMediaBox);

            // Phase 4 transforms (review [P1]) — the per-page box → effective (ancestor-composed) cm,
            // shared by the decoration / image / text passes so a transformed element transforms its
            // whole subtree. Empty (no allocation) when nothing on the page is transformed.
            var effectiveTransforms = TransformResolver.BuildEffectiveTransforms(
                bodyFragments, imageCache.TransformBoxes, ppMargins.LeftPx, ppMargins.TopPx, ppMediaBox.HeightPts);

            FragmentPainter.PaintFragments(
                bodyFragments, page, ppMediaBox.HeightPts, ppMargins.LeftPx, ppMargins.TopPx,
                paintBackgrounds: options.PrintBackgrounds, diagnostics,
                imageCache, document, effectiveTransforms);

            // Replaced-element content (img-pipeline cycle): each <img> fragment places its
            // decoded XObject at its content box — over every band/border, under the glyphs.
            ImagePainter.PaintImages(
                bodyFragments, page, document, imageCache, ppMediaBox.HeightPts,
                ppMargins.LeftPx, ppMargins.TopPx, diagnostics, effectiveTransforms);

            // Hyperlinks (Phase 4 PR 4): <a href> elements → PDF /Link annotations over their fragments.
            LinkAnnotationCollector.AddLinks(
                page, bodyFragments, ppMediaBox.HeightPts, ppMargins.LeftPx, ppMargins.TopPx);

            // Document outline (Phase 4 PR 4): <h1>–<h6> headings → /Outlines bookmarks.
            OutlineCollector.Collect(
                document, page, bodyFragments, ppMediaBox.HeightPts, ppMargins.TopPx, seenHeadings);

            var textFragments = bodyFragments;
            if (hasMarginBoxes)
            {
                // The per-page selector context (cycle 6 / 7) computed at the top of the loop picks which
                // @page rules' margin boxes + page-context style apply, so a left page paints `@page :left`'s
                // header, a named page `@page <name>`'s, etc., over the bare @page. Per-page GEOMETRY (size /
                // margins) now ALSO varies per page (per-page-geometry cycle) — `ppSize`/`ppMargins` above —
                // and the margin boxes lay out against the page's OWN size + margins.
                var ctxKey = (pageCtx.IsFirstPage, pageCtx.IsRightPage, pageCtx.IsBlank, pageCtx.AssignedPageName);
                if (!marginBoxCache!.TryGetValue(ctxKey, out var resolvedForCtx))
                {
                    resolvedForCtx = (
                        AtPageMarginBoxResolver.Resolve(phase2.Sheets, media, pageCtx),
                        AtPageMarginBoxResolver.PageContextDeclarations(phase2.Sheets, media, pageCtx));
                    marginBoxCache[ctxKey] = resolvedForCtx;
                }
                var pageBoxes = resolvedForCtx.Boxes;
                var pageDecls = resolvedForCtx.Decls;

                // Real per-page counters (cycle 4): counter(page) = this page's 1-based number;
                // counter(pages) = the now-known document total. Margin boxes paint through the
                // SAME TextPainter pass as the body so a shared font is subset + embedded once.
                var pageCounters = new CssContentList.PageCounters(
                    page: pageIndex + 1, pages: totalPages);
                var marginResult = PageMarginBoxPainter.Layout(
                    pageBoxes, ppSize.WidthPx, ppSize.HeightPx,
                    ppMargins.TopPx, ppMargins.RightPx, ppMargins.BottomPx, ppMargins.LeftPx,
                    ppMargins.LeftPx, ppMargins.TopPx, marginHost!, marginRootStyle!,
                    pageDecls, pageCounters, marginContexts[pageIndex], shaper, diagnostics);
                // Background bands paint BEHIND the header/footer text; PrintBackgrounds-gated.
                if (options.PrintBackgrounds && marginResult.Backgrounds.Count > 0)
                    PageMarginBoxPainter.PaintBackgrounds(page, marginResult.Backgrounds, ppMediaBox.HeightPts);
                // Background images tile over the bands, under borders/text; PrintBackgrounds-gated.
                if (options.PrintBackgrounds && marginResult.BackgroundImages.Count > 0)
                {
                    foreach (var bi in marginResult.BackgroundImages)
                    {
                        if (!imageCache.TryGetByRawUrl(bi.RawUrl, out var biEntry)) continue;
                        FragmentPainter.PaintBackgroundImageTiles(
                            page, document, biEntry, ppMediaBox.HeightPts,
                            bi.LeftPx, bi.TopPx, bi.WidthPx, bi.HeightPx,
                            diagnostics, ref marginVariantReported,
                            bi.RepeatRaw, bi.SizeRaw, bi.PositionRaw,
                            bi.ClipLeftPx, bi.ClipTopPx, bi.ClipWidthPx, bi.ClipHeightPx,
                            bi.ClipRadiiPx); // a margin-box border-radius rounds the image clip
                    }
                }
                // Margin-box borders paint over the bands, before the text — NOT PrintBackgrounds-gated.
                if (marginResult.Borders.Count > 0)
                    PageMarginBoxPainter.PaintBorders(page, marginResult.Borders, ppMediaBox.HeightPts, diagnostics);
                if (marginResult.Fragments.Count > 0)
                {
                    var combined = new List<BoxFragment>(bodyFragments.Count + marginResult.Fragments.Count);
                    combined.AddRange(bodyFragments);
                    combined.AddRange(marginResult.Fragments);
                    textFragments = combined;
                }
            }

            // Text paints OVER backgrounds + borders, ONE pass over body + margin-box fragments.
            // Collect this page's glyphs + draw commands now (the kept-alive shaper shapes once); the
            // actual glyph runs are replayed in Finish() below, after every font is subset + embedded
            // once from the cross-page union. Per-page geometry: the y-flip + content origin use the
            // page's OWN MediaBox height + margins (= the session defaults when no per-page rule applies).
            textSession.CollectPage(page, textFragments, ppMediaBox.HeightPts, ppMargins.LeftPx, ppMargins.TopPx,
                effectiveTransforms);
        }

        // Inline overflow / the page-count cap / per-page-geometry clipping (review F3) surfaced once.
        // Block content past page 1 FLOWS onto further pages, so it is not truncated. Emitted here (after
        // the paint loop) so the per-page-geometry overflow check above is included.
        if (!fontResolutionFailed && clippedOrTruncated)
        {
            EmitOverflowClipped(diagnostics);
        }

        // Build each font ONCE (cross-page union) + replay every page's text — font-dedup-across-pages.
        // Honors the caller's cancellation/timeout (this pass subsets/embeds fonts + replays all pages'
        // text after the per-page loop — review P2).
        textSession.Finish(document, cancellationToken);

        var bytes = document.Save();
        return new RenderOutcome(bytes, document.Pages.Count, diagnostics.Items);
    }

    /// <summary>The first DOM-element-backed box in the tree (depth-first) — the document root
    /// element's box. Its <see cref="Box.SourceElement"/> is the <c>attr()</c> host for page
    /// margin-box content, and its <see cref="Box.Style"/> is the top of the margin-box style
    /// inheritance chain (root → page context → margin box). <see langword="null"/> only for an
    /// element-free document (then margin boxes are skipped).</summary>
    private static Box? FindRootElementBox(Box? box)
    {
        if (box is null) return null;
        if (box.SourceElement is not null) return box;
        foreach (var child in box.Children)
            if (FindRootElementBox(child) is { } found) return found;
        return null;
    }

    /// <summary>PR #207 review [P2] — the box whose computed style carries the document-level
    /// value of an INHERITED property (e.g. <c>orphans</c> / <c>widows</c>). BoxBuilder roots
    /// the tree at a SYNTHETIC box with the property's initial value, so reading it would never
    /// see authored CSS. Prefers the <c>&lt;body&gt;</c> box (it inherits <c>&lt;html&gt;</c> /
    /// <c>:root</c> and its own declaration — the usual place these are authored), falls back to
    /// the root element (<c>&lt;html&gt;</c>), then the synthetic root (initial default).</summary>
    private static Box ResolveDocumentRootForInheritedProps(Box syntheticRoot)
    {
        var rootEl = FindRootElementBox(syntheticRoot);
        if (rootEl is null) return syntheticRoot;
        return FindBodyBox(rootEl) ?? rootEl;
    }

    private static Box? FindBodyBox(Box box)
    {
        if (box.SourceElement is { } el
            && el.LocalName.Equals("body", System.StringComparison.OrdinalIgnoreCase))
            return box;
        foreach (var child in box.Children)
            if (FindBodyBox(child) is { } found) return found;
        return null;
    }

    /// <summary>The used <c>page</c> name (CSS Page 3 §3.4) of the FIRST CONTENT box on a page — the page's
    /// break-triggering box (cycle 7 + PR #179 review P1; <see cref="Box.PageName"/> computed at build
    /// time). The <c>&lt;html&gt;</c> / <c>&lt;body&gt;</c> structural wrappers SPAN every page with
    /// <c>page: auto</c>, so their fragment leads the list but doesn't name the page; they're skipped to
    /// reach the first real content box (which, since the layouter forces a break on a <c>page</c> change,
    /// is the named element on a named page). Anonymous fragments (no source element) are skipped too; the
    /// empty string for an empty / unnamed page.</summary>
    private static string FirstContentPageName(IReadOnlyList<BoxFragment> fragments)
    {
        foreach (var fragment in fragments)
            if (fragment.Box.SourceElement is { } el
                && !el.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase)
                && !el.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase))
                return fragment.Box.PageName;
        return string.Empty;
    }

    private static void EmitTextFontUnresolved(IDiagnosticsSink diagnostics, string detail) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PaintTextFontUnresolved001,
            "A font could not be resolved during layout, so the affected text was not painted: " +
            detail + " The rest of the document still renders. A bundled deterministic last-resort " +
            "font (so the default path always resolves) is a tracked follow-up " +
            "(deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));

    /// <summary>Backstop page count for the multi-page driver loop (layout→PDF cycle 3).
    /// Forward progress is guaranteed by the forced-overflow penalty, so this never trips for
    /// real content; it bounds the loop if a layouter fails to advance its continuation.</summary>
    private const int MaxPages = 20_000;

    private static void EmitOverflowClipped(IDiagnosticsSink diagnostics) =>
        diagnostics.Emit(new Diagnostic(
            DiagnosticCodes.PdfContentOverflowTruncated001,
            "Some content does not fit its page's content box and is clipped — e.g. an element " +
            "wider than the content area (NetPdf paginates the block axis across pages but does " +
            "not split the inline axis), a negative / absolute offset outside the page, or " +
            "content that exceeded the page-count safety cap. Block content that overflows the " +
            "block axis now FLOWS onto further pages (deferrals.md#layout-to-pdf-pipeline).",
            DiagnosticSeverity.Warning));

    // CSS Fragmentation L3 §3.1.1 + §4.3 / CSS Page L3 §3.6 — the forced break-before page-parity that
    // selects the document's STARTING side. A break-before on a first in-flow child propagates to its
    // container (§3.1.1), so the root's OWN break-before AND every first-in-flow descendant's coincide
    // at the document start; when these side-constrained breaks combine, "the value on the LATEST element
    // in flow wins" (§4.3) — the first-in-flow-child chain runs outermost→innermost (earliest→latest), so
    // the DEEPEST non-Any parity wins. The walk starts at the SYNTHETIC root so <html> / <body>'s OWN
    // break-before is included (PR #219 review [P1 #3]; the synthetic root's own value is the Any default).
    // A parity-LESS `break-before: page` / `always` / `all` carries no side, so it does NOT shift the
    // starting side — CSS Page §3.6: any leading empty page it would force is suppressed and `:first`
    // matches the first PRINTED page (PR #219 review [P1 #2]). Out-of-flow (abspos / fixed) leading
    // children don't start the flow.
    private static PageParity FirstContentForcedStartParity(Box syntheticRoot)
    {
        var parity = PageParity.Any;
        for (Box? box = syntheticRoot; box is not null; box = FirstInFlowChild(box))
            parity = ComputedStyleLayoutExtensions.CombineForcedParityLatestWins(
                parity, box.Style.ForcedPageBreakParityBefore());
        return parity;
    }

    private static Box? FirstInFlowChild(Box box)
    {
        foreach (var child in box.Children)
            if (!child.Style.IsOutOfFlow()) return child;
        return null;
    }

    // CSS Fragmentation L3 §3.1 — does a 1-based page number satisfy a forced-break parity? Delegates
    // to the shared `PageProgression` (PR #219 review [P2 #5]) so this can't drift from the
    // `@page :left`/`:right` selector parity (`PageSelectorContext.IsRightPage`). `recto` / `verso` are
    // direction-independent page-NUMBER parities (recto = odd); the physical `left` / `right` swap in
    // RTL (the recto is the physical LEFT page). The first-page starting side is folded into
    // `pageNumber` by the caller (the `firstPageParityOffset`), so the progression is recto-first here.
    private static bool PageNumberHasParity(int pageNumber, PageParity parity, bool isRtl)
    {
        var progression = new PageProgression(IsRtl: isRtl);
        return parity switch
        {
            PageParity.Recto => progression.IsRecto(pageNumber),
            PageParity.Verso => !progression.IsRecto(pageNumber),
            PageParity.Right => progression.IsRightPage(pageNumber),
            PageParity.Left => !progression.IsRightPage(pageNumber),
            _ => true,   // PageParity.Any — no constraint.
        };
    }

    // A fragment whose border box leaves the content rect [0,contentInline]×[0,contentBlock]
    // (CSS px, content-area-relative) paints into the margins / off-page and may be clipped.
    private static bool FragmentsOverflowContentRect(
        IReadOnlyList<BoxFragment> fragments, double contentInlinePx, double contentBlockPx)
    {
        const double eps = 0.5; // px — edge-touching (e.g. a full-width block) is not overflow.
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            if (f.InlineSize <= 0 || f.BlockSize <= 0) continue;
            if (f.InlineOffset < -eps || f.BlockOffset < -eps
                || f.InlineOffset + f.InlineSize > contentInlinePx + eps
                || f.BlockOffset + f.BlockSize > contentBlockPx + eps)
                return true;
        }
        return false;
    }

    private static string PdfVersionString(PdfVersion version) => version switch
    {
        PdfVersion.V1_4 => "1.4",
        PdfVersion.V1_5 => "1.5",
        PdfVersion.V1_6 => "1.6",
        PdfVersion.V2_0 => "2.0",
        _ => "1.7",
    };
}
