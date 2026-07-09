// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>
/// A deterministic <see cref="IFontResolver"/> that maps EVERY requested family to the committed DejaVu Sans
/// pack (<see cref="VisualHarness.FontsDir"/>), selecting the RIBBI face from the query's weight and style.
///
/// <para>The visual-regression gate PINS fonts so the page-for-page diff measures LAYOUT-ENGINE differences
/// (NetPdf vs. Chrome) rather than font drift between the .NET test host and the pinned-Chrome reference
/// generator. Chrome is aliased to the SAME DejaVu Sans via the Docker image's fontconfig
/// (<c>docker/Dockerfile</c>), so both sides shape identical glyphs and only real layout deltas surface.</para>
///
/// <para>Bold = <c>weight &gt;= 600</c> (so <c>font-weight: 600</c>/<c>bold</c> and <c>&lt;strong&gt;</c> all map
/// to the bold face); italic OR oblique map to the DejaVu Oblique faces (DejaVu ships oblique, not a true
/// italic — CSS Fonts Module Level 4 §5.2 permits treating italic and oblique as synonyms). Bytes are cached
/// per face for the life of the resolver.</para>
/// </summary>
internal sealed class PinnedFontResolver : IFontResolver
{
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<byte>> _cache = new();

    public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
    {
        var bold = query.WeightCss >= 600;
        var slanted = query.Style is FontStyle.Italic or FontStyle.Oblique;
        var file = (bold, slanted) switch
        {
            (true, true) => "DejaVuSans-BoldOblique.ttf",
            (true, false) => "DejaVuSans-Bold.ttf",
            (false, true) => "DejaVuSans-Oblique.ttf",
            _ => "DejaVuSans.ttf",
        };

        var bytes = _cache.GetOrAdd(file, static f => File.ReadAllBytes(Path.Combine(VisualHarness.FontsDir, f)));
        return new ValueTask<FontFaceData?>(new FontFaceData
        {
            Bytes = bytes,
            Family = "DejaVu Sans",
            WeightCss = bold ? 700 : 400,
            Style = slanted ? FontStyle.Oblique : FontStyle.Normal,
        });
    }
}
