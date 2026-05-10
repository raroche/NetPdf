// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Text.Bidi;
using NetPdf.Text.Hyphenation;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 10 cycle 1 — the inline-pass facade. Wraps the
/// three-call sequence <see cref="LineBuilder.Itemize"/> →
/// <see cref="LineBuilder.Shape"/> → <see cref="LineBuilder.Wrap"/>
/// into a single integration seam for the block-layouter. Future
/// cycles add per-source-TextRun policy plumbing (white-space,
/// overflow-wrap, word-break, hyphens read from each source's
/// <c>ComputedStyle</c>), UAX #24 script detection, RTL fragment-
/// level reversal, and the <see cref="LineFragment"/>[] →
/// <c>BoxFragment</c> conversion that emits inline-fragment records
/// into the <c>IBlockFragmentSink</c>.
///
/// <para><b>Cycle 1 scope (this revision).</b> A thin static facade
/// — bundles the existing 3-call sequence behind one <c>Layout</c>
/// method that block-layouters and tests can call with one
/// invocation. Cycle 1 takes wrap-time policy as EXPLICIT
/// parameters (same as <see cref="LineBuilder.Wrap"/>); the CSS
/// property pipeline (properties.json → KeywordResolver →
/// ComputedStyle → InlineLayouter argument) lands in cycle 2.</para>
///
/// <para><b>Cycle 1 deferrals (subsequent cycles):</b></para>
/// <list type="bullet">
///   <item>Per-source-TextRun policy — cycle 2. Mixed inline
///   descendants like <c>&lt;span style="white-space:nowrap"&gt;</c>
///   inside <c>white-space:normal</c> text need per-glyph metadata
///   carrying through Wrap. Cycle 2 plumbs a per-source
///   <c>InlineRunPolicy</c> struct alongside the typed
///   <c>ComputedStyle</c> fields once the property pipeline lands.</item>
///   <item>UAX #24 script detection — cycle 3. Detects script per
///   codepoint + adds a script-change boundary in <see cref="LineBuilder.Itemize"/>
///   so multi-script documents shape each script with its
///   appropriate OpenType feature set.</item>
///   <item>RTL fragment-level reversal — cycle 3.
///   <see cref="LineBuilder.Shape"/> (cycle 2 ship) already produces
///   RTL glyph arrays in HarfBuzz visual order; cycle 3 reverses
///   fragment-level slice order for RTL paragraphs so the painter
///   walks slices visually right-to-left.</item>
///   <item>BoxFragment conversion — cycle 4. Converts
///   <see cref="LineFragment"/>[] into per-line <c>BoxFragment</c>
///   records that the block-layouter emits into the
///   <c>IBlockFragmentSink</c> alongside its block fragments.
///   Phase 4 painter consumes the unified fragment list.</item>
///   <item>Bidi-aware glyph painting — cycle 4 (Phase 4 painter
///   integration). The painter's pen-position arithmetic differs
///   for LTR vs RTL glyph runs.</item>
/// </list>
///
/// <para><b>Threading.</b> Stateless; every <c>Layout</c>
/// call is self-contained. No instance fields. The injected
/// <see cref="IShaperResolver"/> is responsible for shaper caching;
/// the <see cref="Hyphenator"/> is process-cached via
/// <see cref="EnUsHyphenation.Default"/> (lazy first-load).</para>
/// </summary>
internal static class InlineLayouter
{
    /// <summary>Per Phase 3 Task 10 cycle 1 + post-cycle-1 review
    /// hardening — run the inline pass. Apply CSS white-space
    /// preprocessing, tokenize source <see cref="TextRun"/>s into
    /// <see cref="ItemizedRun"/>s, shape each one, then wrap into
    /// <see cref="LineFragment"/>s sized to fit
    /// <paramref name="availableInlineSize"/>.
    ///
    /// <para>Equivalent to the call sequence:</para>
    /// <code>
    /// var preprocessed = LineBuilder.PreprocessTextRuns(textRuns, whiteSpace);
    /// var itemized = LineBuilder.Itemize(preprocessed, paragraphDirection, ct);
    /// var shaped = LineBuilder.Shape(preprocessed, itemized, resolver,
    ///                                scriptIso15924, language, ct);
    /// var fragments = LineBuilder.Wrap(preprocessed, shaped, availableInlineSize,
    ///                                  whiteSpace, overflowWrap, wordBreak,
    ///                                  hyphens, hyphenator, ct);
    /// </code>
    ///
    /// <para><b>Post-cycle-1 review hardening (PR #38):</b></para>
    /// <list type="number">
    ///   <item>White-space preprocessing now runs at the facade
    ///   layer via <see cref="LineBuilder.PreprocessTextRuns"/>.
    ///   Multi-run whitespace boundaries (e.g.,
    ///   <c>"Hello "</c> + styled <c>"world"</c>) collapse correctly
    ///   instead of producing <c>"Helloworld"</c> or duplicated
    ///   spaces; CRLF normalizes to LF for Pre/PreWrap/PreLine; etc.
    ///   Without this, callers using the facade got cycle 3a
    ///   AS-IS-input behavior even after sub-cycle 1's preprocessor
    ///   shipped — surprising + spec-violating.</item>
    ///   <item>Removed unsafe Latn/en defaults for
    ///   <paramref name="scriptIso15924"/> + <paramref name="language"/>.
    ///   Both are now required arguments to match
    ///   <see cref="LineBuilder.Shape"/>'s contract — silently
    ///   shaping non-Latin (Arabic / Hebrew / Indic / CJK / Thai)
    ///   text as Latin would produce plausible-but-wrong glyphs that
    ///   pass a Latin-only reviewer's eye test.</item>
    ///   <item>All argument validation runs at method entry BEFORE
    ///   any expensive work (Itemize, Shape, Wrap). Invalid
    ///   <paramref name="availableInlineSize"/> / enum values throw
    ///   immediately instead of after a full bidi+shaping pass.</item>
    ///   <item>Cancellation is observed at method entry + after
    ///   PreprocessTextRuns + after Itemize + after Shape + during
    ///   Wrap. Cycle 1 originally checked only between calls;
    ///   <see cref="LineBuilder.Itemize"/> now also checks during
    ///   its own concat/bidi/run-split passes.</item>
    /// </list>
    /// </summary>
    /// <param name="sourceTextRuns">The inline content's source runs
    /// in document order. Must not be <see langword="null"/>.</param>
    /// <param name="availableInlineSize">The maximum inline-axis
    /// size of a wrapped line, in CSS px. Must be positive +
    /// finite.</param>
    /// <param name="resolver">Resolves a HarfBuzz shaper per
    /// <c>ComputedStyle</c> for the shape pass.</param>
    /// <param name="scriptIso15924">ISO 15924 script tag passed
    /// uniformly to every shaping call. <b>Required</b> — no default.
    /// Cycle 3 will derive per-run from UAX #24.</param>
    /// <param name="language">BCP 47 language tag passed uniformly.
    /// <b>Required</b> — no default.</param>
    /// <param name="paragraphDirection">UAX #9 paragraph-level base
    /// direction (default <see cref="ParagraphDirection.LeftToRight"/>).</param>
    /// <param name="whiteSpace">CSS Text L3 §3 <c>white-space</c>
    /// value applied to the WHOLE inline pass. Drives both the
    /// preprocessing step + the wrap-time <c>Pre</c>/<c>NoWrap</c>
    /// gating. Cycle 2 adds per-run support.</param>
    /// <param name="overflowWrap">CSS Text L3 §5.1 <c>overflow-wrap</c>
    /// value.</param>
    /// <param name="wordBreak">CSS Text L3 §5.2 <c>word-break</c>
    /// value.</param>
    /// <param name="hyphens">CSS Text L3 §6.1 <c>hyphens</c>
    /// value.</param>
    /// <param name="hyphenator">Optional Liang hyphenator. Falls
    /// back to <see cref="EnUsHyphenation.Default"/> when null +
    /// <see cref="Hyphens.Auto"/>.</param>
    /// <param name="cancellationToken">Cooperative cancellation
    /// across preprocessing, itemization, shaping, and wrap.</param>
    /// <returns>One <see cref="LineFragment"/> per wrapped line in
    /// document order. Empty when <paramref name="sourceTextRuns"/>
    /// is empty or contains only empty strings.</returns>
    /// <exception cref="ArgumentNullException">A required argument is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="availableInlineSize"/> non-positive /
    /// non-finite, or any of the enum args has an undefined value.</exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.</exception>
    public static LineFragment[] Layout(
        IReadOnlyList<TextRun> sourceTextRuns,
        double availableInlineSize,
        IShaperResolver resolver,
        string scriptIso15924,
        string language,
        ParagraphDirection paragraphDirection = ParagraphDirection.LeftToRight,
        WhiteSpace whiteSpace = WhiteSpace.Normal,
        OverflowWrap overflowWrap = OverflowWrap.Normal,
        WordBreak wordBreak = WordBreak.Normal,
        Hyphens hyphens = Hyphens.Manual,
        Hyphenator? hyphenator = null,
        CancellationToken cancellationToken = default)
    {
        // Per PR #38 review fix (User #3 + Copilot #2): all argument
        // validation runs at method entry BEFORE Itemize/Shape so
        // invalid inputs don't waste CPU/native shaping.
        ArgumentNullException.ThrowIfNull(sourceTextRuns);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scriptIso15924);
        ArgumentNullException.ThrowIfNull(language);

        if (!double.IsFinite(availableInlineSize) || availableInlineSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableInlineSize),
                availableInlineSize,
                "InlineLayouter.Layout: availableInlineSize must be a positive finite value (CSS px).");
        }
        if (paragraphDirection is not (ParagraphDirection.LeftToRight
            or ParagraphDirection.RightToLeft
            or ParagraphDirection.Auto))
        {
            throw new ArgumentOutOfRangeException(nameof(paragraphDirection),
                paragraphDirection,
                "InlineLayouter.Layout: paragraphDirection must be a defined ParagraphDirection value.");
        }
        if (whiteSpace is not (WhiteSpace.Normal
            or WhiteSpace.Pre
            or WhiteSpace.NoWrap
            or WhiteSpace.PreWrap
            or WhiteSpace.PreLine
            or WhiteSpace.BreakSpaces))
        {
            throw new ArgumentOutOfRangeException(nameof(whiteSpace),
                whiteSpace,
                "InlineLayouter.Layout: whiteSpace must be a defined WhiteSpace value.");
        }
        if (overflowWrap is not (OverflowWrap.Normal or OverflowWrap.Anywhere))
        {
            throw new ArgumentOutOfRangeException(nameof(overflowWrap),
                overflowWrap,
                "InlineLayouter.Layout: overflowWrap must be a defined OverflowWrap value.");
        }
        if (wordBreak is not (WordBreak.Normal or WordBreak.BreakAll or WordBreak.KeepAll))
        {
            throw new ArgumentOutOfRangeException(nameof(wordBreak),
                wordBreak,
                "InlineLayouter.Layout: wordBreak must be a defined WordBreak value.");
        }
        if (hyphens is not (Hyphens.None or Hyphens.Manual or Hyphens.Auto))
        {
            throw new ArgumentOutOfRangeException(nameof(hyphens),
                hyphens,
                "InlineLayouter.Layout: hyphens must be a defined Hyphens value.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 0: white-space preprocessing — collapse / preserve /
        // normalize per CSS Text L3 §4.1. Carries collapse state
        // across TextRun boundaries (per PR #35 review fix —
        // PreprocessTextRuns is the inline-context API). Pre/PreWrap
        // also normalize CRLF→LF.
        var preprocessed = LineBuilder.PreprocessTextRuns(sourceTextRuns, whiteSpace);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1: bidi + style itemization.
        var itemized = LineBuilder.Itemize(preprocessed, paragraphDirection,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: shape each itemized run.
        var shaped = LineBuilder.Shape(
            preprocessed, itemized, resolver,
            scriptIso15924, language, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 3: wrap to fit available inline size.
        var fragments = LineBuilder.Wrap(
            preprocessed, shaped, availableInlineSize,
            whiteSpace, overflowWrap, wordBreak,
            hyphens, hyphenator, cancellationToken);

        return fragments;
    }

    /// <summary>Per Phase 3 Task 10 cycle 3 — convenience overload
    /// that reads the wrap-time policy from a containing-block
    /// <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/> via
    /// <see cref="InlineTextPolicyMaterializer.ReadInlineTextPolicy"/>.
    /// Closes the cycle-1/2 gap where callers had to manually pass
    /// 4 wrap-policy enums; now the integrating block-layouter just
    /// passes the block's own ComputedStyle + the chain auto-
    /// resolves the policy bundle.
    ///
    /// <para><b>UNIFORM-POLICY only — does NOT support per-source-TextRun.</b>
    /// CSS Text L3 §3-§6 properties (white-space, overflow-wrap,
    /// word-break, hyphens) all inherit, so for the common case
    /// where a paragraph + its descendants share the inherited
    /// values from the containing block, this overload is correct.
    /// Mixed-mode descendants (e.g.,
    /// <c>&lt;span style="white-space:nowrap"&gt;</c> inside
    /// <c>white-space:normal</c> text) require per-glyph policy
    /// metadata flowing through Wrap — NOT supported by this
    /// overload. Cycle 3 ships only the containing-block-uniform
    /// path, which covers the 95% case for invoice/report content.</para>
    ///
    /// <para><b>Caller contract.</b> The integrating block-layouter
    /// MUST verify that all source TextRuns inherit white-space /
    /// overflow-wrap / word-break / hyphens from
    /// <paramref name="containingBlockStyle"/>, OR pre-flatten the
    /// box tree so mixed-mode descendants don't reach this seam.
    /// When mixed modes ARE present, cycle 3b's per-glyph metadata
    /// API will be the right call; this overload silently applies
    /// the containing-block policy to the entire pass, producing
    /// wrong output for mixed runs (no exception is thrown — the
    /// failure is silent layout error). A pinned regression test
    /// (<c>InlineLayouterCycle3Tests.Layout_with_mixed_run_styles_silently_uniform_pinned</c>)
    /// documents this limit.</para>
    ///
    /// <para>Hyphenator selection: when
    /// <see cref="Hyphens.Auto"/>, the call falls back to
    /// <see cref="EnUsHyphenation.Default"/> if no explicit
    /// hyphenator is passed. Per-language pattern routing lands
    /// alongside UAX #24 script detection (cycle 4).</para>
    /// </summary>
    /// <param name="sourceTextRuns">The inline content's source runs
    /// in document order.</param>
    /// <param name="availableInlineSize">The maximum inline-axis
    /// size of a wrapped line, in CSS px.</param>
    /// <param name="resolver">Resolves a HarfBuzz shaper per
    /// <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/>.</param>
    /// <param name="containingBlockStyle">The
    /// <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/> of the
    /// containing block (typically the &lt;p&gt;-level box). Cycle 3
    /// reads white-space / overflow-wrap / word-break / hyphens
    /// from this style via the <see cref="InlineTextPolicy"/>
    /// materializer.</param>
    /// <param name="scriptIso15924">ISO 15924 script tag.</param>
    /// <param name="language">BCP 47 language tag.</param>
    /// <param name="paragraphDirection">UAX #9 paragraph-level
    /// direction.</param>
    /// <param name="hyphenator">Optional Liang hyphenator override
    /// for <see cref="Hyphens.Auto"/>.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>One <see cref="LineFragment"/> per wrapped line in
    /// document order.</returns>
    public static LineFragment[] Layout(
        IReadOnlyList<TextRun> sourceTextRuns,
        double availableInlineSize,
        IShaperResolver resolver,
        NetPdf.Css.ComputedValues.ComputedStyle containingBlockStyle,
        string scriptIso15924,
        string language,
        ParagraphDirection paragraphDirection = ParagraphDirection.LeftToRight,
        Hyphenator? hyphenator = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingBlockStyle);

        var policy = containingBlockStyle.ReadInlineTextPolicy();
        return Layout(
            sourceTextRuns,
            availableInlineSize,
            resolver,
            scriptIso15924,
            language,
            paragraphDirection,
            policy.WhiteSpace,
            policy.OverflowWrap,
            policy.WordBreak,
            policy.Hyphens,
            hyphenator,
            cancellationToken);
    }

    /// <summary>Per Phase 3 Task 10 cycle 3b — per-source-TextRun
    /// policy overload. Reads <see cref="InlineTextPolicy"/> from
    /// EACH source-TextRun's Style + verifies they all match.
    /// When all runs share the same policy (the most common case
    /// because CSS Text L3 §3-§6 properties all inherit), delegates
    /// to the cycle-3a uniform path. When mixed-mode descendants
    /// produce DIFFERENT per-run policies, throws
    /// <see cref="NotSupportedException"/> with a descriptive
    /// message — the failure mode is loud + deterministic instead
    /// of silently using the wrong policy.
    ///
    /// <para><b>Cycle 3c — narrow Normal/NoWrap mixed-mode support
    /// (post-PR-#42 review hardening).</b> When the only inter-run
    /// difference is <c>white-space</c> AND every non-empty run's
    /// <c>white-space</c> ∈ {<see cref="WhiteSpace.Normal"/>,
    /// <see cref="WhiteSpace.NoWrap"/>}, the call delegates to
    /// <see cref="LineBuilder.Wrap"/> with a per-source-run
    /// <c>WhiteSpace</c> array. Per CSS Text L3 §4.1, Normal + NoWrap
    /// share the SAME whitespace-collapse semantics — only the
    /// wrappability differs — so the preprocessor runs once with
    /// <see cref="WhiteSpace.Normal"/> and the wrap loop's per-glyph
    /// gating downgrades Allowed→Prohibited inside NoWrap runs. Any
    /// other WhiteSpace mismatch (Pre/PreWrap/PreLine/BreakSpaces
    /// mixed with Normal/NoWrap) requires true per-run preprocessing
    /// — collapse vs. preserve cannot be reconciled by post-shape
    /// metadata alone — and STILL throws
    /// <see cref="NotSupportedException"/> until cycle 3d lands a
    /// per-source-run preprocessor.</para>
    ///
    /// <para><b>Why fail loud outside the Normal/NoWrap matrix?</b>
    /// Cycle 3b shipped the per-run READING but NOT the per-glyph
    /// metadata flowing through Wrap. Cycle 3c adds the per-run
    /// WhiteSpace array but ONLY for the Normal/NoWrap matrix where
    /// preprocessing is uniform. Mixing Pre/PreWrap (preserve) with
    /// Normal/NoWrap (collapse) would require splitting the
    /// preprocessor into per-source-run passes that respect each
    /// run's collapse mode — out of scope for cycle 3c. Per-glyph
    /// overflow-wrap/word-break/hyphens mixed-mode also still throws
    /// (deferred to subsequent cycles).</para>
    ///
    /// <para><b>Equality check.</b> Two policies are "the same" by
    /// the auto-generated <see cref="InlineTextPolicy"/> record-
    /// struct equality (white-space + overflow-wrap + word-break +
    /// hyphens all equal). Identity isn't required.</para>
    /// </summary>
    /// <param name="sourceTextRuns">Source runs in document order.
    /// Each run's Style is read for its individual policy.</param>
    /// <param name="availableInlineSize">Available inline-axis size
    /// in CSS px.</param>
    /// <param name="resolver">Shaper resolver.</param>
    /// <param name="scriptIso15924">ISO 15924 script tag.</param>
    /// <param name="language">BCP 47 language tag.</param>
    /// <param name="paragraphDirection">Paragraph base direction.</param>
    /// <param name="hyphenator">Optional Liang hyphenator override.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>One <see cref="LineFragment"/> per wrapped line.</returns>
    /// <exception cref="ArgumentNullException">Required arg is null.</exception>
    /// <exception cref="NotSupportedException">Source TextRuns have
    /// non-uniform <see cref="InlineTextPolicy"/> values where the
    /// difference falls OUTSIDE the cycle 3c Normal/NoWrap-only
    /// WhiteSpace matrix (mismatch in overflow-wrap/word-break/
    /// hyphens, or WhiteSpace mismatch involving Pre/PreWrap/PreLine/
    /// BreakSpaces).</exception>
    public static LineFragment[] LayoutPerRun(
        IReadOnlyList<TextRun> sourceTextRuns,
        double availableInlineSize,
        IShaperResolver resolver,
        string scriptIso15924,
        string language,
        ParagraphDirection paragraphDirection = ParagraphDirection.LeftToRight,
        Hyphenator? hyphenator = null,
        CancellationToken cancellationToken = default)
    {
        // Per Phase 3 Task 10 cycle 3b review (User #2 + Copilot #1)
        // — front-load all argument validation BEFORE the per-run
        // policy scan so invalid args throw their proper exceptions
        // instead of being masked by the policy scan or the
        // mixed-mode NotSupportedException.
        ArgumentNullException.ThrowIfNull(sourceTextRuns);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scriptIso15924);
        ArgumentNullException.ThrowIfNull(language);

        if (!double.IsFinite(availableInlineSize) || availableInlineSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableInlineSize),
                availableInlineSize,
                "InlineLayouter.LayoutPerRun: availableInlineSize must be a positive finite value (CSS px).");
        }
        if (paragraphDirection is not (ParagraphDirection.LeftToRight
            or ParagraphDirection.RightToLeft
            or ParagraphDirection.Auto))
        {
            throw new ArgumentOutOfRangeException(nameof(paragraphDirection),
                paragraphDirection,
                "InlineLayouter.LayoutPerRun: paragraphDirection must be a defined ParagraphDirection value.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (sourceTextRuns.Count == 0)
        {
            // No runs → no policy contention. Use defaults.
            return Layout(
                sourceTextRuns, availableInlineSize, resolver,
                scriptIso15924, language, paragraphDirection,
                hyphenator: hyphenator,
                cancellationToken: cancellationToken);
        }

        // Per Phase 3 Task 10 cycle 3b review (User #3) — empty
        // TextRuns contribute no glyphs and no wrap decisions. Skip
        // them when picking + comparing the effective policy so an
        // empty `<span>` with a different style doesn't falsely
        // trigger the mixed-mode guard. If ALL runs are empty,
        // delegate to the empty-input path with default policy.
        //
        // Per Phase 3 Task 10 cycle 3c — when policies differ ONLY
        // in WhiteSpace AND every non-empty run's WhiteSpace is in
        // the Normal/NoWrap subset (which share collapse semantics
        // per CSS Text L3 §4.1), build a per-source-run WhiteSpace
        // array + delegate to the per-glyph WhiteSpace honoring
        // path. Mixed-mode involving Pre/PreWrap/PreLine/BreakSpaces
        // still throws (collapse-vs-preserve cannot be reconciled
        // post-shape; per-source-run preprocessing is deferred to
        // cycle 3d). Mixed-mode in any of overflow-wrap/word-break/
        // hyphens also still throws (per-glyph metadata for those
        // 3 properties is out of cycle 3c scope).
        InlineTextPolicy? effectivePolicy = null;
        var firstNonEmptyIndex = -1;
        WhiteSpace[]? whiteSpacePerRun = null; // built lazily on mismatch
        for (var i = 0; i < sourceTextRuns.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceTextRuns[i].Text.Length == 0) continue;
            var p = sourceTextRuns[i].Style.ReadInlineTextPolicy();
            if (effectivePolicy is null)
            {
                effectivePolicy = p;
                firstNonEmptyIndex = i;
            }
            else if (p != effectivePolicy)
            {
                // Cycle 3c — accept mismatch when ONLY WhiteSpace
                // differs. The other 3 properties must still match.
                var sameOtherFields = p.OverflowWrap == effectivePolicy.Value.OverflowWrap
                    && p.WordBreak == effectivePolicy.Value.WordBreak
                    && p.Hyphens == effectivePolicy.Value.Hyphens;
                if (!sameOtherFields)
                {
                    throw new NotSupportedException(
                        $"InlineLayouter.LayoutPerRun: source TextRuns have " +
                        $"different InlineTextPolicy values that differ in " +
                        $"more than just WhiteSpace (run {firstNonEmptyIndex}={effectivePolicy}, " +
                        $"run {i}={p}). Per-glyph overflow-wrap/word-break/" +
                        $"hyphens mixed-mode is deferred to a future cycle. " +
                        $"Cycle 3c handles WhiteSpace mismatches; until others " +
                        $"land, callers must either avoid mixed inline " +
                        $"descendants of those properties or split the wrap " +
                        $"into homogeneous sub-passes.");
                }
                // Cycle 3c hardening (post-PR-#42 review Recs #1+#3)
                // — narrow the WhiteSpace mismatch matrix to
                // {Normal, NoWrap}-only. Per CSS Text L3 §4.1 these
                // two values share collapse semantics (both collapse
                // runs of whitespace to a single SP, both normalize
                // CR/LF to SP); they only differ in wrappability,
                // which the per-glyph downgrade in
                // <see cref="LineBuilder.Wrap"/> handles. Any other
                // WhiteSpace combination — Pre/PreWrap/PreLine/
                // BreakSpaces mixed with collapse modes — needs
                // per-source-run preprocessing because preserve-vs-
                // collapse decisions happen BEFORE shaping. Refuse
                // those until cycle 3d ships the per-run
                // preprocessor.
                var prevWs = effectivePolicy.Value.WhiteSpace;
                var currWs = p.WhiteSpace;
                if (!IsCollapseModeMatrixMember(prevWs)
                    || !IsCollapseModeMatrixMember(currWs))
                {
                    throw new NotSupportedException(
                        $"InlineLayouter.LayoutPerRun: WhiteSpace " +
                        $"mismatch outside the cycle 3c Normal/NoWrap " +
                        $"matrix (run {firstNonEmptyIndex}={prevWs}, " +
                        $"run {i}={currWs}). Mixed Pre/PreWrap/PreLine/" +
                        $"BreakSpaces with Normal/NoWrap requires true " +
                        $"per-source-run whitespace preprocessing " +
                        $"(collapse-vs-preserve cannot be reconciled " +
                        $"post-shape) and is deferred to cycle 3d. " +
                        $"Until then, callers must either avoid the " +
                        $"mismatched mode-mix or split the wrap into " +
                        $"homogeneous sub-passes.");
                }
                // WhiteSpace-only mismatch within Normal/NoWrap
                // matrix — build per-run array lazily on first
                // mismatch.
                if (whiteSpacePerRun is null)
                {
                    whiteSpacePerRun = new WhiteSpace[sourceTextRuns.Count];
                    var fillTo = prevWs;
                    for (var j = 0; j < sourceTextRuns.Count; j++)
                    {
                        // Empty runs get the effective ws — they
                        // contribute no glyphs anyway. Non-empty
                        // already-walked runs get fillTo (the first
                        // non-empty's ws). The current run + later
                        // runs will be set as we walk.
                        whiteSpacePerRun[j] = fillTo;
                    }
                }
                whiteSpacePerRun[i] = currWs;
            }
            else if (whiteSpacePerRun is not null)
            {
                // Same policy as effective + we're already in per-
                // run mode — set this run's ws too.
                whiteSpacePerRun[i] = p.WhiteSpace;
            }
        }

        // All runs empty (or all share policy) — use the chosen
        // policy or default if no non-empty run was seen.
        var policy = effectivePolicy ?? InlineTextPolicy.Default;

        // Per cycle 3c review Rec #4 + Copilot #1 — preprocess once,
        // pass the SAME instance to ShapeForLayout AND
        // <see cref="LineBuilder.Wrap"/>. The previous implementation
        // called PreprocessTextRuns twice with identical args, which
        // wasted CPU + risked non-determinism if the preprocessor
        // ever became non-pure.
        //
        // When per-run WhiteSpace varies (whiteSpacePerRun != null),
        // preprocess with <see cref="WhiteSpace.Normal"/>: the matrix
        // is now narrowed to {Normal, NoWrap} (validated above) and
        // both collapse identically per CSS Text L3 §4.1. The wrap
        // loop's per-glyph gating then suppresses wraps inside the
        // NoWrap-tagged runs. When the policy is uniform,
        // preprocess with the chosen WhiteSpace as cycle 3a/3b did.
        //
        // For the wrap-time `whiteSpace` argument: pass
        // <see cref="WhiteSpace.Normal"/> when per-run mode is
        // active so the global `wrapsAtAllowed` gate inside
        // <see cref="LineBuilder.Wrap"/> doesn't suppress wraps for
        // the Normal-tagged runs (the per-glyph downgrade still
        // handles NoWrap). When uniform, pass policy.WhiteSpace.
        var preprocessWhiteSpace = whiteSpacePerRun is null
            ? policy.WhiteSpace
            : WhiteSpace.Normal;
        var preprocessed = LineBuilder.PreprocessTextRuns(
            sourceTextRuns, preprocessWhiteSpace);
        var wrapWhiteSpace = whiteSpacePerRun is null
            ? policy.WhiteSpace
            : WhiteSpace.Normal;

        return LineBuilder.Wrap(
            preprocessed,
            ShapeForLayout(
                preprocessed,
                resolver, scriptIso15924, language, paragraphDirection,
                cancellationToken),
            availableInlineSize,
            wrapWhiteSpace,
            policy.OverflowWrap,
            policy.WordBreak,
            policy.Hyphens,
            hyphenator,
            cancellationToken,
            whiteSpacePerRun);
    }

    /// <summary>Per Phase 3 Task 10 cycle 3c hardening (post-PR-#42
    /// review Rec #1+#3) — predicate identifying members of the
    /// Normal/NoWrap collapse-mode matrix accepted for mixed-mode
    /// per-source-run WhiteSpace plumbing in
    /// <see cref="LayoutPerRun"/>. Per CSS Text L3 §4.1 these two
    /// values share whitespace-collapse semantics (collapse runs of
    /// whitespace to a single SP, normalize CR/LF to SP); only their
    /// wrappability differs (Normal wraps at Allowed, NoWrap
    /// suppresses wraps). Any other WhiteSpace value
    /// (Pre/PreWrap/PreLine/BreakSpaces) preserves whitespace and
    /// requires distinct preprocessing — those mixes throw until
    /// cycle 3d ships per-source-run preprocessing.</summary>
    private static bool IsCollapseModeMatrixMember(WhiteSpace ws) =>
        ws is WhiteSpace.Normal or WhiteSpace.NoWrap;

    /// <summary>Per Phase 3 Task 10 cycle 3c — itemize + shape
    /// helper used by <see cref="LayoutPerRun"/> when delegating
    /// directly to <see cref="LineBuilder.Wrap"/> with a per-run
    /// WhiteSpace array (bypasses the convenience
    /// <see cref="Layout(IReadOnlyList{TextRun}, double, IShaperResolver, string, string, ParagraphDirection, WhiteSpace, OverflowWrap, WordBreak, Hyphens, Hyphenator?, CancellationToken)"/>
    /// path which doesn't take the per-run array).</summary>
    private static IReadOnlyList<ShapedRun> ShapeForLayout(
        IReadOnlyList<TextRun> textRuns,
        IShaperResolver resolver,
        string scriptIso15924,
        string language,
        ParagraphDirection paragraphDirection,
        CancellationToken cancellationToken)
    {
        var itemized = LineBuilder.Itemize(textRuns, paragraphDirection, cancellationToken);
        return LineBuilder.Shape(textRuns, itemized, resolver,
            scriptIso15924, language, cancellationToken);
    }
}
