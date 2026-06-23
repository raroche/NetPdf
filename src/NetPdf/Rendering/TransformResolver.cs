// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;

namespace NetPdf.Rendering;

/// <summary>Phase 4 transforms (PR #210 review [P1]) — resolve each fragment's EFFECTIVE transform:
/// the composition of every ancestor-or-self CSS <c>transform</c>, as one PDF <c>cm</c>. A CSS
/// transform on an ancestor transforms its whole subtree, so a child paragraph inside a transformed
/// <c>&lt;div&gt;</c> must paint under the div's matrix too. Each painter (decoration / image / text)
/// looks up its fragment's box in the returned map and wraps its ops in that single matrix.</summary>
internal static class TransformResolver
{
    private static readonly IReadOnlyDictionary<Box, (double, double, double, double, double, double)> Empty
        = new Dictionary<Box, (double, double, double, double, double, double)>();

    /// <summary>Build box → effective PDF <c>cm</c> for every box (on this page's fragments) that has
    /// an ancestor-or-self transform. Each ancestor's matrix is derived from ITS painted border box,
    /// so the composition is exact for ancestors painted on the same page (a transformed ancestor
    /// split onto another page is skipped — a documented edge). Returns an empty map when nothing is
    /// transformed (every painter then emits no wrap — byte-identical).</summary>
    public static IReadOnlyDictionary<Box, (double A, double B, double C, double D, double E, double F)>
        BuildEffectiveTransforms(
            IReadOnlyList<BoxFragment> fragments,
            IReadOnlyDictionary<Box, ImageResourceCache.BoxTransform> transformBoxes,
            double contentOriginLeftPx, double contentOriginTopPx, double pageHeightPt)
    {
        if (transformBoxes.Count == 0) return Empty;

        // box → border-box geometry (page-top CSS px). First fragment wins for a paginated box.
        var geom = new Dictionary<Box, (double L, double T, double W, double H)>();
        foreach (var f in fragments)
            if (f.Box is { } box)
                geom.TryAdd(box, (contentOriginLeftPx + f.InlineOffset, contentOriginTopPx + f.BlockOffset,
                    f.InlineSize, f.BlockSize));

        var result = new Dictionary<Box, (double, double, double, double, double, double)>();
        foreach (var box in geom.Keys)
        {
            (double, double, double, double, double, double)? eff = null;
            // Walk self → ancestors; each transformed box's matrix wraps (is the OUTER of) the
            // accumulation so far, so eff = cm_root · … · cm_parent · cm_self.
            for (Box? b = box; b is not null; b = b.Parent)
            {
                if (!transformBoxes.TryGetValue(b, out var bt) || bt.Transform.IsIdentity) continue;
                if (!geom.TryGetValue(b, out var g)) continue; // ancestor not painted on this page
                var cm = CssTransform_Parser.ToPdfMatrix(bt.Transform, bt.Origin, g.L, g.T, g.W, g.H, pageHeightPt);
                eff = eff is { } inner ? Compose(cm, inner) : cm;
            }
            if (eff is { } e) result[box] = e;
        }
        return result;
    }

    /// <summary>Compose two PDF <c>cm</c> affines: <paramref name="outer"/> · <paramref name="inner"/>
    /// (apply <paramref name="inner"/> first, then <paramref name="outer"/>) — the [a c e; b d f]
    /// matrix product, the same affine convention CSS and PDF share.</summary>
    private static (double, double, double, double, double, double) Compose(
        (double A, double B, double C, double D, double E, double F) outer,
        (double A, double B, double C, double D, double E, double F) inner) =>
        (outer.A * inner.A + outer.C * inner.B,
         outer.B * inner.A + outer.D * inner.B,
         outer.A * inner.C + outer.C * inner.D,
         outer.B * inner.C + outer.D * inner.D,
         outer.A * inner.E + outer.C * inner.F + outer.E,
         outer.B * inner.E + outer.D * inner.F + outer.F);
}
