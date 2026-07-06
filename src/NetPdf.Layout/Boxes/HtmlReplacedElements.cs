// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// HTML elements that are <i>replaced elements</i> per HTML Living Standard
/// "Rendering" §15.5 — the layout engine treats them as atomic and consults
/// their intrinsic dimensions for sizing per CSS Sizing 3 §5. Distinguishing
/// replaced from non-replaced elements drives the
/// <see cref="BoxKind.BlockReplacedElement"/> vs
/// <see cref="BoxKind.InlineReplacedElement"/> dispatch in
/// <see cref="DisplayMapper"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage</b> is the media subset: <c>img</c>, <c>video</c>, <c>audio</c>,
/// <c>canvas</c>, <c>iframe</c>, <c>object</c>, <c>embed</c> — plus an inline
/// <c>&lt;svg&gt;</c> element, which is treated as atomic replaced content (its
/// intrinsic size comes from <c>width</c>/<c>height</c>/<c>viewBox</c> and it renders
/// through the SVG pipeline like an <c>&lt;img&gt;</c> SVG source; its SVG-namespaced
/// children are not laid out as HTML boxes). Form controls (<c>input</c>,
/// <c>textarea</c>, <c>select</c>, <c>button</c>) are also replaced per the HTML spec
/// but have internal anonymous structure (placeholder text, dropdown options, etc.)
/// that v1 doesn't render — they stay non-replaced for now and emit no visible UI.
/// </para>
/// <para>
/// <b>Lookup is ASCII case-insensitive</b> per HTML5 §13.</para>
/// </remarks>
internal static class HtmlReplacedElements
{
    /// <summary><see langword="true"/> when <paramref name="localName"/> names an
    /// HTML replaced element per the v1 coverage list.</summary>
    public static bool IsReplaced(string localName)
    {
        if (string.IsNullOrEmpty(localName)) return false;
        return Set.Contains(localName);
    }

    private static readonly FrozenSet<string> Set =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "img", "video", "audio", "canvas", "iframe", "object", "embed",
            // An inline <svg> element is treated as a replaced element (atomic; its intrinsic size
            // comes from width/height/viewBox and it renders via the SVG pipeline, like <img> SVG).
            // Its SVG-namespaced children (<circle>/<path>/…) are NOT laid out as HTML boxes.
            "svg",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
