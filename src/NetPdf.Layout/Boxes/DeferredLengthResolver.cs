// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Body context-dependent cycle — resolves the box tree's DEFERRED font-/viewport-relative
/// lengths IN PLACE once the page box is final. The cascade can't fold them
/// (<c>LengthResolver</c> defers a <c>2em</c>/<c>5vw</c> raw, and — body context-dependent
/// cycle — a math function whose only context dependence is font-/viewport-relative, e.g.
/// <c>calc(2em + 10px)</c>), and the layouters only read <see cref="ComputedSlotTag.LengthPx"/>
/// slots, so without this pass those values silently behaved as <c>auto</c>/0. The pipeline
/// calls this AFTER the <c>@page size</c> override fixes the page box and BEFORE layout.
/// </summary>
/// <remarks>
/// <para><b>Bases.</b> <c>em</c>/<c>ex</c>/<c>ch</c> scale by the OWNING box's resolved
/// <c>font-size</c> (<c>BoxBuilder</c>'s deferred-font pass has already run, so the slot is
/// used px); <c>rem</c> by the ROOT element box's; <c>vw</c>/<c>vh</c>/<c>vmin</c>/<c>vmax</c>
/// by the PAGE box — the same bases the margin-box painter feeds
/// <see cref="RelativeLengthResolver"/>. An <see cref="BoxKind.AnonymousBlock"/> SHARES its
/// parent's style object, so re-visiting it is a no-op (the slot is already rewritten).</para>
/// <para><b>Range.</b> Each property's §10.5 range follows
/// <see cref="NonNegativeProperties.IsRequired"/>: a margin/offset resolves a negative value
/// (<c>margin-left: -2em</c>), a width/padding rejects it on the unit path (stays deferred,
/// like the cascade's negative-literal reject) and clamps it on the calc path.</para>
/// <para><b>Still deferred</b> (left untouched, the pre-pass behavior): PERCENTAGE terms (the
/// containing-block base is layout-time — deferrals.md), border widths (the LineWidth resolver
/// never defers a relative raw), <c>lh</c>/<c>cap</c>/<c>ic</c>-style typographic units
/// (<see cref="RelativeLengthResolver"/> doesn't support them), and container-relative units.</para>
/// </remarks>
internal static class DeferredLengthResolver
{
    /// <summary>The body box-model lengths the layouters consume via
    /// <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/> — the slots this pass
    /// rewrites. Border widths are absent by design (their resolver never defers).
    ///
    /// <para><b>multicol-balancing-pagination</b> — <c>column-width</c> joins the set so a
    /// font-relative <c>column-width: 12em</c> / <c>20rem</c> resolves to a used px BEFORE the
    /// multicol dispatch decision (<c>BlockLayouter.IsMulticolContainer</c> routes on
    /// <see cref="ComputedStyleLayoutExtensions.ReadColumnWidth"/>, which only sees a
    /// <see cref="ComputedSlotTag.LengthPx"/> slot). Pre-fix the cascade DEFERRED a font-relative
    /// <c>column-width</c> (the slot stayed <see cref="ComputedSlotTag.Unset"/>), so the container
    /// fell through to ordinary block flow — CSS Multi-column L1 §3.1's own introductory example
    /// (<c>column-width: 12em</c>) never columnized. It is a multicol intrinsic length read via
    /// <c>ReadColumnWidth</c>, not a box-model length; the sibling <c>column-gap</c> is resolved
    /// in <c>ReadColumnGap</c> instead, because its <c>normal</c> initial is itself font-relative
    /// (1em — a keyword, not a deferred raw) AND the property is shared with the flex/grid gutter
    /// where <c>normal</c> computes to 0 (so a global rewrite here would be wrong for it).</para></summary>
    private static readonly PropertyId[] BodyLengthProperties =
    [
        PropertyId.Width, PropertyId.Height,
        PropertyId.MarginTop, PropertyId.MarginRight, PropertyId.MarginBottom, PropertyId.MarginLeft,
        PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft,
        PropertyId.Top, PropertyId.Right, PropertyId.Bottom, PropertyId.Left,
        PropertyId.LineHeight,
        PropertyId.ColumnWidth,
    ];

    /// <summary>Resolve every deferred font-/viewport-relative length in <paramref name="root"/>'s
    /// subtree in place. <paramref name="pageWidthPx"/>/<paramref name="pageHeightPx"/> are the
    /// RESOLVED page box (after any <c>@page size</c> override) in CSS px.</summary>
    public static void ResolveTreeInPlace(Box root, double pageWidthPx, double pageHeightPx)
    {
        ArgumentNullException.ThrowIfNull(root);
        var rootEmPx = RootElementEmPx(root);
        Visit(root, rootEmPx, pageWidthPx, pageHeightPx);
    }

    private static void Visit(Box box, double rootEmPx, double pageWidthPx, double pageHeightPx)
    {
        ResolveStyleInPlace(box.Style, rootEmPx, pageWidthPx, pageHeightPx);
        foreach (var child in box.Children)
            Visit(child, rootEmPx, pageWidthPx, pageHeightPx);
    }

    private static void ResolveStyleInPlace(
        ComputedStyle style, double rootEmPx, double pageWidthPx, double pageHeightPx)
    {
        if (style is null) return;
        var emPx = style.ReadLengthPxOrZero(PropertyId.FontSize);
        if (emPx <= 0) emPx = 16.0;

        foreach (var id in BodyLengthProperties)
        {
            if (!style.IsDeferred(id)
                || !style.TryGetDeferred(id, out var raw)
                || string.IsNullOrEmpty(raw))
            {
                continue;
            }

            var nonNegative = NonNegativeProperties.IsRequired(id);
            double px;
            if (CalcLengthEvaluator.IsMathFunction(raw))
            {
                // NaN percent base — a % term that somehow reached this far still poisons the
                // result and the raw stays deferred (the cascade classifier should have rejected
                // it; defense in depth, not a new path).
                var ctx = new CalcLengthEvaluator.CalcContext(
                    double.NaN, emPx, rootEmPx, pageWidthPx, pageHeightPx);
                if (!CalcLengthEvaluator.TryEvaluate(raw, ctx, clampNonNegative: nonNegative, out px))
                    continue;
            }
            else if (!RelativeLengthResolver.TryResolve(
                         raw, emPx, rootEmPx, pageWidthPx, pageHeightPx,
                         allowNegative: !nonNegative, out px))
            {
                continue;
            }

            style.Set(id, ComputedSlot.FromLengthPx(px));
        }
    }

    /// <summary>The <c>rem</c> base — the ROOT ELEMENT box's resolved font-size (the first
    /// element-backed box under the synthetic <see cref="BoxKind.Root"/>), falling back to the
    /// UA-default 16px when the tree has no element box yet.</summary>
    private static double RootElementEmPx(Box root)
    {
        var rootElement = root.SourceElement is not null ? root : root.FirstChild;
        var em = rootElement?.Style?.ReadLengthPxOrZero(PropertyId.FontSize) ?? 0;
        return em > 0 ? em : 16.0;
    }
}
