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

    /// <summary>A run's OWN computed line-height (px) — a declared length, else
    /// <paramref name="fontSizePx"/> × 1.2 (the normal-line-height factor). The base for a text
    /// vertical-align <c>%</c> (CSS 2.2 §10.8.1).</summary>
    public static double OwnLineHeightPx(ComputedStyle runStyle, double fontSizePx)
    {
        var declared = runStyle.ReadLengthPxOrZero(PropertyId.LineHeight);
        return declared > 0 ? declared : fontSizePx * 1.2;
    }
}
