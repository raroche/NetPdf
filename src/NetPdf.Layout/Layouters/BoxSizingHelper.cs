// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;

namespace NetPdf.Layout.Layouters;

/// <summary>Box-sizing cross-cutting audit (CSS Basic UI 4 §10) — the ONE shared
/// mapping from a DECLARED size to the used BORDER-box size, honoring
/// <c>box-sizing</c>. Consolidates the previously per-layouter copies
/// (<c>BlockLayouter</c> + <c>GridSizing</c>'s <c>IsBorderBoxSizing</c> /
/// <c>DeclaredWidthToBorderBox</c>) the <c>grid-box-sizing-border-box-deferred</c>
/// entry called out, and extends the support to the block + table declared-size
/// readers.
///
/// <para>The grid intrinsic-contribution side keeps its own <c>ItemBorderBoxExtent</c>
/// (it folds in content-measure + min floors); this helper is the simple
/// declared-size mapping shared by the block / flex-cross / table readers.</para></summary>
internal static class BoxSizingHelper
{
    /// <summary>Whether the box uses <c>box-sizing: border-box</c> (keyword index 1)
    /// vs the initial <c>content-box</c> (0).</summary>
    public static bool IsBorderBox(ComputedStyle style) =>
        style.ReadKeywordOrDefault(PropertyId.BoxSizing, defaultIndex: 0) == 1;

    /// <summary>Map a DECLARED size (CSS px; <c>auto</c> / percentage read as 0 by
    /// the caller's reader) to the used BORDER-box size given the box's chrome
    /// (border + padding on that axis): <c>border-box</c> → the declared size IS the
    /// border box, FLOORED at the chrome (the content area bottoms out at 0, per the
    /// PR #155 margin-box rule); <c>content-box</c> (the initial) → declared + chrome.
    /// For <c>content-box</c> this is byte-identical to <c>declared + chrome</c>
    /// (both non-negative), so existing callers stay unchanged.</summary>
    public static double DeclaredToBorderBox(ComputedStyle style, double declaredPx, double chromePx) =>
        IsBorderBox(style)
            ? Math.Max(declaredPx, chromePx)
            : Math.Max(0, declaredPx + chromePx);
}
