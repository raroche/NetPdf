// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Shared text <c>vertical-align</c> geometry (CSS 2.2 §10.8.1) — the SINGLE source of truth for an
/// inline TEXT run's baseline RAISE, used by BOTH the layout line-box sizing (which grows the line to
/// contain the shift) and <c>TextPainter</c> (which applies the shift to the glyph baseline). Keeping
/// them on one helper means the line the layout reserves and the baseline the painter draws on can
/// never disagree.
/// </summary>
internal static class InlineVerticalAlign
{
    /// <summary>vertical-align <c>sub</c> / <c>super</c> raise / drop, as a fraction of the run
    /// font-size (CSS 2.2 §10.8.1 leaves the amount to the UA — matches the inline-atomic shift).</summary>
    private const double SuperRiseEm = 0.3;
    private const double SubDropEm = 0.2;

    /// <summary>A text run's baseline RAISE in px (positive = up, off the line baseline). <c>super</c> =
    /// +0.3em, <c>sub</c> = −0.2em, a <c>&lt;length&gt;</c> = the length, a <c>&lt;percentage&gt;</c> =
    /// that fraction of the run's OWN line-height (§10.8.1, not the line box). <c>baseline</c> / plain
    /// text / <c>top</c> / <c>bottom</c> / <c>middle</c> / <c>text-*</c> (the latter deferred for text)
    /// are 0.
    /// <para><b>Inline-level gate.</b> Returns 0 when <paramref name="runStyle"/> IS
    /// <paramref name="blockStyle"/> — that run is BLOCK-DIRECT text (its vertical-align is the block's /
    /// table cell's, which doesn't apply to inline content per §10.8.1). A nested
    /// <c>&lt;sub&gt;</c>/<c>&lt;sup&gt;</c>/<c>&lt;span&gt;</c> run carries the inline element's OWN
    /// (distinct) style, so it shifts. vertical-align is non-inherited, so the common cases are
    /// correct.</para></summary>
    public static double TextRaisePx(ComputedStyle runStyle, ComputedStyle blockStyle, double fontSizePx)
    {
        if (ReferenceEquals(runStyle, blockStyle)) return 0.0;
        var slot = runStyle.Get(PropertyId.VerticalAlign);
        return slot.Tag switch
        {
            ComputedSlotTag.LengthPx => slot.AsLengthPx(),
            ComputedSlotTag.Percentage => slot.AsPercentage() / 100.0 * OwnLineHeightPx(runStyle, fontSizePx),
            ComputedSlotTag.Keyword => slot.AsKeyword() switch
            {
                2 => SuperRiseEm * fontSizePx,    // super — raised
                1 => -SubDropEm * fontSizePx,     // sub — lowered
                _ => 0.0,                         // baseline + top/bottom/middle/text-* (deferred for text)
            },
            _ => 0.0,
        };
    }

    /// <summary>The run's baseline-top-px for a LINE-EDGE-relative text <c>vertical-align</c>
    /// (<c>top</c> / <c>bottom</c> / <c>middle</c> / <c>text-top</c> / <c>text-bottom</c>, CSS 2.2
    /// §10.8.1), or <see langword="null"/> for a baseline-relative one (<c>baseline</c> / <c>sub</c> /
    /// <c>super</c> / a <c>&lt;length&gt;</c> / <c>&lt;percentage&gt;</c>) — the caller positions those at
    /// <paramref name="lineBaselineTopPx"/> − <see cref="TextRaisePx"/>. The line-edge keywords align the
    /// run to the LINE BOX top / bottom (<c>top</c> / <c>bottom</c>), the line MIDDLE (<c>middle</c> —
    /// the run's midpoint at the line baseline + half the parent x-height), or the PARENT text
    /// content-area (<c>text-top</c> / <c>text-bottom</c> — the run's top / bottom at the parent font's
    /// ascent / descent above / below the line baseline).
    /// <para><b>Bounded first cut.</b> The run is positioned WITHIN the line box AS SIZED by its baseline
    /// content; a run TALLER than that line OVERFLOWS it (line growth for non-baseline-aligned content is
    /// deferred). Parent metrics use the 0.8 / 0.2-em ascent / descent + 0.5-em x-height approximation
    /// (the same the layout uses). <paramref name="descentPx"/> is NEGATIVE (the font descender), so the
    /// run spans [<paramref name="lineBaselineTopPx"/> result − ascent, − descent].</para>
    /// <para><b>Inline-level gate.</b> Block-direct text (<paramref name="runStyle"/> IS
    /// <paramref name="blockStyle"/>) returns <see langword="null"/> — a block's / cell's own
    /// vertical-align doesn't apply to its inline content (§10.8.1), same as <see cref="TextRaisePx"/>.</para></summary>
    public static double? TextLineEdgeBaselineTopPx(
        ComputedStyle runStyle, ComputedStyle blockStyle,
        double lineTopPx, double lineHeightPx, double lineBaselineTopPx,
        double ascentPx, double descentPx, double parentFontSizePx)
    {
        if (ReferenceEquals(runStyle, blockStyle)) return null;   // block-direct text — inline-level gate
        var slot = runStyle.Get(PropertyId.VerticalAlign);
        if (slot.Tag != ComputedSlotTag.Keyword) return null;     // baseline / length / % → raise path
        // Parent (strut) text content-area metrics for text-top / text-bottom / middle.
        var parentAscentPx = 0.8 * parentFontSizePx;
        var parentDescentPx = -0.2 * parentFontSizePx;            // negative
        var parentHalfXHeightPx = 0.25 * parentFontSizePx;        // half the ≈0.5em x-height
        return slot.AsKeyword() switch
        {
            6 => lineTopPx + ascentPx,                                       // top: run top at line-box top
            7 => lineTopPx + lineHeightPx + descentPx,                       // bottom: run bottom at line-box bottom
            3 => lineBaselineTopPx - parentAscentPx + ascentPx,              // text-top: run top at parent ascent
            4 => lineBaselineTopPx - parentDescentPx + descentPx,            // text-bottom: run bottom at parent descent
            5 => lineBaselineTopPx - parentHalfXHeightPx + (ascentPx + descentPx) / 2.0, // middle
            _ => null,                                                       // baseline / sub / super → raise path
        };
    }

    /// <summary>A run's OWN computed line-height (px) — a declared length, else
    /// <paramref name="fontSizePx"/> × 1.2 (the normal-line-height factor). The base for a text
    /// vertical-align <c>%</c> (CSS 2.2 §10.8.1).</summary>
    public static double OwnLineHeightPx(ComputedStyle runStyle, double fontSizePx)
    {
        var declared = runStyle.ReadLengthPxOrZero(PropertyId.LineHeight);
        return declared > 0 ? declared : fontSizePx * 1.2;
    }
}
