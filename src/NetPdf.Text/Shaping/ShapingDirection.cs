// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Shaping;

/// <summary>
/// Writing direction for a shaping run. Mirrors HarfBuzz's <c>hb_direction_t</c> but
/// keeps the engine API independent of HarfBuzzSharp types so consumers don't take a
/// transitive dependency on the shaping native library.
/// </summary>
internal enum ShapingDirection
{
    /// <summary>Latin, Cyrillic, Devanagari, Han, Hiragana, … and most other scripts.</summary>
    LeftToRight,

    /// <summary>Arabic, Hebrew, N'Ko, Thaana, etc.</summary>
    RightToLeft,

    /// <summary>Vertical-CJK top-to-bottom (rare for inline text in HTML/CSS).</summary>
    TopToBottom,

    /// <summary>Vertical bottom-to-top (Mongolian).</summary>
    BottomToTop,
}
