// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Stand-in for the user-agent stylesheet's <c>display</c> rules per HTML
/// Living Standard "Rendering" §15.3 + the spec-blessed CSS defaults for HTML
/// elements. Looks up an HTML element's local name and returns the spec-default
/// <c>display</c> keyword string (e.g., "block" for <c>div</c>, "inline" for
/// <c>span</c>, "table-row" for <c>tr</c>). The cascade resolver is the proper
/// place to inject UA-stylesheet rules — this lookup is a cycle-1 stand-in
/// until the UA stylesheet ships, so <see cref="BoxBuilder"/> can compute
/// sensible default <see cref="BoxKind"/>s for unstyled HTML.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookup is ASCII case-insensitive</b> per HTML5 §13 (HTML element names
/// are case-insensitive in the HTML namespace). The keys are stored
/// lower-case; callers must lower-case the input before lookup.
/// </para>
/// <para>
/// <b>Coverage</b> is intentionally limited to the elements the corpus +
/// real-world stylesheets touch most. Anything not in the table falls back
/// to the CSS spec default of <c>inline</c>. Cycle 2 will replace this with
/// the actual UA stylesheet (a `.css` file shipped as an embedded resource +
/// loaded by the cascade as origin = User Agent).
/// </para>
/// <para>
/// <b>Excluded</b> from cycle 1: form-control elements (<c>input</c>,
/// <c>textarea</c>, <c>select</c>, <c>button</c>) — they're "replaced-ish"
/// with internal anonymous structure that needs more thought; <c>ruby</c>
/// + descendants — ruby layout is post-v1; SVG inline elements — handled
/// by the SVG pipeline.
/// </para>
/// </remarks>
internal static class HtmlDefaultDisplay
{
    /// <summary>The CSS spec default <c>display</c> keyword when no UA rule
    /// applies. Per Display L3 §1, it's <c>inline</c>.</summary>
    public const string SpecDefault = "inline";

    /// <summary>Look up the UA-default <c>display</c> keyword for an HTML
    /// element by local name (ASCII case-insensitive). Returns
    /// <see cref="SpecDefault"/> when no UA rule is defined for the tag.</summary>
    public static string GetDefault(string localName)
    {
        if (string.IsNullOrEmpty(localName)) return SpecDefault;
        return Table.TryGetValue(localName.ToLowerInvariant(), out var d) ? d : SpecDefault;
    }

    private static readonly FrozenDictionary<string, string> Table = BuildTable();

    private static FrozenDictionary<string, string> BuildTable()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Document structure — block.
            ["html"] = "block", ["body"] = "block",

            // Block-level flow.
            ["address"] = "block", ["article"] = "block", ["aside"] = "block",
            ["blockquote"] = "block", ["details"] = "block", ["dialog"] = "block",
            ["dd"] = "block", ["div"] = "block", ["dl"] = "block", ["dt"] = "block",
            ["fieldset"] = "block", ["figcaption"] = "block", ["figure"] = "block",
            ["footer"] = "block", ["form"] = "block",
            ["h1"] = "block", ["h2"] = "block", ["h3"] = "block",
            ["h4"] = "block", ["h5"] = "block", ["h6"] = "block",
            ["header"] = "block", ["hgroup"] = "block", ["hr"] = "block",
            ["main"] = "block", ["nav"] = "block", ["p"] = "block",
            ["pre"] = "block", ["section"] = "block", ["summary"] = "block",

            // Lists.
            ["ul"] = "block", ["ol"] = "block", ["menu"] = "block",
            ["li"] = "list-item",

            // Tables — the wrapper + every internal kind has its own display.
            ["table"] = "table",
            ["thead"] = "table-header-group",
            ["tbody"] = "table-row-group",
            ["tfoot"] = "table-footer-group",
            ["tr"] = "table-row",
            ["td"] = "table-cell",
            ["th"] = "table-cell",
            ["col"] = "table-column",
            ["colgroup"] = "table-column-group",
            ["caption"] = "table-caption",

            // Inline (CSS spec default — listed for clarity even though the
            // SpecDefault fallback already returns "inline" for unknown tags).
            ["a"] = "inline", ["abbr"] = "inline", ["b"] = "inline",
            ["bdi"] = "inline", ["bdo"] = "inline", ["br"] = "inline",
            ["cite"] = "inline", ["code"] = "inline", ["data"] = "inline",
            ["del"] = "inline", ["dfn"] = "inline", ["em"] = "inline",
            ["i"] = "inline", ["ins"] = "inline", ["kbd"] = "inline",
            ["label"] = "inline", ["mark"] = "inline", ["output"] = "inline",
            ["q"] = "inline", ["s"] = "inline", ["samp"] = "inline",
            ["small"] = "inline", ["span"] = "inline", ["strong"] = "inline",
            ["sub"] = "inline", ["sup"] = "inline", ["time"] = "inline",
            ["u"] = "inline", ["var"] = "inline", ["wbr"] = "inline",

            // Replaced media — the display defaults to inline; replaced-ness
            // is detected separately via HtmlReplacedElements.
            ["img"] = "inline", ["video"] = "inline", ["audio"] = "inline",
            ["canvas"] = "inline", ["iframe"] = "inline",
            ["object"] = "inline", ["embed"] = "inline", ["picture"] = "inline",
        };
        return dict.ToFrozenDictionary();
    }
}
