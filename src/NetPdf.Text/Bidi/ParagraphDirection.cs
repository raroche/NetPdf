// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Paragraph-level direction request consumed by the bidi algorithm. Maps to UAX #9
/// rules P2/P3: <see cref="LeftToRight"/> and <see cref="RightToLeft"/> force a known
/// base direction (paragraph level 0 or 1); <see cref="Auto"/> infers it from the first
/// strong character in the paragraph (defaulting to LTR when no strong char exists).
/// </summary>
internal enum ParagraphDirection
{
    /// <summary>Force paragraph level 0 (LTR).</summary>
    LeftToRight,

    /// <summary>Force paragraph level 1 (RTL).</summary>
    RightToLeft,

    /// <summary>Auto-detect from the first strong character (P2/P3).</summary>
    Auto,
}
