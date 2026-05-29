// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 19 cycle 1 — pure placement math for
/// <c>position: absolute</c> boxes per CSS Positioned Layout L3 §6.
/// Kept side-effect-free (no sink, no fragmentainer) so the offset/size
/// resolution is unit-testable in isolation; <c>BlockLayouter</c> owns
/// the containing-block derivation + fragment emission + inner-content
/// dispatch.
///
/// <para><b>Cycle 1 scope</b>: only EXPLICIT pixel <c>top</c> +
/// <c>left</c> + <c>width</c> + <c>height</c>, resolved against the
/// containing block's content-box origin. Any deferred feature
/// (<c>auto</c> offset, percentage, <c>right</c>/<c>bottom</c>
/// anchoring, <c>auto</c> width/height) returns
/// <see cref="AbsolutePlacement.Unresolved"/> with a human-readable
/// reason so the caller can drop the box + surface
/// <c>LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001</c> instead of
/// mis-placing it. The full §6 resolution (static position, over-
/// constrained left/right/width, percentage bases, shrink-to-fit) is
/// cycle 2+. See <c>docs/deferrals.md#abspos-cycle-1-explicit-only</c>.</para>
/// </summary>
internal static class AbsoluteLayouter
{
    /// <summary>Resolve an <c>position: absolute</c> box's border-box
    /// rectangle in the SAME coordinate space as
    /// <paramref name="containingBlock"/> (= the caller's sink
    /// coordinates). Returns <see cref="AbsolutePlacement.Unresolved"/>
    /// when the box uses a cycle-1-deferred feature.</summary>
    public static AbsolutePlacement ResolvePlacement(
        Box box, AbsoluteContainingBlock containingBlock)
    {
        var style = box.Style;

        // Cycle 1 requires explicit pixel top + left. `auto` (the
        // default) lands as an Unset/Keyword slot; percentages land as
        // a Percentage slot. Both defer.
        if (!TryReadExplicitPx(style, PropertyId.Top, out var top))
        {
            return AbsolutePlacement.Unresolved(
                "`top` is auto/percentage/unset — cycle 1 needs an explicit "
                + "pixel value (static-position + percentage resolution is cycle 2)");
        }
        if (!TryReadExplicitPx(style, PropertyId.Left, out var left))
        {
            return AbsolutePlacement.Unresolved(
                "`left` is auto/percentage/unset — cycle 1 needs an explicit "
                + "pixel value (static-position + percentage resolution is cycle 2)");
        }
        if (!TryReadExplicitPx(style, PropertyId.Width, out var width))
        {
            return AbsolutePlacement.Unresolved(
                "`width` is auto/percentage/unset — cycle 1 needs an explicit "
                + "pixel value (shrink-to-fit + offset-derived width is cycle 2)");
        }
        if (!TryReadExplicitPx(style, PropertyId.Height, out var height))
        {
            return AbsolutePlacement.Unresolved(
                "`height` is auto/percentage/unset — cycle 1 needs an explicit "
                + "pixel value (content-derived + offset-derived height is cycle 2)");
        }

        // Negative width/height are invalid per CSS Sizing; treat as a
        // deferred/dropped case rather than emitting a negative-extent
        // fragment.
        if (width < 0 || height < 0)
        {
            return AbsolutePlacement.Unresolved(
                "negative `width`/`height` is invalid; box dropped");
        }

        // Per §6 the offsets are measured from the containing block's
        // padding edge (= its content-box origin in cycle 1's content-
        // box CB approximation). LTR + horizontal writing mode: left
        // anchors the inline-start edge, top the block-start edge.
        var inlineOffset = containingBlock.InlineOrigin + left;
        var blockOffset = containingBlock.BlockOrigin + top;
        return AbsolutePlacement.Resolved(inlineOffset, blockOffset, width, height);
    }

    /// <summary>Read a <see cref="PropertyId"/> as an explicit CSS-px
    /// length. Returns <see langword="false"/> for the
    /// <c>auto</c>/unset path (Keyword/Unset slot) AND for percentages
    /// (Percentage slot) — both are cycle-2 deferrals.</summary>
    private static bool TryReadExplicitPx(ComputedStyle style, PropertyId id, out double px)
    {
        var slot = style.Get(id);
        if (slot.Tag == ComputedSlotTag.LengthPx)
        {
            px = slot.AsLengthPx();
            return true;
        }
        px = 0;
        return false;
    }
}

/// <summary>Per Phase 3 Task 19 cycle 1 — the containing block for an
/// absolutely-positioned box, in the caller's sink coordinate space.
/// Cycle 1 derives this as the establishing <c>BlockLayouter</c>'s
/// content area (= the fragmentainer content box for the top-level
/// case, which coincides with the initial containing block + with a
/// positioned root's content box). The spec-correct nearest-positioned-
/// ancestor PADDING box + the ancestor walk is cycle 2.</summary>
internal readonly record struct AbsoluteContainingBlock(
    double InlineOrigin,
    double BlockOrigin,
    double InlineSize,
    double BlockSize);

/// <summary>Per Phase 3 Task 19 cycle 1 — the resolved border-box
/// rectangle for an absolutely-positioned box, OR an unresolved marker
/// carrying the cycle-1 defer reason.</summary>
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
