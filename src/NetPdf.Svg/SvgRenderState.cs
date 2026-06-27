// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>Mutable render-walk state shared across the SVG element tree: the element-count budget (DoS
/// guard), the unsupported-feature flag, the id → element map (gradient / <c>&lt;use&gt;</c> targets), and
/// the user-space viewport extent used to resolve <c>userSpaceOnUse</c> percentages.</summary>
internal sealed class SvgRenderState
{
    public int Elements;
    public bool SawUnsupported;
    public IReadOnlyDictionary<string, XElement> Ids = new Dictionary<string, XElement>();
    public double ViewportW;
    public double ViewportH;

    /// <summary>Resolve a <c>url(#id)</c> gradient paint server against a shape's bounding box. Returns
    /// <see langword="null"/> when the id is missing or doesn't name a gradient (e.g. a pattern) — the
    /// caller flags it unsupported and skips the paint.</summary>
    public SKShader? ResolveShader(string id, SKRect bounds, float opacity) =>
        Ids.TryGetValue(id, out var el)
            ? SvgPaintServers.BuildShader(el, bounds, Ids, ViewportW, ViewportH, opacity)
            : null;
}
