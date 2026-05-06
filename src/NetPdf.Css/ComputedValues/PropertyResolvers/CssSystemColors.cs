// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;
using System.Collections.Generic;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// CSS Color L4 §10 system colors mapped to fixed sRGB values appropriate for print
/// output. The system-color set is context-dependent in screen browsers (it follows
/// the user-selected color scheme), but for a deterministic print pipeline we resolve
/// them to a stable, paper-friendly palette so the property defaults that reference
/// them (notably <c>color: canvastext</c>) parse cleanly. ASCII case-insensitive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fixed values for print.</b> The default for the <c>color</c> property in
/// <c>properties.json</c> is <c>canvastext</c> — that's how the spec phrases it. If
/// the resolver doesn't recognize the keyword, every element with no explicit color
/// gets an "invalid at computed value time" diagnostic, which would flood the sink
/// for any real document. Print output is monochrome-friendly by convention:
/// <c>canvas</c> = paper white, <c>canvastext</c> = ink black, links blue, etc.
/// </para>
/// <para>
/// <b>Clean-room values.</b> The choices below are picked for spec conformance + print
/// readability, not by reading any browser's source. They follow the historical
/// Mosaic/Netscape conventions that the original CSS 2.1 system color list documented
/// in non-normative text, plus reasonable defaults for the L4 additions
/// (<c>canvas</c>, <c>canvastext</c>, <c>linktext</c>, <c>visitedtext</c>,
/// <c>activetext</c>, <c>accentcolor</c>, <c>accentcolortext</c>, etc.).
/// </para>
/// <para>
/// <b>Excluded.</b> The <c>color()</c> functional, <c>color-scheme</c>-dependent
/// resolution, and any post-v1 "user follows OS theme" behavior are out of scope.
/// All values pack as fully opaque (alpha = 0xFF).
/// </para>
/// </remarks>
internal static class CssSystemColors
{
    /// <summary>Look up a CSS system color name (ASCII case-insensitive). Returns
    /// <see langword="true"/> + packed <c>0xAARRGGBB</c> on hit.</summary>
    public static bool TryGet(string name, out uint argb)
    {
        if (string.IsNullOrEmpty(name))
        {
            argb = 0;
            return false;
        }
        return Table.TryGetValue(name.ToLowerInvariant(), out argb);
    }

    private static readonly FrozenDictionary<string, uint> Table = BuildTable();

    private static FrozenDictionary<string, uint> BuildTable()
    {
        var pairs = new Dictionary<string, uint>(20)
        {
            // Surfaces.
            ["canvas"]            = 0xFFFFFFFFu, // paper / page background
            ["canvastext"]        = 0xFF000000u, // ink black
            ["field"]             = 0xFFFFFFFFu, // form input bg = paper
            ["fieldtext"]         = 0xFF000000u, // form input text = ink

            // Links — historical Mosaic palette.
            ["linktext"]          = 0xFF0000EEu, // unvisited link
            ["visitedtext"]       = 0xFF551A8Bu, // visited link
            ["activetext"]        = 0xFFEE0000u, // active link

            // Buttons.
            ["buttonface"]        = 0xFFEFEFEFu,
            ["buttontext"]        = 0xFF000000u,
            ["buttonborder"]      = 0xFF808080u,

            // Selection / highlight.
            ["highlight"]         = 0xFFB4D5FEu, // selection background
            ["highlighttext"]     = 0xFF000000u, // selection text
            ["selecteditem"]      = 0xFFB4D5FEu, // CSS Color 4 alias of highlight
            ["selecteditemtext"]  = 0xFF000000u,

            // Marker (highlighted text fragments, <mark>).
            ["mark"]              = 0xFFFFFF00u, // highlighter yellow
            ["marktext"]          = 0xFF000000u,

            // Disabled / inactive UI.
            ["graytext"]          = 0xFF808080u,

            // Accent (CSS Color 4 §10.2 — used by form controls).
            ["accentcolor"]       = 0xFF0000EEu, // matches linktext for v1
            ["accentcolortext"]   = 0xFFFFFFFFu,
        };
        return pairs.ToFrozenDictionary();
    }
}
