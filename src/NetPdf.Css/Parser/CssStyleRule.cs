// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser;

/// <summary>
/// A standard CSS style rule: a selector followed by a declaration block, e.g.,
/// <c>.foo, .bar &gt; .baz { color: red; font-weight: bold !important }</c>.
/// </summary>
/// <param name="Selector">The selector text (and, in later phase tasks, the compiled
/// selector bytecode). Task 6 fills in the structured selector tree; for Task 2 the
/// selector is a thin wrapper around the raw text.</param>
/// <param name="Declarations">The declarations inside the block. AngleSharp.Css expands
/// shorthand properties (e.g., <c>background</c>) into their longhand components during
/// parsing — the cascade resolver in Task 7 expects longhands, so this fidelity loss is
/// deliberate and beneficial downstream.</param>
/// <param name="Location">Source position of the rule's selector start. Currently
/// <see cref="CssSourceLocation.Unknown"/> until Task 3 wires real positions.</param>
internal sealed record CssStyleRule(
    CssSelector Selector,
    ImmutableArray<CssDeclaration> Declarations,
    CssSourceLocation Location) : CssRule(Location);
