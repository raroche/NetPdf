// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Controls per-document and process-level caching of fetched resources, parsed fonts,
/// and shaped glyph runs. Defaults are tuned for typical document workloads.
/// </summary>
public sealed class CachePolicy
{
    /// <summary>Cap on the process-wide font cache (parsed font faces).</summary>
    public int MaxCachedFontFaces { get; init; } = 64;

    /// <summary>Cap on the per-document shaped-glyph-run cache.</summary>
    public int MaxCachedGlyphRuns { get; init; } = 4096;

    /// <summary>Cap on the per-document parsed-stylesheet cache.</summary>
    public int MaxCachedStylesheets { get; init; } = 32;

    public static CachePolicy Default { get; } = new();
}
