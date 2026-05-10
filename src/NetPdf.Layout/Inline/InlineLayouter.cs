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
/// into a single integration seam for the block-layouter.
///
/// <para><b>Cycle 1 scope (this revision).</b> A thin static facade
/// — bundles the existing 3-call sequence behind one <c>Layout</c>
/// method that block-layouters and tests can call with one
/// invocation. Cycle 1 takes wrap-time policy as EXPLICIT
/// parameters (same as <see cref="LineBuilder.Wrap"/>); the CSS
/// property pipeline (properties.json → KeywordResolver →
/// ComputedStyle → InlineLayouter argument) lands in cycle 2.</para>
///
/// <para><b>Shipped capabilities (cycle 1 → 3d sub-cycle 2):</b></para>
/// <list type="bullet">
///   <item>Cycle 2 — CSS property pipeline for overflow-wrap /
///   word-break / hyphens read from ComputedStyle via
///   <see cref="InlineTextPolicy"/>.</item>
///   <item>Cycle 3 — Layout overload reading uniform-policy
///   <see cref="InlineTextPolicy"/> from a containing-block
///   <c>ComputedStyle</c>.</item>
///   <item>Cycle 3b — <see cref="LayoutPerRun"/> reads each source-
///   TextRun's policy + throws <see cref="System.NotSupportedException"/>
///   on mismatch.</item>
///   <item>Cycle 3c — per-source-run WhiteSpace plumbing through
///   <see cref="LineBuilder.Wrap"/>'s per-glyph metadata for the
///   Normal/NoWrap subset.</item>
///   <item>Cycle 3d sub-cycle 1 — per-source-run preprocessor
///   (<see cref="LineBuilder.PreprocessTextRunsPerRun"/>) broadens
///   WhiteSpace matrix to all 6 values (collapse + preserve modes
///   mixed).</item>
///   <item>Cycle 3d sub-cycle 2 — per-source-run OverflowWrap
///   plumbing via a single <see cref="InlineTextPolicy"/>[] array
///   parameter on <see cref="LineBuilder.Wrap"/>. Anywhere
///   forced-break fallback gated per-glyph by source-run
///   WhiteSpace + OverflowWrap + grapheme-cluster boundary (UAX
///   #29).</item>
///   <item>Cycle 3d sub-cycle 3 — per-source-run WordBreak.BreakAll
///   plumbing through the flat-build phase's BreakAll upgrade
///   pass. KeepAll on mismatch still throws (CJK semantics need
///   UAX #24 script detection). Cross-run BreakAll boundary uses
///   "either side may opt in" rule per sub-cycle 3 review
///   Finding #3.</item>
///   <item>Cycle 3d sub-cycle 4 — per-source-run Hyphens via the
///   hyphenation pipeline. Soft-hyphen demotion (Hyphens=None)
///   applied per concat position; Liang application gated
///   per-word by the source-run's Hyphens (apply iff Auto).
///   Position→source-run-index map built lazily when
///   <c>inlineTextPolicyPerRun</c> is supplied.</item>
/// </list>
///
/// <para><b>Per-run mismatch acceptance matrix as of sub-cycle 4:</b></para>
/// <list type="bullet">
///   <item><b>WhiteSpace</b> — all 6 values mixable
///   (sub-cycle 1).</item>
///   <item><b>OverflowWrap</b> — Normal + Anywhere mixable
///   (sub-cycle 2).</item>
///   <item><b>WordBreak</b> — Normal + BreakAll mixable
///   (sub-cycle 3). KeepAll on mismatch THROWS (CJK semantics
///   deferred).</item>
///   <item><b>Hyphens</b> — all 3 values mixable
///   (sub-cycle 4).</item>
/// </list>
///
/// <para><b>Subsequent-cycle deferrals:</b></para>
/// <list type="bullet">
///   <item>KeepAll CJK inter-character break suppression — needs
///   UAX #24 script detection + UAX #14 LB30b handling. Uniform
///   KeepAll currently behaves like Normal (documented
///   approximation); KeepAll on mismatch throws.</item>
///   <item>UAX #24 script detection. Detects script per
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
    /// produce DIFFERENT per-run policies, the dispatch depends on
    /// which properties differ.
    ///
    /// <para><b>Cycle 3c + 3d WhiteSpace mixed-mode support.</b>
    /// When the inter-run difference is in <c>white-space</c> only,
    /// the call delegates to <see cref="LineBuilder.Wrap"/> with a
    /// per-source-run <c>WhiteSpace</c> array. Cycle 3c handled the
    /// Normal/NoWrap subset (uniform collapse, per-glyph wrap
    /// suppression); cycle 3d sub-cycle 1 broadens to the FULL
    /// six-value matrix by replacing the uniform preprocessor with
    /// <see cref="LineBuilder.PreprocessTextRunsPerRun"/> — each
    /// source run is preprocessed with its OWN WhiteSpace mode so
    /// preserve-modes (Pre/PreWrap/BreakSpaces) keep their content
    /// while collapse-modes (Normal/NoWrap/PreLine) continue to
    /// strip/collapse via their carried <c>inWs</c> state.</para>
    ///
    /// <para><b>Still-loud failure modes.</b> Per-glyph
    /// overflow-wrap / word-break / hyphens mixed-mode is NOT yet
    /// supported — mismatch in any of those three properties throws
    /// <see cref="NotSupportedException"/> (deferred to a subsequent
    /// cycle).</para>
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
    /// non-uniform <see cref="InlineTextPolicy"/> values where
    /// overflow-wrap / word-break / hyphens differ (per-glyph
    /// metadata for those 3 deferred to a subsequent cycle).</exception>
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
        // Per Phase 3 Task 10 cycle 3d sub-cycle 2 review Rec #4 —
        // single per-source-run InlineTextPolicy array. Replaces
        // the cycle 3c whiteSpacePerRun + cycle 3d sub-cycle 2
        // overflowWrapPerRun parallel arrays. One coherent array
        // both simplifies the wrap loop's lookups + lets future
        // sub-cycles (word-break, hyphens) plumb through without
        // adding more nullable parameters.
        //
        // Active dimensions in <see cref="LineBuilder.Wrap"/>:
        //   * WhiteSpace (sub-cycle 1) — collapse vs. preserve;
        //     per-glyph IsBreakSpace + wrap-at-Allowed downgrade.
        //   * OverflowWrap (sub-cycle 2) — per-glyph anywhere
        //     fallback gating.
        // WordBreak + Hyphens fields are populated but NOT yet
        // honored per-run (sub-cycle 3+). Mixed-mode in those still
        // throws here.
        InlineTextPolicy? effectivePolicy = null;
        var firstNonEmptyIndex = -1;
        InlineTextPolicy[]? perRunPolicy = null; // built lazily on first mismatch
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
                // Cycle 3d sub-cycle 4 — Hyphens mismatch is now
                // handled via per-source-run plumbing through
                // <see cref="LineBuilder.Wrap"/>'s hyphenation
                // pipeline. The remaining hard-throw is KeepAll
                // mismatch (per sub-cycle 3 review Finding #1,
                // KeepAll's CJK semantics need UAX #24).
                if ((p.WordBreak == WordBreak.KeepAll
                        || effectivePolicy.Value.WordBreak == WordBreak.KeepAll)
                    && p.WordBreak != effectivePolicy.Value.WordBreak)
                {
                    throw new NotSupportedException(
                        $"InlineLayouter.LayoutPerRun: source TextRuns have " +
                        $"a word-break:keep-all vs. {(p.WordBreak == WordBreak.KeepAll ? effectivePolicy.Value.WordBreak : p.WordBreak)} mismatch " +
                        $"(run {firstNonEmptyIndex}={effectivePolicy}, run {i}={p}). " +
                        $"KeepAll's CJK inter-character break suppression " +
                        $"(CSS Text L3 §5.2) requires UAX #24 script detection " +
                        $"+ CJK-aware UAX #14 LB30b handling — not yet " +
                        $"implemented. Per sub-cycle 3 review Finding #1 we " +
                        $"throw on the mixed case so KeepAll behavior isn't " +
                        $"silently lost. Uniform-KeepAll behaves like Normal " +
                        $"(documented approximation).");
                }

                // Lazily build the per-run policy array on first
                // mismatch. Fill all previously-walked entries with
                // the effective (first non-empty's) policy so
                // anything that referenced an empty run gets a
                // sensible default + later runs get set as we walk.
                if (perRunPolicy is null)
                {
                    perRunPolicy = new InlineTextPolicy[sourceTextRuns.Count];
                    var fillTo = effectivePolicy.Value;
                    for (var j = 0; j < sourceTextRuns.Count; j++)
                    {
                        perRunPolicy[j] = fillTo;
                    }
                }
                perRunPolicy[i] = p;
            }
            else if (perRunPolicy is not null)
            {
                // Same policy as effective + we're already in per-
                // run mode — set this run's policy too.
                perRunPolicy[i] = p;
            }
        }

        // All runs empty (or all share policy) — use the chosen
        // policy or default if no non-empty run was seen.
        var policy = effectivePolicy ?? InlineTextPolicy.Default;

        // Per cycle 3c review Rec #4 + Copilot #1 — preprocess once,
        // pass the SAME instance to ShapeForLayout AND
        // <see cref="LineBuilder.Wrap"/>. Cycle 3d sub-cycle 1
        // replaces the uniform PreprocessTextRuns call with
        // <see cref="LineBuilder.PreprocessTextRunsPerRun"/> when
        // per-run WhiteSpace varies — each run is processed with its
        // OWN mode (preserve modes keep content, collapse modes chain
        // <c>inWs</c> state across boundaries).
        //
        // For the wrap-time `whiteSpace` argument: pass
        // <see cref="WhiteSpace.Normal"/> when per-run mode is
        // active so the global `wrapsAtAllowed` gate inside
        // <see cref="LineBuilder.Wrap"/> doesn't suppress wraps for
        // the Normal-tagged runs (the per-glyph downgrade still
        // handles NoWrap/Pre suppression). When uniform, pass
        // policy.WhiteSpace.
        IReadOnlyList<TextRun> preprocessed;
        if (perRunPolicy is null)
        {
            preprocessed = LineBuilder.PreprocessTextRuns(
                sourceTextRuns, policy.WhiteSpace);
        }
        else
        {
            // Per Phase 3 Task 10 cycle 3d sub-cycle 1 review Rec #4
            // — pass cancellation through to the per-run preprocessor
            // so large hostile inline text doesn't waste CPU after a
            // late cancellation signal. Extract the WhiteSpace array
            // from the per-run policy (the preprocessor only needs
            // collapse-vs-preserve decisions per run).
            var perRunWs = new WhiteSpace[perRunPolicy.Length];
            for (var i = 0; i < perRunPolicy.Length; i++)
            {
                perRunWs[i] = perRunPolicy[i].WhiteSpace;
            }
            preprocessed = LineBuilder.PreprocessTextRunsPerRun(
                sourceTextRuns, perRunWs, cancellationToken);
        }
        // Per cycle 3d sub-cycle 1 review Rec #4 — observe
        // cancellation immediately after preprocessing, before the
        // potentially-expensive ShapeForLayout call. The token may
        // have been signaled DURING preprocessing (a long input
        // span between the per-run boundary checks); this catches
        // late cancellations before HarfBuzz fires up.
        cancellationToken.ThrowIfCancellationRequested();

        var wrapWhiteSpace = perRunPolicy is null
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
            perRunPolicy);
    }

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
