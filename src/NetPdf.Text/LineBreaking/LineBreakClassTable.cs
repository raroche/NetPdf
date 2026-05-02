// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.LineBreaking;

/// <summary>
/// Maps Unicode codepoints to their <see cref="LineBreakClass"/> per UCD
/// <c>LineBreak.txt</c> 16.0. Delegates to <see cref="LineBreakClassUcdRanges.Lookup"/>
/// after validating the codepoint range.
/// </summary>
internal static class LineBreakClassTable
{
    /// <summary>Look up the line-break class of a Unicode codepoint.</summary>
    public static LineBreakClass GetClass(int codepoint)
    {
        if ((uint)codepoint > 0x10FFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(codepoint), codepoint,
                "Codepoint must be in the Unicode range [0, 0x10FFFF].");
        }
        return LineBreakClassUcdRanges.Lookup(codepoint);
    }
}
