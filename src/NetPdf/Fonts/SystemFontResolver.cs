// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;
using NetPdf.Text.Fonts;
using NetPdf.Text.Fonts.SystemFonts;

namespace NetPdf;

/// <summary>
/// <see cref="IFontResolver"/> backed by a process-wide system-font index. Resolves
/// CSS-generic family names (<c>serif</c>, <c>sans-serif</c>, <c>monospace</c>,
/// <c>cursive</c>, <c>fantasy</c>, <c>system-ui</c>) by walking a per-platform fallback
/// chain of known family names; for non-generic queries, looks up the family directly
/// in the index built from the OS's font directories.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy index build.</b> Indexing every system font is non-trivial (parsing every
/// TTF in <c>/System/Library/Fonts</c> takes tens of milliseconds on warm cache,
/// hundreds cold). The index is built lazily on the first <see cref="ResolveAsync"/>
/// call; tests that don't touch this resolver pay nothing.
/// </para>
/// <para>
/// <b>CSS-generic mapping.</b> The fallback chains target the families NetPdf's three
/// production platforms ship out of the box. They follow the same approach Chromium /
/// WebKit / Gecko use for their default generic-family substitution tables, generalized
/// to the macOS / Windows / Linux trinity.
/// </para>
/// <para>
/// <b>Custom resolvers.</b> Callers wanting full control should implement
/// <see cref="IFontResolver"/> directly and either delegate to <see cref="SystemFontResolver"/>
/// for fallback or skip system-font resolution entirely (e.g. shipping bundled fonts).
/// </para>
/// </remarks>
public sealed class SystemFontResolver : IFontResolver
{
    private static readonly FrozenDictionary<string, string[]> CssGenericFallbacks = BuildCssGenericFallbacks();
    private readonly Lazy<SystemFontIndex> _index;
    private readonly FontCache _cache;

    /// <summary>
    /// Construct a resolver that lazily builds an index from the current platform's font
    /// directories.
    /// </summary>
    public SystemFontResolver()
    {
        _index = new Lazy<SystemFontIndex>(
            () => SystemFontIndex.Build(SystemFontEnumerator.CreateForCurrentPlatform()));
        _cache = new FontCache();
    }

    /// <summary>
    /// Test / advanced-caller seam: construct over a pre-built index. Lets fixtures
    /// inject a deterministic set of entries without touching the disk.
    /// </summary>
    internal SystemFontResolver(SystemFontIndex index, FontCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = new Lazy<SystemFontIndex>(() => index);
        _cache = cache ?? new FontCache();
    }

    /// <inheritdoc />
    public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = ResolveEntry(query);
        if (entry is null) return ValueTask.FromResult<FontFaceData?>(null);

        var bytes = _cache.GetOrAdd(entry.Value.FilePath, static path => File.ReadAllBytes(path));
        var data = new FontFaceData
        {
            Bytes = bytes,
            Family = entry.Value.FamilyName,
            WeightCss = entry.Value.WeightCss,
            StretchCss = entry.Value.StretchCss,
            Style = entry.Value.IsItalic ? FontStyle.Italic : FontStyle.Normal,
            PostScriptName = entry.Value.PostScriptName,
            Source = new Uri("file://" + entry.Value.FilePath),
        };
        return ValueTask.FromResult<FontFaceData?>(data);
    }

    /// <summary>
    /// Pure-lookup variant that returns the chosen system entry without reading bytes.
    /// Useful for tests + diagnostics. Propagates <see cref="FontQuery.StretchCss"/> through
    /// to the matcher; null is normalized to <c>5</c> (CSS Fonts 4 §5.2.3 default) and
    /// out-of-range explicit values are clamped to <c>[1, 9]</c> per the public
    /// <see cref="FontQuery.StretchCss"/> contract documented for the default resolver.
    /// </summary>
    internal SystemFontEntry? ResolveEntry(FontQuery query)
    {
        var italic = query.Style is FontStyle.Italic or FontStyle.Oblique;
        var stretchCss = Math.Clamp(query.StretchCss ?? 5, 1, 9);

        // Direct hit by family name.
        if (_index.Value.HasFamily(query.Family))
        {
            return _index.Value.FindBest(query.Family, query.WeightCss, italic, stretchCss);
        }

        // CSS-generic expansion: walk the fallback chain in order.
        if (CssGenericFallbacks.TryGetValue(query.Family, out var chain))
        {
            foreach (var candidateFamily in chain)
            {
                if (_index.Value.HasFamily(candidateFamily))
                {
                    return _index.Value.FindBest(candidateFamily, query.WeightCss, italic, stretchCss);
                }
            }
        }
        return null;
    }

    private static FrozenDictionary<string, string[]> BuildCssGenericFallbacks()
    {
        // Chains are searched in order; first family present in the index wins. Coverage
        // targets all three NetPdf production platforms — at least one entry per chain is
        // typically shipped by macOS, Windows, or Linux's default font set.
        var raw = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["serif"] = ["Times New Roman", "Times", "Liberation Serif", "DejaVu Serif", "Noto Serif"],
            ["sans-serif"] = ["Arial", "Helvetica", "Helvetica Neue", "Liberation Sans", "DejaVu Sans", "Noto Sans"],
            ["monospace"] = ["Courier New", "Courier", "Liberation Mono", "DejaVu Sans Mono", "Menlo", "Consolas", "Noto Sans Mono"],
            ["cursive"] = ["Comic Sans MS", "Apple Chancery", "URW Chancery L"],
            ["fantasy"] = ["Impact", "Papyrus", "Western"],
            ["system-ui"] = ["San Francisco", "Helvetica Neue", "Segoe UI", "Cantarell", "Ubuntu", "Liberation Sans"],
        };
        return raw.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
