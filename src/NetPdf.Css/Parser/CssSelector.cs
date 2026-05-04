// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// A CSS selector. Task 2 only carries the raw selector text — the structural decomposition
/// + bytecode compilation lands in Task 6 (<c>src/NetPdf.Css/Selectors/</c>). The wrapper
/// type exists now so call sites pass a typed selector rather than an untyped string,
/// preventing accidental confusion with property names or value text.
/// </summary>
/// <param name="RawText">The selector text exactly as AngleSharp.Css normalized it during
/// parsing. AngleSharp.Css canonicalizes whitespace around combinators (<c>.a > .b</c>
/// becomes <c>.a>.b</c>), so this is not byte-for-byte identical to the source.</param>
internal sealed record CssSelector(string RawText);
