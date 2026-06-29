// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Xml.Linq;
using SkiaSharp;

namespace NetPdf.Svg;

/// <summary>A resolved fill/stroke shader plus any backing resource that must outlive it (a
/// <c>&lt;pattern&gt;</c> tile is rendered into an <see cref="SKImage"/> the shader samples). Disposing this
/// releases both. A gradient shader has no backing image.</summary>
internal sealed class SvgResolvedShader(SKShader shader, SKImage? backing) : IDisposable
{
    public SKShader Shader { get; } = shader;
    private readonly SKImage? _backing = backing;

    public void Dispose()
    {
        Shader.Dispose();
        _backing?.Dispose();
    }
}

/// <summary>Mutable render-walk state shared across the SVG element tree: the element-count budget (DoS
/// guard), the unsupported-feature flag, the id → element map (gradient / pattern / <c>&lt;use&gt;</c>
/// targets), the user-space viewport extent used to resolve <c>userSpaceOnUse</c> percentages, and the
/// nesting depth of <c>&lt;pattern&gt;</c> tile rendering (a second DoS guard against self-referential
/// patterns).</summary>
internal sealed class SvgRenderState
{
    public int Elements;
    public bool SawUnsupported;
    public IReadOnlyDictionary<string, XElement> Ids = new Dictionary<string, XElement>();
    public double ViewportW;
    public double ViewportH;
    public int PatternDepth;

    /// <summary>Builds a repeating-tile shader for a <c>&lt;pattern&gt;</c> paint server (set by the
    /// rasterizer, which owns the recursive tile rendering). <see langword="null"/> until wired.</summary>
    public Func<XElement, SKRect, float, SvgStyle, SvgResolvedShader?>? PatternShaderFactory;

    /// <summary>Resolve a <c>url(#id)</c> paint server against a shape's bounding box. A
    /// <c>&lt;pattern&gt;</c> routes through <see cref="PatternShaderFactory"/>; a gradient is built by
    /// <see cref="SvgPaintServers"/>. Returns <see langword="null"/> when the id is missing or doesn't name a
    /// paint server — the caller flags it unsupported and skips the paint.</summary>
    public SvgResolvedShader? ResolveShader(string id, SKRect bounds, float opacity, SvgStyle style)
    {
        if (!Ids.TryGetValue(id, out var el)) return null;
        if (el.Name.LocalName.Equals("pattern", StringComparison.OrdinalIgnoreCase))
            return PatternShaderFactory?.Invoke(el, bounds, opacity, style);
        var shader = SvgPaintServers.BuildShader(el, bounds, Ids, ViewportW, ViewportH, opacity, style.CurrentColor);
        return shader is null ? null : new SvgResolvedShader(shader, null);
    }
}
