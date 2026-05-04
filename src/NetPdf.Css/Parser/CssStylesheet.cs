// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// The top-level container for a parsed CSS stylesheet, in NetPdf's internal AST.
/// One instance per <c>&lt;style&gt;</c> element or external stylesheet that downstream
/// stages (cascade, computed values, box generation) consume. AngleSharp.Css types do not
/// appear anywhere in this tree — that boundary is the adapter's responsibility.
/// </summary>
internal sealed record CssStylesheet(IReadOnlyList<CssRule> Rules);
