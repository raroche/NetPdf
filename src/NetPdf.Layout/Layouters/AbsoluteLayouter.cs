// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 19 — pure placement math for <c>position: absolute</c>
/// boxes per CSS 2.1 §10.3.7 (inline-axis width / left / right) +
/// §10.6.4 (block-axis height / top / bottom). Side-effect-free (no
/// sink, no fragmentainer) so the constraint solving is unit-testable
/// in isolation; <c>BlockLayouter</c> owns the containing-block
/// derivation + fragment emission + inner-content dispatch.
///
/// <para><b>Cycle 2b — the full §6 constraint solver</b>: each axis
/// resolves the (inset-start, size, inset-end, margin-start, margin-end)
/// system against the containing-block extent, honoring
/// <c>auto</c> + percentage values, the over-constrained "ignore the
/// end inset" rule (LTR / top-to-bottom), and auto-margin centering.
/// Border + padding are folded into the box-model chrome.</para>
///
/// <para><b>Two documented approximations</b> (need machinery beyond
/// this pure solver):</para>
/// <list type="bullet">
///   <item><b>Shrink-to-fit / content height</b> — when a size is
///   <c>auto</c> and NOT pinned by both insets, the spec uses
///   shrink-to-fit (inline) / content height (block), which need
///   intrinsic-size measurement. Cycle 2b approximates as the
///   AVAILABLE extent (CB minus the resolved inset(s) + margins +
///   chrome). For the common pinned-both-insets case the result is
///   EXACT (= fill).</item>
///   <item><b>Static position</b> — when both insets on an axis are
///   <c>auto</c>, the spec uses the box's normal-flow static position.
///   Cycle 2b approximates as the CB content origin (offset 0), which
///   is exact when the box would have been the first in-flow child.</item>
/// </list>
/// <para>See <c>docs/deferrals.md#abspos-cycle-1-explicit-only</c>.</para>
/// </summary>
internal static class AbsoluteLayouter
{
    /// <summary>Resolve a <c>position: absolute</c> box's border-box
    /// rectangle in the SAME coordinate space as
    /// <paramref name="cb"/> (= the caller's sink coordinates). Returns
    /// <see cref="AbsolutePlacement.Unresolved"/> only for the
    /// genuinely-invalid case (negative resolved size).</summary>
    public static AbsolutePlacement ResolvePlacement(
        Box box, AbsoluteContainingBlock cb)
    {
        var style = box.Style;

        // Inline axis (LTR, horizontal writing mode): left = inset-start,
        // right = inset-end, width = size. Percentages resolve against
        // the CB inline extent; padding percentages too (CSS 2.1 §8.4).
        var (inlineOffset, inlineSize) = SolveAxis(
            insetStart: ReadAutoOrPx(style, PropertyId.Left, cb.InlineSize),
            insetEnd: ReadAutoOrPx(style, PropertyId.Right, cb.InlineSize),
            size: ReadAutoOrPx(style, PropertyId.Width, cb.InlineSize),
            marginStart: ReadMarginAutoOrPx(style, PropertyId.MarginLeft, cb.InlineSize),
            marginEnd: ReadMarginAutoOrPx(style, PropertyId.MarginRight, cb.InlineSize),
            borderStart: style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth),
            paddingStart: ReadPxOrPct(style, PropertyId.PaddingLeft, cb.InlineSize),
            paddingEnd: ReadPxOrPct(style, PropertyId.PaddingRight, cb.InlineSize),
            borderEnd: style.ReadLengthPxOrZero(PropertyId.BorderRightWidth),
            cbExtent: cb.InlineSize);

        // Block axis: top = inset-start, bottom = inset-end, height =
        // size. Per CSS 2.1 percentage top/bottom/height resolve against
        // the CB BLOCK extent; percentage padding (block axis) resolves
        // against the CB INLINE extent per §8.4 — but that's a rarely-
        // used corner; cycle 2b resolves block-axis padding percentages
        // against the inline extent to match §8.4.
        var (blockOffset, blockSize) = SolveAxis(
            insetStart: ReadAutoOrPx(style, PropertyId.Top, cb.BlockSize),
            insetEnd: ReadAutoOrPx(style, PropertyId.Bottom, cb.BlockSize),
            size: ReadAutoOrPx(style, PropertyId.Height, cb.BlockSize),
            marginStart: ReadMarginAutoOrPx(style, PropertyId.MarginTop, cb.InlineSize),
            marginEnd: ReadMarginAutoOrPx(style, PropertyId.MarginBottom, cb.InlineSize),
            borderStart: style.ReadLengthPxOrZero(PropertyId.BorderTopWidth),
            paddingStart: ReadPxOrPct(style, PropertyId.PaddingTop, cb.InlineSize),
            paddingEnd: ReadPxOrPct(style, PropertyId.PaddingBottom, cb.InlineSize),
            borderEnd: style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth),
            cbExtent: cb.BlockSize);

        // SolveAxis already clamps a negative resolved content size to
        // 0 (CSS clamps used width/height to >= 0), so inlineSize /
        // blockSize are non-negative here — the box always resolves.
        // AbsolutePlacement.Unresolved is retained for the API contract
        // (EmitOneAbsoluteBox's IsResolved check) but the §6 solver no
        // longer produces it; the BlockLayouter's null-CB path (relative-
        // offset ancestor / unrecorded ancestor) is the drop site.
        return AbsolutePlacement.Resolved(
            cb.InlineOrigin + inlineOffset,
            cb.BlockOrigin + blockOffset,
            inlineSize,
            blockSize);
    }

    /// <summary>Solve one axis of the §10.3.7 / §10.6.4 system. All
    /// inputs are post-resolution: a non-null value is a used length in
    /// px; <see langword="null"/> means <c>auto</c>. Returns the box's
    /// BORDER-BOX start offset from the CB content origin + the
    /// border-box size (= chrome + content size).</summary>
    private static (double offset, double borderBoxSize) SolveAxis(
        double? insetStart, double? insetEnd, double? size,
        double? marginStart, double? marginEnd,
        double borderStart, double paddingStart, double paddingEnd, double borderEnd,
        double cbExtent)
    {
        var chrome = borderStart + paddingStart + paddingEnd + borderEnd;
        var startAuto = insetStart is null;
        var endAuto = insetEnd is null;
        var sizeAuto = size is null;

        // ---- Over-constrained case: inset-start, size, inset-end all
        // given. The end inset is "ignored" (LTR / ttb), BUT auto
        // margins first absorb the slack (centering / one-sided).
        if (!startAuto && !sizeAuto && !endAuto)
        {
            var slack = cbExtent - insetStart!.Value - insetEnd!.Value
                - chrome - size!.Value;
            double mS, mE;
            if (marginStart is null && marginEnd is null)
            {
                // Both auto → center (split slack; may be negative).
                mS = slack / 2.0;
                mE = slack - mS;
            }
            else if (marginStart is null)
            {
                mS = slack - marginEnd!.Value;
                mE = marginEnd.Value;
            }
            else if (marginEnd is null)
            {
                mS = marginStart.Value;
                mE = slack - marginStart.Value;
            }
            else
            {
                // Fully specified → over-constrained; ignore the END
                // inset (LTR). Margins stay as authored.
                mS = marginStart.Value;
                mE = marginEnd.Value;
            }
            return (insetStart.Value + mS, chrome + size.Value);
        }

        // ---- Not over-constrained: auto margins resolve to 0.
        var marS = marginStart ?? 0;
        var marE = marginEnd ?? 0;

        // Resolve (usedStart, usedSize) per the auto pattern. The end
        // inset, when present, anchors; when auto, it's the remainder
        // (not needed for the border-box offset, which keys off start).
        double usedStart;
        double usedSize;

        if (sizeAuto)
        {
            // Size auto → shrink-to-fit (inline) / content height
            // (block), approximated as the AVAILABLE extent. EXACT when
            // both insets pin it (fill).
            if (!startAuto && !endAuto)
            {
                // Fill: both insets given → size = remaining space.
                usedStart = insetStart!.Value;
                usedSize = cbExtent - insetStart.Value - insetEnd!.Value
                    - marS - marE - chrome;
            }
            else if (!startAuto)
            {
                // start given, end auto → available from start to CB end.
                usedStart = insetStart!.Value;
                usedSize = cbExtent - insetStart.Value - marS - marE - chrome;
            }
            else if (!endAuto)
            {
                // end given, start auto → fill from CB start to end inset,
                // start anchored at CB origin (static-position = 0).
                usedSize = cbExtent - insetEnd!.Value - marS - marE - chrome;
                usedStart = StaticPosition;
            }
            else
            {
                // all auto → start = static position; size = available.
                usedStart = StaticPosition;
                usedSize = cbExtent - StaticPosition - marS - marE - chrome;
            }
        }
        else
        {
            usedSize = size!.Value;
            if (!startAuto)
            {
                // start given (size given, end auto OR end given handled
                // above) → anchor at start.
                usedStart = insetStart!.Value;
            }
            else if (!endAuto)
            {
                // start auto, size + end given → start = remainder.
                usedStart = cbExtent - insetEnd!.Value - marE - chrome
                    - usedSize - marS;
            }
            else
            {
                // start + end auto, size given → start = static position.
                usedStart = StaticPosition;
            }
        }

        if (usedSize < 0) usedSize = 0;  // clamp; caller treats <0 as drop via the border-box check
        return (usedStart + marS, chrome + usedSize);
    }

    /// <summary>The static-position approximation per the class doc: the
    /// CB content origin (offset 0). Exact when the box would have been
    /// the first in-flow child of its containing block.</summary>
    private const double StaticPosition = 0.0;

    /// <summary>Read an inset / size / margin property as a used px
    /// length, OR <see langword="null"/> for <c>auto</c> (the Unset /
    /// Keyword slot). Percentages resolve against
    /// <paramref name="pctBase"/>.</summary>
    private static double? ReadAutoOrPx(ComputedStyle style, PropertyId id, double pctBase)
    {
        var slot = style.Get(id);
        return slot.Tag switch
        {
            ComputedSlotTag.LengthPx => slot.AsLengthPx(),
            ComputedSlotTag.Percentage => slot.AsPercentage() / 100.0 * pctBase,
            // Unset / Keyword (auto) / anything else → auto.
            _ => null,
        };
    }

    /// <summary>Read a margin property as a used px length, OR
    /// <see langword="null"/> for an EXPLICIT <c>margin: auto</c> (a
    /// Keyword slot). Differs from <see cref="ReadAutoOrPx"/> for the
    /// UNSET case: a margin's CSS initial value is <c>0</c> (NOT
    /// <c>auto</c>), so an unset margin resolves to 0 — only an explicit
    /// <c>auto</c> participates in the §10.3.7 auto-margin (centering)
    /// resolution.</summary>
    private static double? ReadMarginAutoOrPx(ComputedStyle style, PropertyId id, double pctBase)
    {
        var slot = style.Get(id);
        return slot.Tag switch
        {
            ComputedSlotTag.LengthPx => slot.AsLengthPx(),
            ComputedSlotTag.Percentage => slot.AsPercentage() / 100.0 * pctBase,
            ComputedSlotTag.Keyword => null,  // explicit `auto`
            _ => 0.0,                          // unset → initial 0
        };
    }

    /// <summary>Read a padding property as a used px length (px OR
    /// percentage-of-<paramref name="pctBase"/>); <c>auto</c> isn't
    /// valid for padding so the fallback is 0.</summary>
    private static double ReadPxOrPct(ComputedStyle style, PropertyId id, double pctBase)
    {
        var slot = style.Get(id);
        return slot.Tag switch
        {
            ComputedSlotTag.LengthPx => slot.AsLengthPx(),
            ComputedSlotTag.Percentage => slot.AsPercentage() / 100.0 * pctBase,
            _ => 0.0,
        };
    }
}

/// <summary>Per Phase 3 Task 19 — the containing block for an
/// absolutely-positioned box, in the caller's sink coordinate space.
/// Origin + size are the nearest positioned ancestor's PADDING box
/// (cycle 2a), or the initial containing block (= fragmentainer content
/// area) when there's no positioned ancestor.</summary>
internal readonly record struct AbsoluteContainingBlock(
    double InlineOrigin,
    double BlockOrigin,
    double InlineSize,
    double BlockSize);

/// <summary>Per Phase 3 Task 19 cycle 1 — the resolved border-box
/// rectangle for an absolutely-positioned box, OR an unresolved marker
/// carrying the defer reason.</summary>
internal readonly record struct AbsolutePlacement(
    bool IsResolved,
    double InlineOffset,
    double BlockOffset,
    double InlineSize,
    double BlockSize,
    string? DeferReason)
{
    public static AbsolutePlacement Resolved(
        double inlineOffset, double blockOffset, double inlineSize, double blockSize)
        => new(true, inlineOffset, blockOffset, inlineSize, blockSize, null);

    public static AbsolutePlacement Unresolved(string reason)
        => new(false, 0, 0, 0, 0, reason);
}
