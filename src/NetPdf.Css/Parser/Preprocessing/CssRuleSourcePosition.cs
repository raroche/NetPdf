// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Source position of a top-level rule, indexed by the rule's ordinal among all top-level
/// rules in the source. Closes the fourth Task 3 blocker from review cycle 1: AngleSharp.Css
/// 1.0.0-beta.144 does not surface position information on its <c>ICss*</c> interfaces,
/// so every <see cref="CssRule.Location"/> field would otherwise stay
/// <see cref="CssSourceLocation.Unknown"/>.
/// </summary>
/// <param name="OrdinalIndex">0-indexed position in source order, counting every top-level
/// rule (style rules + at-rules together). AngleSharp emits rules in the same order so the
/// adapter can index directly into the position array by AngleSharp's rule index.</param>
/// <param name="Location">The rule's source position — line/column of its first character
/// (the <c>@</c> for at-rules, the first selector character for style rules).</param>
internal sealed record CssRuleSourcePosition(
    int OrdinalIndex,
    CssSourceLocation Location);
