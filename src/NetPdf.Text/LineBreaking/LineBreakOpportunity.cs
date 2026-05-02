// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.LineBreaking;

/// <summary>
/// The kind of line-break opportunity at a given position in a piece of text. Output of
/// <see cref="LineBreakAlgorithm.FindBreaks"/> at each position between two codepoints
/// (and at the end of the text).
/// </summary>
/// <remarks>
/// UAX #14 distinguishes:
/// <list type="bullet">
///   <item><see cref="Mandatory"/> — caller MUST break here (paragraph boundary).</item>
///   <item><see cref="Allowed"/> — caller may break here (line-wrapping opportunity).</item>
///   <item><see cref="Prohibited"/> — caller MUST NOT break here (no opportunity).</item>
/// </list>
/// </remarks>
internal enum LineBreakOpportunity : byte
{
    /// <summary>No break is permitted between these characters.</summary>
    Prohibited = 0,

    /// <summary>A break is permitted at this position (line-wrapping opportunity).</summary>
    Allowed = 1,

    /// <summary>A break is required at this position (paragraph break).</summary>
    Mandatory = 2,
}
