// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// A CSS declaration value. Task 2 only carries the raw value text — the typed value tree
/// (lengths, colors, gradients, transforms, function calls) lands in Tasks 9–10. The wrapper
/// type exists now so call sites pass a typed value rather than a bare string.
/// </summary>
/// <param name="RawText">The value text as AngleSharp.Css normalized it during parsing.
/// Named colors are expanded to <c>rgba(r, g, b, a)</c> form; whitespace is canonicalized;
/// shorthand syntax is rewritten when AngleSharp.Css splits a shorthand declaration into
/// longhands, in which case the per-longhand value here reflects the resolved longhand text
/// rather than the original shorthand authoring.</param>
internal sealed record CssValue(string RawText);
