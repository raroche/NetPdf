// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Xml.Linq;

namespace NetPdf.Svg;

/// <summary>Shared SVG attribute access: a plain attribute, a presentation value (an inline
/// <c>style="…"</c> declaration wins over the attribute per SVG §6.4), and the <c>href</c> /
/// <c>xlink:href</c> reference used by <c>&lt;use&gt;</c> and gradient inheritance.</summary>
internal static class SvgAttr
{
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    public static string? Get(XElement el, string name) => el.Attribute(name)?.Value;

    /// <summary>A presentation value from either the inline <c>style="…"</c> declaration (which wins) or
    /// the matching attribute. Returns <see langword="null"/> when unset.</summary>
    public static string? Presentation(XElement el, string name)
    {
        var style = el.Attribute("style")?.Value;
        if (style is not null)
        {
            foreach (var decl in style.Split(';'))
            {
                var c = decl.IndexOf(':');
                if (c <= 0) continue;
                if (decl.AsSpan(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return decl[(c + 1)..].Trim();
            }
        }
        return el.Attribute(name)?.Value;
    }

    /// <summary>The local-fragment id a <c>href</c> / <c>xlink:href</c> points at (e.g. <c>"#grad"</c> →
    /// <c>"grad"</c>), or <see langword="null"/> when absent or not a local <c>#fragment</c>.</summary>
    public static string? HrefId(XElement el)
    {
        var raw = el.Attribute("href")?.Value ?? el.Attribute(Xlink + "href")?.Value;
        if (raw is null) return null;
        raw = raw.Trim();
        return raw.StartsWith('#') && raw.Length > 1 ? raw[1..] : null;
    }
}
