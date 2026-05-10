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
/// — bundles the existing 3-call sequence behind one <see cref="Layout"/>
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
/// <para><b>Threading.</b> Stateless; every <see cref="Layout"/>
/// call is self-contained. No instance fields. The injected
/// <see cref="IShaperResolver"/> is responsible for shaper caching;
/// the <see cref="Hyphenator"/> is process-cached via
/// <see cref="EnUsHyphenation.Default"/> (lazy first-load).</para>
/// </summary>
internal static class InlineLayouter
{
    /// <summary>Per Phase 3 Task 10 cycle 1 — run the inline pass.
    /// Tokenize source <see cref="TextRun"/>s into
    /// <see cref="ItemizedRun"/>s, shape each one, then wrap into
    /// <see cref="LineFragment"/>s sized to fit
    /// <paramref name="availableInlineSize"/>.
    ///
    /// <para>Equivalent to the 3-call sequence:</para>
    /// <code>
    /// var itemized = LineBuilder.Itemize(textRuns, paragraphDirection);
    /// var shaped = LineBuilder.Shape(textRuns, itemized, resolver,
    ///                                scriptIso15924, language, ct);
    /// var fragments = LineBuilder.Wrap(textRuns, shaped, availableInlineSize,
    ///                                  whiteSpace, overflowWrap, wordBreak,
    ///                                  hyphens, hyphenator, ct);
    /// </code>
    /// </summary>
    /// <param name="sourceTextRuns">The inline content's source runs in
    /// document order.</param>
    /// <param name="availableInlineSize">The maximum inline-axis
    /// size of a wrapped line, in CSS px. Must be positive +
    /// finite.</param>
    /// <param name="resolver">Resolves a HarfBuzz shaper per
    /// <c>ComputedStyle</c> for the shape pass.</param>
    /// <param name="paragraphDirection">UAX #9 paragraph-level base
    /// direction (default <see cref="ParagraphDirection.LeftToRight"/>).</param>
    /// <param name="scriptIso15924">ISO 15924 script tag passed
    /// uniformly to every shaping call. Cycle 3 will derive per-run
    /// from UAX #24.</param>
    /// <param name="language">BCP 47 language tag passed uniformly.</param>
    /// <param name="whiteSpace">CSS Text L3 §3 <c>white-space</c>
    /// value applied to the WHOLE inline pass. Cycle 2 adds per-run
    /// support.</param>
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
    /// across all three sub-passes.</param>
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
        ParagraphDirection paragraphDirection = ParagraphDirection.LeftToRight,
        string scriptIso15924 = "Latn",
        string language = "en",
        WhiteSpace whiteSpace = WhiteSpace.Normal,
        OverflowWrap overflowWrap = OverflowWrap.Normal,
        WordBreak wordBreak = WordBreak.Normal,
        Hyphens hyphens = Hyphens.Manual,
        Hyphenator? hyphenator = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceTextRuns);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scriptIso15924);
        ArgumentNullException.ThrowIfNull(language);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 1: bidi + style itemization.
        var itemized = LineBuilder.Itemize(sourceTextRuns, paragraphDirection);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: shape each itemized run.
        var shaped = LineBuilder.Shape(
            sourceTextRuns, itemized, resolver,
            scriptIso15924, language, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 3: wrap to fit available inline size.
        var fragments = LineBuilder.Wrap(
            sourceTextRuns, shaped, availableInlineSize,
            whiteSpace, overflowWrap, wordBreak,
            hyphens, hyphenator, cancellationToken);

        return fragments;
    }
}
