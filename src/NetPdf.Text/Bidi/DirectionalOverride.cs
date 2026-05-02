// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Directional override status carried by entries on the BD13 directional status stack
/// (UAX #9 §3.3.2). Either neutral (no override active — pushed by LRE/RLE/LRI/RLI/FSI),
/// or one of the explicit overrides L (LRO) and R (RLO).
/// </summary>
/// <remarks>
/// Modeled as a dedicated enum rather than reusing <see cref="BidiClass"/> so that
/// "no override" is unambiguous — confusing it with the bidi class <c>ON</c> would let
/// override application accidentally rewrite character classes to <c>ON</c>.
/// </remarks>
internal enum DirectionalOverride : byte
{
    /// <summary>No override active — characters keep their resolved class.</summary>
    Neutral = 0,

    /// <summary>LRO is in effect — non-paragraph-separator characters are reclassified to L.</summary>
    L = 1,

    /// <summary>RLO is in effect — non-paragraph-separator characters are reclassified to R.</summary>
    R = 2,
}
