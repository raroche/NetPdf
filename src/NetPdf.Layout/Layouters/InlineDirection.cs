// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Text.Bidi;

namespace NetPdf.Layout.Layouters;

/// <summary>The resolved inline base direction of a box (CSS Writing Modes 4 §2.1
/// <c>direction</c>). LTR / RTL only — the <c>direction</c> property admits no
/// <c>auto</c> (unlike the bidi paragraph level, which can be auto-detected).</summary>
internal enum InlineDirection
{
    /// <summary><c>direction: ltr</c> — keyword id 0 (the initial value).</summary>
    Ltr,

    /// <summary><c>direction: rtl</c> — keyword id 1.</summary>
    Rtl,
}

/// <summary>
/// The shared direction / writing-mode resolution pipeline — the ONE seam that maps a
/// box's computed <c>direction</c> (+ writing-mode, fixed at horizontal-tb today) onto
/// the values layout consumes: the bidi paragraph base direction and the
/// inline-start ↔ left/right mapping that <c>text-align</c> (and, when picked up,
/// floats / <c>clear</c> / <c>caption-side</c>) need.
///
/// <para><b>Why one seam.</b> Several LTR-only approximations across the layouters
/// (<see cref="ComputedStyleLayoutExtensions.ReadInlineAlignFactor"/>'s start/end swap,
/// <see cref="ComputedStyleLayoutExtensions.ReadFloatSide"/> /
/// <see cref="ComputedStyleLayoutExtensions.ReadCaptionSide"/> inline-start/end
/// resolution) cite "resolve against writing-mode + direction" as their pickup trigger.
/// This is that resolution. Writing mode is horizontal-tb only for now (the property is
/// not yet registered), so the inline axis is horizontal and <c>direction</c> alone
/// decides whether the inline-start edge is left or right; when vertical writing modes
/// land they compose HERE, leaving the call sites unchanged.</para>
/// </summary>
internal static class DirectionStyleExtensions
{
    /// <summary>The box's computed inline base direction (CSS Writing Modes 4 §2.1).
    /// <c>ltr</c>(0, the initial) / <c>rtl</c>(1) — the
    /// <c>KeywordResolver</c> id contract.</summary>
    public static InlineDirection ReadDirection(this ComputedStyle s) =>
        s.ReadKeywordOrDefault(PropertyId.Direction, defaultIndex: 0) == 1
            ? InlineDirection.Rtl
            : InlineDirection.Ltr;

    /// <summary>True when the box's inline base direction is RTL.</summary>
    public static bool IsRtl(this ComputedStyle s) =>
        s.ReadDirection() == InlineDirection.Rtl;

    /// <summary>Map the computed <c>direction</c> onto the bidi paragraph base
    /// direction. An explicit <c>direction</c> pre-empts the UAX #9 P2/P3 first-
    /// strong-character heuristic: <c>rtl</c> forces paragraph level 1,
    /// <c>ltr</c> forces level 0. Consumed at the inline-layout seam
    /// (<c>BlockLayouter</c> → <c>InlineLayouter.LayoutPerRun</c>) so an RTL block
    /// lays its paragraph out right-to-left.</summary>
    public static ParagraphDirection ReadParagraphDirection(this ComputedStyle s) =>
        s.IsRtl() ? ParagraphDirection.RightToLeft : ParagraphDirection.LeftToRight;
}
