// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Stable string constants for every diagnostic code emitted by NetPdf. The single
/// source of truth for the registry is <c>docs/diagnostics-codes.md</c>; the constants
/// here let emission sites and tests share one literal so a typo at the call site
/// is impossible. Constants are kept <see langword="internal"/>: consumers receive the
/// codes as <see cref="Diagnostic.Code"/> string values, not via a named-constant API.
/// </summary>
internal static class DiagnosticCodes
{
    // region HTML-*

    /// <summary>
    /// A <c>&lt;script&gt;</c> element was encountered. NetPdf does not execute JavaScript in v1.
    /// The element was removed from the rendering tree. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlScriptIgnored001 = "HTML-SCRIPT-IGNORED-001";

    /// <summary>
    /// An <c>href</c> / <c>xlink:href</c> / <c>action</c> / <c>src</c> / <c>data</c> /
    /// <c>content</c> attribute carried a <c>javascript:</c> / <c>vbscript:</c> /
    /// <c>data:</c> URL. The attribute was removed so the URL is not honored downstream;
    /// the surrounding element + its text content remain. Phase A (Phase 2 deep review
    /// security pass) widened the strip beyond the original <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c>
    /// /<c>xlink:href</c> coverage to also include <c>&lt;form action&gt;</c>,
    /// <c>&lt;iframe src&gt;</c>, <c>&lt;object data&gt;</c>, <c>&lt;embed src&gt;</c>,
    /// <c>&lt;base href&gt;</c>, and <c>&lt;meta http-equiv="refresh" content="0;url=..."&gt;</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlJavaScriptUrlIgnored001 = "HTML-JAVASCRIPT-URL-IGNORED-001";

    /// <summary>
    /// An <c>on*</c> event-handler attribute (e.g., <c>onclick</c>, <c>onload</c>,
    /// <c>onerror</c>, <c>onmouseover</c>) was found on an element. NetPdf does not
    /// execute scripts in v1, so the handler is inert today — but Phase 5 PDF/UA
    /// emission could surface attribute values into accessibility metadata. The
    /// attribute is stripped at parse time as a defense-in-depth measure.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string HtmlEventHandlerIgnored001 = "HTML-EVENT-HANDLER-IGNORED-001";

    /// <summary>
    /// A parsed HTML document exceeded one of the per-document DoS caps:
    /// total element count, nesting depth, attributes per element, attribute
    /// value length, or text-node content length. The offending region was
    /// truncated (excess elements / attributes removed; excess text /
    /// attribute-value strings clamped) so downstream stages still see a
    /// rendering-shaped document. One diagnostic is emitted per violation
    /// kind, naming the cap that fired. Per Phase B B-1.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlDomLimitExceeded001 = "HTML-DOM-LIMIT-EXCEEDED-001";

    /// <summary>
    /// The iterative HTML strip pass (per Phase B B-4 — defense against
    /// AngleSharp normalization or SVG <c>&lt;foreignObject&gt;</c>
    /// re-introducing stripped script / javascript-URL / event-handler
    /// content) failed to converge after the maximum iteration cap.
    /// Dangerous content may still be present in the DOM. The diagnostic
    /// message names the surviving kind(s) — <c>&lt;script&gt;</c>,
    /// <c>on*</c> event handler, or <c>javascript:</c>/<c>vbscript:</c>/<c>data:</c>
    /// URL — so the author can investigate.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlStripNotStable001 = "HTML-STRIP-NOT-STABLE-001";

    /// <summary>
    /// The HTML input length exceeded the per-document character cap
    /// (<see cref="HtmlParsingHost.MaxInputLength"/>). The parse is
    /// rejected before AngleSharp materializes the tree — defends against
    /// "1 GiB single-string" attacks where post-parse caps would only
    /// fire after the parser had already consumed memory + CPU on the
    /// full input. Per Phase B B-1 follow-up (PR #16 review).
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string HtmlInputTooLarge001 = "HTML-INPUT-TOO-LARGE-001";

    // endregion HTML-*

    // region CSS-*

    /// <summary>
    /// A CSS rule was malformed and skipped. The cascade resolver emits this when a rule's
    /// selector text fails to parse (e.g., unsupported pseudo-class, unterminated function).
    /// The rest of the stylesheet still loads. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssParseWarning001 = "CSS-PARSE-WARNING-001";

    /// <summary>
    /// A <c>:has()</c> selector was encountered. NetPdf's v1 contract is that <c>:has()</c>
    /// parses but never matches — the rule has no rendering effect. Roadmap v1.4 will plug
    /// in real <c>:has()</c> evaluation. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssHasRenderingNotImplemented001 = "CSS-HAS-RENDERING-NOT-IMPLEMENTED-001";

    /// <summary>
    /// An unrecognized at-rule was preserved in the AST but had no rendering effect — the
    /// cascade resolver couldn't decompose its body or its conditions weren't evaluable.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssAtRuleUnknown001 = "CSS-AT-RULE-UNKNOWN-001";

    /// <summary>
    /// An <c>@container</c> rule was encountered. NetPdf does not evaluate container queries
    /// in v1 — the contained rules are skipped. Roadmap v1.4 will add container-query
    /// evaluation. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssContainerQueryUnsupported001 = "CSS-CONTAINER-QUERY-UNSUPPORTED-001";

    /// <summary>
    /// A <c>var()</c> chain produced a circular reference (e.g.,
    /// <c>--a: var(--b); --b: var(--a)</c>); the substitution stopped + resolved to the
    /// fallback value or <c>unset</c>. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssVarCircular001 = "CSS-VAR-CIRCULAR-001";

    /// <summary>
    /// A <c>var()</c> substitution exceeded the user-agent's depth or output-length
    /// safety limit. Distinct from a circular reference — the chain is acyclic but
    /// pathological (e.g., long non-cyclic chain past 32 frames, or an exponentially
    /// expanding chain past 1 MiB output). The substitution is treated as "invalid at
    /// computed value time" per CSS Custom Properties L1 §3.5; references resolve to
    /// the fallback or <c>unset</c>. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssVarExpansionLimit001 = "CSS-VAR-EXPANSION-LIMIT-001";

    /// <summary>
    /// A <c>calc()</c> / <c>min()</c> / <c>max()</c> / <c>clamp()</c> / <c>abs()</c> /
    /// <c>sign()</c> expression was syntactically invalid or had a type mismatch
    /// (e.g., adding a number to a length). Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssCalcInvalid001 = "CSS-CALC-INVALID-001";

    /// <summary>
    /// A <c>calc()</c> expression divided by zero. Per CSS Values L4 §10.1, the result
    /// is treated as "invalid at computed value time"; the property's initial value
    /// applies. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssCalcDivByZero001 = "CSS-CALC-DIV-BY-ZERO-001";

    /// <summary>
    /// A property's value text could not be parsed into the property's typed value
    /// per its declared <c>PropertyType</c> (e.g., <c>color: not-a-color</c>,
    /// <c>width: nonsense</c>, <c>display: foo</c>). The cascade's "invalid at computed
    /// value time" rule applies — the property's initial value (or the inherited value
    /// for inherited properties) is used. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssPropertyValueInvalid001 = "CSS-PROPERTY-VALUE-INVALID-001";

    /// <summary>
    /// A <c>content:</c> value (on <c>::before</c> / <c>::after</c> / <c>::marker</c>)
    /// used a function / keyword that cycle 1 doesn't yet handle (<c>counter()</c> /
    /// <c>counters()</c> / <c>url()</c> / <c>image()</c> / <c>image-set()</c> /
    /// <c>linear-gradient()</c> / <c>open-quote</c> / <c>close-quote</c> /
    /// <c>no-open-quote</c> / <c>no-close-quote</c>). The pseudo-element generates no
    /// box. Roadmap cycle 2 ships counter machinery + the resource pipeline +
    /// quotation-stack tracking. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssContentFunctionUnsupported001 = "CSS-CONTENT-FUNCTION-UNSUPPORTED-001";

    /// <summary>
    /// A modern <c>attr(name type, fallback)</c> form was rejected — cycle 1 supports
    /// the bare <c>attr(name)</c> form only. The pseudo-element generates no box rather
    /// than silently dropping the type / fallback args. Roadmap cycle 2 delivers the
    /// typed-value pipeline. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string CssAttrMultiArgUnsupported001 = "CSS-ATTR-MULTI-ARG-UNSUPPORTED-001";

    /// <summary>
    /// A modern color function (<c>oklch()</c> / <c>oklab()</c> / <c>lab()</c> /
    /// <c>lch()</c> / <c>color()</c> / <c>color-mix()</c>) was used in a property
    /// value. Cycle 1 rejects these — the cascade's "invalid at computed value
    /// time" rule applies. Roadmap cycle 2 ships sRGB-conversion so the rendered
    /// color is approximate but visible. Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssModernColorFunctionUnsupported001 = "CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001";

    /// <summary>
    /// A <c>::before</c> / <c>::after</c> rule targeted a replaced element
    /// (<c>&lt;img&gt;</c> / <c>&lt;video&gt;</c> / <c>&lt;canvas&gt;</c> /
    /// <c>&lt;iframe&gt;</c> / <c>&lt;object&gt;</c> / <c>&lt;embed&gt;</c>); per
    /// CSS Pseudo L4 §3 the pseudo-element is suppressed because replaced elements
    /// are atomic and can't host generated content. The author rule has no effect.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string CssPseudoSuppressedOnReplaced001 = "CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001";

    // endregion CSS-*

    // region PAGINATION-*

    /// <summary>
    /// The bounded DP optimizer in <c>NetPdf.Paginate</c> exceeded its
    /// time / candidate-set budget for the document under layout, and the
    /// paginator fell back to greedy pagination (take each break-point as
    /// it arrives, no lookahead). The PDF still emits cleanly; layout
    /// quality is the same as a non-optimizing renderer (typically just
    /// "fine" — orphan / widow / heading-stranding penalties are not
    /// minimized for this document). Per Phase 3 §pagination.
    /// Severity: <see cref="DiagnosticSeverity.Info"/>.
    /// </summary>
    public const string PaginationOptimizerFallback001 = "PAGINATION-OPTIMIZER-FALLBACK-001";

    /// <summary>
    /// A region marked <c>break-inside: avoid</c> (or otherwise un-splittable
    /// per the cost model) is taller than a single fragmentainer (page) and
    /// had to be split anyway. The first piece occupies the remainder of
    /// the current page; the rest cascades onto subsequent pages. PDF
    /// renders correctly but the author's break constraint was violated.
    /// Per Phase 3 §pagination + CSS Fragmentation L3 §3.2 last-resort
    /// fallback. Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string PaginationForcedOverflow001 = "PAGINATION-FORCED-OVERFLOW-001";

    // endregion PAGINATION-*

    // region LAYOUT-*

    /// <summary>
    /// Per Phase 3 Task 11 cycle 1 sub-cycle 1 — emitted by the block
    /// layouter when a block container with inline-level children is
    /// encountered but the pipeline did not supply an inline shaper
    /// resolver. The inline children are skipped (rendered as empty
    /// space, equivalent to the pre-sub-cycle-1 behavior) so layout
    /// can still complete. Production callers wire a shaper resolver
    /// when constructing the renderer; the diagnostic exists for
    /// test harnesses and tooling that drive the layouter directly.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutInlineSkippedNoShaperResolver001 = "LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001";

    /// <summary>
    /// Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review
    /// Finding #4 — emitted by the block layouter when an inline-only
    /// block contains an atomic inline descendant
    /// (<c>inline-block</c>, <c>inline-flex</c>, <c>inline-grid</c>,
    /// <c>inline-table</c>, <c>inline replaced</c>) whose dedicated
    /// layout pipeline has not yet shipped. The atomic inline is
    /// skipped in the line; surrounding text continues to render.
    /// Sub-cycle 2 will inject atomic-inline placeholders into the
    /// line via per-layouter intrinsic-sizing hooks.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutInlineAtomicNotSupported001 = "LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening review
    /// Finding #6 — emitted by the block layouter when the inline
    /// pass throws <see cref="System.NotSupportedException"/> for a
    /// configuration the inline layouter doesn't yet support (e.g.,
    /// per-source-TextRun <c>word-break: keep-all</c> mismatch — CJK
    /// semantics need UAX #24 script detection). The inline-only
    /// block emits no fragment + the block layouter continues with
    /// the next child; the exception message is carried in the
    /// diagnostic's <c>MessageDetail</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutInlineUnsupported001 = "LAYOUT-INLINE-UNSUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 1 — emitted by the table
    /// layouter when the input box tree exercises a CSS Tables L3
    /// feature the sub-cycle 1 algorithm doesn't yet implement
    /// (currently: <c>colspan</c> / <c>rowspan</c> cell merging;
    /// sub-cycle 2 will add <c>table-layout: auto</c> /
    /// <c>fixed</c>, <c>border-collapse</c>, captions,
    /// <c>&lt;col&gt;</c> widths, header/footer repetition across
    /// pages). The table renders with the feature silently
    /// ignored. See
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c> for
    /// the full deferral list. Severity:
    /// <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableFeatureUnsupported001 = "LAYOUT-TABLE-FEATURE-UNSUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 2 hardening Finding 4 — emitted
    /// by the table layouter when a table's cumulative
    /// <c>rowspan × colspan</c> slot count would exceed the
    /// 1,000,000 slot DoS budget. Cells crossing the budget are
    /// capped at <c>rowspan = colspan = 1</c>; the table still
    /// renders (truncated geometry, not dropped content). Defends
    /// against hostile HTML where legal attribute values (e.g.,
    /// <c>rowspan="65534" colspan="1000"</c> on multiple cells) would
    /// force unbounded CPU + memory work in the placement pass.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableSlotBudgetExceeded001 = "LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 — emitted
    /// by the table layouter when the sum of declared column widths
    /// under <c>table-layout: fixed</c> exceeds the table wrapper's
    /// content-inline-size. CSS 2.1 §17.5.2.1 says the table grid's
    /// inline extent grows to fit the declared column widths in that
    /// case — the table overflows its wrapper in the inline axis. The
    /// layouter keeps the declared widths intact (row + caption
    /// fragments grow to the column sum); the diagnostic surfaces the
    /// overflow so authors can tune their declarations.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableInlineOverflow001 = "LAYOUT-TABLE-INLINE-OVERFLOW-001";

    /// <summary>
    /// Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 — emitted
    /// by the table layouter when the auto-table-layout intrinsic-
    /// measurement pass exceeds its per-table speculative-measurement
    /// budget. Each cell normally runs two speculative nested
    /// BlockLayouter passes (min-content at 1px + max-content at 1e6
    /// px) — i.e., 2 ops per cell. For tables with thousands of cells
    /// the cumulative work is bounded by this budget; cells beyond the
    /// cap fall back to <c>(minContent=0,
    /// maxContent=contentInlineSize)</c>, producing a degenerate
    /// equal-split-like distribution rather than DoS-amplifying the
    /// speculative passes. Defends against hostile HTML with
    /// pathologically deep cell content trees in very large tables.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableIntrinsicMeasurementBudgetExceeded001 =
        "LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001";

    /// <summary>
    /// Per Phase 3 Task 13 cycle 1 hardening Finding 5 — emitted by the
    /// table layouter when the break resolver returns
    /// <c>BreakAction.Rewind</c> at a table row boundary. Cycle 1 does
    /// NOT register per-row checkpoints (the outer block layouter owns
    /// the pre-table rewind frontier), so a resolver-named rewind
    /// checkpoint inside the table is a contract violation. The
    /// layouter falls back to <c>Continue</c> (preserving the pre-
    /// finding behavior) + surfaces this diagnostic so authors /
    /// integrators see the dropped rewind. Per-row checkpoint capture
    /// is cycle 2+ scope.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableRewindNotSupported001 =
        "LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001";

    /// <summary>
    /// Per Phase 3 Task 13 cycle 1 hardening Finding 6 — emitted by the
    /// table layouter when a row break would cut through a cell whose
    /// <c>rowspan&gt;1</c> origin row commits on the current page but
    /// whose span extends past the break. Cycle 1 keeps rowspan cells
    /// atomic across pages: the layouter forces the break BEFORE the
    /// rowspan origin row (the whole spanning cell stays together on
    /// the next page) when at least one row + optional captions have
    /// already committed on the current page; otherwise it falls back
    /// to the existing forced-overflow path. CSS Tables L3 §11 spec-
    /// strict rowspan distribution across pages is sub-cycle 6+ scope.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableRowspanCrossesPage001 =
        "LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001";

    /// <summary>
    /// Per Phase 3 Task 13 cycle 2 — emitted by the table layouter when
    /// the combined <c>&lt;thead&gt;</c> + <c>&lt;tfoot&gt;</c> stack
    /// height (header rows + footer rows) exceeds the fragmentainer's
    /// available block-size, leaving no room to repeat the header +
    /// footer on every page along with any body row. Per CSS Tables L3
    /// §3.6 / §11 the header + footer repeat at the top + bottom of
    /// each page; if they exceed the fragmentainer no body row can fit
    /// on a page that also honors the repeat contract. The layouter
    /// commits the header + footer once (atomically) on the current
    /// page, skips the body to avoid infinite continuation loops, and
    /// surfaces this diagnostic so authors can reduce header / footer
    /// content or widen the page.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutTableHeaderFooterOversized001 =
        "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 2 hardening (Finding #3) — emitted by
    /// the multicol layouter when the author-supplied
    /// <c>column-count</c> exceeds the internal safety cap (= 1000)
    /// and is silently clamped. The clamp protects against the
    /// per-column arithmetic's O(N) cost on adversarial inputs (a
    /// <c>column-count: 1000000</c> would otherwise allocate ~1M
    /// per-column geometry slots + invoke 1M nested BlockLayouter
    /// calls per page). Authors who legitimately hit the cap
    /// (generated CSS, configuration mistakes) see the warning + can
    /// reduce the requested column count. The rendered output will
    /// have AT MOST 1000 columns.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMulticolColumnCountClamped001 =
        "LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 1 — emitted by the multicol layouter
    /// when a multicol container's in-flow content can't make forward
    /// progress through pagination. Cycle 2 ships multi-page multicol
    /// via <c>MulticolContinuation</c> + cycle 2 hardening Finding #1
    /// lifted the recursion-depth limit on continuation propagation;
    /// a clean multi-page split is no longer an error. The diagnostic
    /// now fires only when a <c>MulticolLayouter</c> resume page
    /// emits zero fragments and its continuation doesn't advance past
    /// the entry index — the forward-progress safety fallback for the
    /// single-oversized-child case (analog to TableLayouter's
    /// single-oversized-row fallback). See
    /// <c>docs/deferrals.md#multicol-balancing-pagination</c>.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMulticolForcedOverflow001 =
        "LAYOUT-MULTICOL-FORCED-OVERFLOW-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 1 hardening (Finding 4) — emitted by
    /// the multicol layouter when an arithmetic combination of
    /// <c>column-count</c> + <c>column-gap</c> would produce
    /// non-finite per-column inline-axis geometry (e.g.,
    /// <c>column-gap: 1e300</c> with 100 columns drives
    /// <c>totalGap = (N-1) * columnGap</c> past <c>double.MaxValue</c>
    /// → <c>Infinity</c>). The layouter clamps the bad value to a
    /// sane cap (column-gap is forced to a value that keeps
    /// <c>totalGap &lt; containerInlineSize / 2</c>) so emission can
    /// continue.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutMulticolNonFiniteGeometry001 =
        "LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001";

    /// <summary>
    /// Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — emitted
    /// by the block layouter when a float subtree's nested recursion
    /// returns a non-null <c>LayoutContinuation</c> (indicating a
    /// multicol or table inside the float broke mid-emission). Floats
    /// are out-of-flow per CSS 2.2 §9.5; propagating their
    /// continuation requires float-tracking machinery that's an
    /// existing Phase 3 Task 8 deferral. The layouter discards the
    /// returned continuation (atomic-fallback behavior) + surfaces
    /// this diagnostic so authors see the truncation. Fires at most
    /// once per page.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutFloatBreakInsideNested001 =
        "LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001";

    /// <summary>
    /// Per Phase 3 Task 15 L6 post-PR-#66 review F#4 — emitted by the
    /// flex layouter when the flex container's <c>flex-wrap</c> property
    /// resolves to <c>wrap-reverse</c>. L6 ships <c>wrap</c> in full;
    /// <c>wrap-reverse</c> requires an additional cross-axis line-
    /// stacking reversal transform per CSS Flexbox L1 §6.3 ("same as
    /// wrap but the cross-start and cross-end directions are swapped")
    /// which is L7+ scope. Until then the layouter approximates
    /// <c>wrap-reverse</c> as <c>wrap</c>: items wrap correctly in
    /// main-axis DOM order, but the lines stack in the natural cross-
    /// axis direction rather than the reversed direction the author
    /// requested. Without this diagnostic the wrong rendering would be
    /// silent (the CSS declaration parses successfully but behaves like
    /// <c>flex-wrap: wrap</c>). Fires at most once per <c>AttemptLayout</c>
    /// invocation.
    /// Severity: <see cref="DiagnosticSeverity.Warning"/>.
    /// </summary>
    public const string LayoutFlexWrapReverseApproximated001 =
        "LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001";

    // endregion LAYOUT-*
}
