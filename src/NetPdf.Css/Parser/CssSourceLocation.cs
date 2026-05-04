// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// Source position of a CSS construct (rule or declaration). Mirrors the
/// <c>NetPdf.SourceLocation</c> shape but lives in <c>NetPdf.Css.Parser</c> because
/// <c>NetPdf.Css</c> cannot reference the facade assembly. The diagnostic-emission stage
/// converts to <c>NetPdf.SourceLocation</c> at the boundary.
/// </summary>
/// <param name="Source">Origin file (URL or <c>"&lt;style&gt;"</c> for inline) when known;
/// <see langword="null"/> for unknown / synthetic / built-in.</param>
/// <param name="Line">1-indexed line; <c>0</c> when unknown.</param>
/// <param name="Column">1-indexed column; <c>0</c> when unknown.</param>
/// <remarks>
/// Phase 2 Task 2 ships these slots populated only with <see cref="Unknown"/>: AngleSharp.Css
/// 1.0.0-beta.144 does not surface position information on its rule/declaration interfaces.
/// Phase 2 Task 3's pre-pass tokenizer is where real positions get attached — it tokenizes
/// the raw stylesheet text with position tracking, threads positions through to the rules it
/// rewrites, and the adapter is updated to read those positions from a side channel.
/// </remarks>
internal readonly record struct CssSourceLocation(string? Source, int Line, int Column)
{
    public static CssSourceLocation Unknown { get; } = new(null, 0, 0);
}
