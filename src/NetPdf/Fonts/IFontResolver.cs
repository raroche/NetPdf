// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Resolves a font query (family + weight + style + script) to a font face. NetPdf falls back
/// to system font enumeration if no resolver is set, but custom resolvers can ship fonts with
/// the application or fetch them from a private CDN.
/// </summary>
public interface IFontResolver
{
    /// <summary>
    /// Return a font face matching <paramref name="query"/>, or <c>null</c> if no match is
    /// available — the next entry in the fallback chain will be tried.
    /// </summary>
    ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct);
}
