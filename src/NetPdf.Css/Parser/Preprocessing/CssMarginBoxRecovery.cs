// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// A page margin-box at-rule recovered from inside an <c>@page</c> body — for example
/// <c>@top-center { content: "Header" }</c>. AngleSharp.Css 1.0.0-beta.144 silently drops
/// these (they reach neither the parent <c>ICssPageRule</c> nor the top of the stylesheet);
/// the pre-pass preserves them so the adapter can re-parent them under
/// <see cref="CssAtRule.ChildRules"/> on the owning page rule.
/// </summary>
/// <param name="Name">The margin-box name without the leading <c>@</c> — for example
/// <c>"top-center"</c>, <c>"bottom-right-corner"</c>.</param>
/// <param name="DeclarationsRawText">The raw text inside the margin-box's <c>{ ... }</c>
/// block (without the braces). Task 7's cascade resolver will tokenize this into
/// <see cref="CssDeclaration"/> values; the pre-pass only carries the text forward.</param>
/// <param name="Location">Source position of the margin-box's <c>@</c> keyword.</param>
internal sealed record CssMarginBoxRecovery(
    string Name,
    string DeclarationsRawText,
    CssSourceLocation Location);
