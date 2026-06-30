// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>SVG <c>clip-path</c> (§14.3) + <c>mask</c> (§14.4) element-reference support for the raster
/// renderer, plus the <c>objectBoundingBox</c> reference-box computation they share. Extracted from
/// <see cref="SvgRasterizer"/> (PR-244 review — keep feature areas as small internal collaborators).</summary>
internal static class SvgClipMask
{
    /// <summary>Apply a <c>clip-path="url(#id)"</c> reference: clip the canvas to the union of the referenced
    /// <c>&lt;clipPath&gt;</c>'s child geometry. <c>clipPathUnits</c> is honored — <c>userSpaceOnUse</c> (the
    /// default) treats the geometry as user-space coordinates; <c>objectBoundingBox</c> maps the unit square
    /// onto the clipped element's geometry bounding box. A non-<c>url(#…)</c> value, a missing /
    /// non-<c>&lt;clipPath&gt;</c> target, or empty geometry is flagged unsupported (and leaves the element
    /// unclipped rather than wrongly clipping it all away).</summary>
    public static void ApplyClipPath(SKCanvas canvas, XElement el, SvgStyle style, SvgRenderState state)
    {
        var raw = SvgAttr.Presentation(el, "clip-path");
        if (raw is null) return;
        raw = raw.Trim();
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        if (SvgRasterizer.PaintServerId(raw) is not { } id || !state.Ids.TryGetValue(id, out var clip)
            || !clip.Name.LocalName.Equals("clipPath", StringComparison.OrdinalIgnoreCase))
        {
            state.SawUnsupported = true; // url() to a non-clipPath / geometry-box / unresolved → not applied
            return;
        }
        var obb = (SvgRasterizer.Attr(clip, "clipPathUnits") ?? "userSpaceOnUse")
            .Trim().Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase);
        var bbox = obb ? ComputeBBox(el, style, state, depth: 0, SKMatrix.Identity, isRoot: true) : (SKRect?)null;
        if (obb && bbox is not { Width: > 0, Height: > 0 }) { state.SawUnsupported = true; return; }
        using var geom = BuildClipPathGeometry(clip, style, state);
        if (geom is null || geom.IsEmpty) { state.SawUnsupported = true; return; }
        if (obb)
        {
            var b = bbox!.Value;
            geom.Transform(new SKMatrix { ScaleX = b.Width, ScaleY = b.Height, TransX = b.Left, TransY = b.Top, Persp2 = 1 });
        }
        canvas.ClipPath(geom, SKClipOperation.Intersect, antialias: true);
    }

    /// <summary>Union the geometry of a <c>&lt;clipPath&gt;</c>'s child shapes (each honoring its own
    /// <c>transform</c>) into a single fill path. A child <c>&lt;use&gt;</c> resolving to a basic shape is
    /// followed, applying the use's <c>transform</c> AND <c>x</c>/<c>y</c> (the effective CTM is
    /// <c>use.transform · translate(x,y) · target.transform</c>, SVG §5.6). Returns <see langword="null"/>
    /// when no child contributes geometry.</summary>
    private static SKPath? BuildClipPathGeometry(XElement clip, SvgStyle style, SvgRenderState state)
    {
        SKPath? acc = null;
        foreach (var child in clip.Elements())
        {
            var target = child;
            SKMatrix? useOffset = null;
            SKMatrix? useTransform = null;
            if (child.Name.LocalName.Equals("use", StringComparison.OrdinalIgnoreCase))
            {
                if (SvgAttr.HrefId(child) is not { } uid || !state.Ids.TryGetValue(uid, out var ut)) continue;
                target = ut;
                useOffset = SKMatrix.CreateTranslation((float)SvgRasterizer.Len(child, "x", state, style, SvgRasterizer.LenAxis.X), (float)SvgRasterizer.Len(child, "y", state, style, SvgRasterizer.LenAxis.Y));
                useTransform = SvgTransform.Parse(SvgRasterizer.Attr(child, "transform")); // the use's OWN transform
            }
            using var sp = SvgRasterizer.BuildShapePath(target, style, state);
            if (sp is null || sp.IsEmpty) continue;
            // Apply innermost-first: the target's own transform, then translate(x,y), then the use's transform.
            if (SvgTransform.Parse(SvgRasterizer.Attr(target, "transform")) is { } tm) sp.Transform(tm);
            if (useOffset is { } uo) sp.Transform(uo);
            if (useTransform is { } ut2) sp.Transform(ut2);
            if (acc is null) acc = new SKPath(sp);
            else acc.AddPath(sp);
        }
        return acc;
    }

    /// <summary>The geometry bounding box of an element in its OWN coordinate space (its own
    /// <c>transform</c> excluded, descendant transforms included) — the reference box for an
    /// <c>objectBoundingBox</c> clip / mask. Unions the bounds of every descendant basic shape, SKIPPING
    /// non-rendered definition subtrees (<c>&lt;defs&gt;</c>/<c>&lt;clipPath&gt;</c>/<c>&lt;mask&gt;</c>/
    /// <c>&lt;pattern&gt;</c>/gradients/<c>&lt;symbol&gt;</c>/…) so the box reflects PAINTED geometry, not
    /// hidden definitions (PR-241 review [P2]).</summary>
    public static SKRect? ComputeBBox(XElement el, SvgStyle inherited, SvgRenderState state, int depth, SKMatrix ctm, bool isRoot)
    {
        if (depth > SvgRasterizer.MaxDepth) return null;
        var style = SvgRasterizer.ResolveStyle(el, inherited, state);
        var local = ctm;
        if (!isRoot && SvgTransform.Parse(SvgRasterizer.Attr(el, "transform")) is { } m) local = ctm.PreConcat(m);
        SKRect? acc = null;
        using (var sp = SvgRasterizer.BuildShapePath(el, style, state))
            if (sp is not null && !sp.IsEmpty) acc = local.MapRect(sp.Bounds);
        foreach (var child in el.Elements())
        {
            if (IsNonRenderedDefinition(child.Name.LocalName)) continue; // hidden definition → not in the bbox
            acc = UnionRect(acc, ComputeBBox(child, style, state, depth + 1, local, isRoot: false));
        }
        return acc;
    }

    /// <summary>Element local-names that define a resource / metadata and are NOT painted in place (they
    /// render only when REFERENCED), so they're excluded from a geometry bounding box.</summary>
    private static bool IsNonRenderedDefinition(string localName) => localName.ToLowerInvariant() switch
    {
        "defs" or "symbol" or "title" or "desc" or "metadata" or "style"
            or "lineargradient" or "radialgradient" or "stop"
            or "pattern" or "clippath" or "mask" or "filter" or "marker" => true,
        _ => false,
    };

    private static SKRect? UnionRect(SKRect? a, SKRect? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        var r = a.Value;
        r.Union(b.Value);
        return r;
    }

    /// <summary>Resolve a <c>mask="url(#id)"</c> reference to its <c>&lt;mask&gt;</c> element, or
    /// <see langword="null"/> for <c>none</c> / an absent value. A url() that doesn't resolve to a
    /// <c>&lt;mask&gt;</c> is flagged unsupported (and the element renders unmasked).</summary>
    public static XElement? ResolveMask(XElement el, SvgRenderState state)
    {
        var raw = SvgAttr.Presentation(el, "mask");
        if (raw is null) return null;
        raw = raw.Trim();
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (SvgRasterizer.PaintServerId(raw) is { } id && state.Ids.TryGetValue(id, out var m)
            && m.Name.LocalName.Equals("mask", StringComparison.OrdinalIgnoreCase))
            return m;
        state.SawUnsupported = true;
        return null;
    }

    /// <summary>Multiply the element-content layer's alpha by the LUMINANCE of the <c>&lt;mask&gt;</c>'s
    /// rendered content (default <c>mask-type: luminance</c>): render the mask content into a layer whose
    /// composite applies a luminance→alpha color filter and the <c>DstIn</c> blend, then composite the element
    /// layer. <c>maskContentUnits="objectBoundingBox"</c> maps the mask content's unit coordinates onto the
    /// masked element's bounding box — computed with the element's RESOLVED <paramref name="style"/> so
    /// inherited <c>em</c>/<c>rem</c> geometry resolves against the right font-size; an unavailable bbox (e.g.
    /// a text / image target) is flagged and the element renders UNMASKED. Closes BOTH the mask layer and the
    /// element-content layer the caller opened.</summary>
    public static void ApplyMask(SKCanvas canvas, XElement mask, XElement el, SvgStyle style, SvgRenderState state, int depth)
    {
        SKMatrix? contentMatrix = null;
        if ((SvgRasterizer.Attr(mask, "maskContentUnits") ?? "userSpaceOnUse").Trim().Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase))
        {
            if (ComputeBBox(el, style, state, depth: 0, SKMatrix.Identity, isRoot: true) is { Width: > 0, Height: > 0 } b)
                contentMatrix = new SKMatrix { ScaleX = b.Width, ScaleY = b.Height, TransX = b.Left, TransY = b.Top, Persp2 = 1 };
            else
            {
                state.SawUnsupported = true; // no bbox for objectBoundingBox content → leave the element unmasked
                canvas.Restore();            // composite the element-content layer unmodified
                return;
            }
        }
        using var luma = SKColorFilter.CreateLumaColor();
        using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn, ColorFilter = luma };
        canvas.SaveLayer(maskPaint);
        if (contentMatrix is { } cm) canvas.Concat(cm);
        // Mask content renders in a fresh presentation context (its own attributes over the initial style).
        var maskStyle = SvgRasterizer.ResolveStyle(mask, SvgStyle.Initial, state);
        foreach (var child in mask.Elements()) SvgRasterizer.RenderElement(canvas, child, maskStyle, state, depth + 1);
        canvas.Restore(); // composite the mask (luma → DstIn) into the element layer
        canvas.Restore(); // composite the element-content layer onto the canvas
    }
}
