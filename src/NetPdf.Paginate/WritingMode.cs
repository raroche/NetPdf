// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate;

/// <summary>
/// Per CSS Writing Modes L3 §3.1 — the document / element block-flow
/// direction. Drives which axis is "block" vs "inline" + how the
/// layouter reads the box sizes. NetPdf v1 supports the four mainline
/// modes; <c>sideways-rl</c> / <c>sideways-lr</c> emit
/// <c>CSS-WRITING-MODE-SIDEWAYS-UNSUPPORTED-001</c> + are projected to
/// the closest <see cref="WritingMode"/> per the compatibility matrix.
/// </summary>
internal enum WritingMode
{
    /// <summary><c>horizontal-tb</c> — Latin / Cyrillic / etc.
    /// (default). Lines stack top-to-bottom; characters within a line
    /// flow left-to-right (subject to bidi).</summary>
    HorizontalTb = 0,

    /// <summary><c>vertical-rl</c> — traditional Chinese / Japanese.
    /// Lines stack right-to-left; characters within a line flow
    /// top-to-bottom.</summary>
    VerticalRl = 1,

    /// <summary><c>vertical-lr</c> — Mongolian. Lines stack
    /// left-to-right; characters flow top-to-bottom.</summary>
    VerticalLr = 2,
}
