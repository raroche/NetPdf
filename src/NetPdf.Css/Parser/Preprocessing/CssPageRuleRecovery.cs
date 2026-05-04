// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Information about an <c>@page</c> rule recovered from raw CSS by the pre-pass tokenizer.
/// Closes two AngleSharp.Css 1.0.0-beta.144 gaps that review cycle 1 named as Task 3 blockers:
/// the page selector (<c>:first</c>, <c>:left</c>, <c>:right</c>, named pages) and inner
/// margin-box at-rules.
/// </summary>
/// <param name="OrdinalIndex">0-indexed position of this rule among <c>@page</c> rules in
/// source order. The adapter uses it to align the recovery with the corresponding AngleSharp
/// rule in the CSSOM (AngleSharp emits <c>@page</c> rules in the same order, just with
/// missing inner detail).</param>
/// <param name="SelectorText">The selector authored after <c>@page</c> — for example
/// <c>":first"</c>, <c>":left :right"</c>, <c>"chapter"</c>, or empty for an unnamed page.
/// AngleSharp drops this entirely; the pre-pass recovers it.</param>
/// <param name="MarginBoxes">Page margin-box at-rules inside this <c>@page</c>'s body.</param>
/// <param name="Location">Source position of the <c>@page</c> at-keyword.</param>
internal sealed record CssPageRuleRecovery(
    int OrdinalIndex,
    string SelectorText,
    ImmutableArray<CssMarginBoxRecovery> MarginBoxes,
    CssSourceLocation Location);
