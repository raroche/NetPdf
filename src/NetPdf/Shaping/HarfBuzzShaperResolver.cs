// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Text.Shaping;

namespace NetPdf.Shaping;

/// <summary>
/// Per Phase 5 layout→PDF wiring, cycle 1 — the PRODUCTION
/// <see cref="IShaperResolver"/>: resolves a real font (via an
/// <see cref="IFontResolver"/>) for a <see cref="ComputedStyle"/> and
/// returns a HarfBuzz <see cref="HbShaper"/> for it, replacing the
/// synthetic test resolvers the layout has used so far. This is the
/// first link in the chain that lets real (text-containing) documents
/// lay out + render: <c>CSS font resolution → THIS shaper → paint
/// bridge → HtmlPdf.Convert facade</c>.
///
/// <para><b>Forward-compatible by design.</b> It reads font-size +
/// font-style through the normal style readers, so it honors whatever
/// the CSS cascade resolves. TODAY the cascade does not yet resolve
/// <c>font-size</c>, <c>font-family</c>, or <c>font-weight</c> (those
/// property resolvers are the documented "cycle 2 backlog"
/// in <c>PropertyResolverDispatch</c> — only <c>font-style</c>
/// resolves), so:</para>
/// <list type="bullet">
///   <item><b>font-size</b> — read via
///   <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrDefault"/>;
///   returns the configured default until <c>FontSizeResolver</c> lands,
///   then real sizes flow through automatically (no code change here).</item>
///   <item><b>font-style</b> — read live (normal / italic / oblique).</item>
///   <item><b>font-family</b> / <b>font-weight</b> — TODO: use the
///   configured default family + normal weight until
///   <c>FontFamilyListResolver</c> / <c>FontWeightResolver</c> are wired;
///   then read the resolved family stack + weight here.</item>
/// </list>
///
/// <para><b>Ownership + caching (per the <see cref="IShaperResolver"/>
/// contract).</b> The resolver OWNS every <see cref="HbShaper"/> it
/// creates + disposes them all at <see cref="Dispose"/>; callers MUST
/// NOT dispose. Shapers are cached by (resolved font key, size) so the
/// same <see cref="ComputedStyle"/> yields the same instance.</para>
///
/// <para><b>Determinism note.</b> HarfBuzz shaping is deterministic for
/// a given (font bytes, size). The non-deterministic part is WHICH font
/// the <see cref="IFontResolver"/> picks — the default
/// <see cref="SystemFontResolver"/> reads platform fonts. Wiring a
/// deterministic default (a bundled last-resort font) + the determinism
/// contract belongs to the facade-wiring cycle, not here; this resolver
/// just consumes whatever <see cref="IFontResolver"/> it's given.</para>
/// </summary>
internal sealed class HarfBuzzShaperResolver : IShaperResolver
{
    /// <summary>CSS initial <c>font-size</c> (<c>medium</c>) in px — the
    /// fallback until <c>FontSizeResolver</c> resolves real sizes.</summary>
    public const double DefaultFontSizePx = 16.0;

    /// <summary>CSS <c>normal</c> font-weight — the fallback until
    /// <c>FontWeightResolver</c> is wired.</summary>
    private const int DefaultWeightCss = 400;

    private readonly IFontResolver _fontResolver;
    private readonly string _defaultFamily;
    private readonly double _defaultFontSizePx;
    private readonly Dictionary<ShaperKey, HbShaper> _cache = new();
    private bool _disposed;

    /// <summary>Construct a resolver over a font source.</summary>
    /// <param name="fontResolver">Resolves a <see cref="FontQuery"/> to
    /// font bytes; e.g. <see cref="SystemFontResolver"/> or a caller's
    /// <see cref="HtmlPdfOptions.FontResolver"/>.</param>
    /// <param name="defaultFamily">The family used until the CSS
    /// font-family resolver lands (a generic like <c>sans-serif</c> the
    /// resolver maps to a real face).</param>
    /// <param name="defaultFontSizePx">Fallback size when the cascade
    /// hasn't resolved <c>font-size</c>.</param>
    public HarfBuzzShaperResolver(
        IFontResolver fontResolver,
        string defaultFamily = "sans-serif",
        double defaultFontSizePx = DefaultFontSizePx)
    {
        ArgumentNullException.ThrowIfNull(fontResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFamily);
        if (!(defaultFontSizePx > 0) || !double.IsFinite(defaultFontSizePx))
            throw new ArgumentOutOfRangeException(nameof(defaultFontSizePx),
                $"defaultFontSizePx must be finite + positive; got {defaultFontSizePx}");
        _fontResolver = fontResolver;
        _defaultFamily = defaultFamily;
        _defaultFontSizePx = defaultFontSizePx;
    }

    /// <inheritdoc />
    public HbShaper Resolve(ComputedStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // font-size: honored live via the standard reader. Clamp a
        // non-positive / non-finite value to the default (HbShaper
        // requires a positive finite size).
        var sizePx = style.ReadLengthPxOrDefault(PropertyId.FontSize, _defaultFontSizePx);
        if (!(sizePx > 0) || !double.IsFinite(sizePx)) sizePx = _defaultFontSizePx;

        // font-style: live keyword (0 normal / 1 italic / 2 oblique).
        var fontStyle = style.ReadKeywordOrDefault(PropertyId.FontStyle, defaultIndex: 0) switch
        {
            1 => FontStyle.Italic,
            2 => FontStyle.Oblique,
            _ => FontStyle.Normal,
        };

        // TODO(layout→pdf cycle): read the resolved font-family stack +
        // font-weight once FontFamilyListResolver / FontWeightResolver are
        // wired into PropertyResolverDispatch. Until then, the default
        // family + normal weight (the cascade returns neither yet).
        var family = _defaultFamily;
        var weight = DefaultWeightCss;

        var key = new ShaperKey(family, weight, fontStyle, sizePx);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var shaper = new HbShaper(ResolveFontBytes(family, weight, fontStyle), sizePx);
        _cache[key] = shaper;
        return shaper;
    }

    /// <summary>Resolve a family (with a generic fallback) to font bytes.
    /// The <see cref="IFontResolver"/> contract is async, but
    /// <see cref="IShaperResolver.Resolve"/> is synchronous; the default
    /// <see cref="SystemFontResolver"/> completes synchronously (sync file
    /// I/O via its cache), so the bridge below only blocks for a genuinely
    /// async resolver. A future async layout-prepass could pre-resolve
    /// faces to avoid the block.</summary>
    private ReadOnlyMemory<byte> ResolveFontBytes(string family, int weight, FontStyle style)
    {
        var face = ResolveFace(family, weight, style);
        if (face is null && !string.Equals(family, _defaultFamily, StringComparison.OrdinalIgnoreCase))
            face = ResolveFace(_defaultFamily, weight, style);
        if (face is null)
            throw new InvalidOperationException(
                $"HarfBuzzShaperResolver: no font resolved for family '{family}' "
                + $"(weight {weight}, {style}) nor the fallback '{_defaultFamily}'. "
                + "Supply an IFontResolver that resolves a face, or install system "
                + "fonts. (A bundled deterministic last-resort font is a follow-up TODO.)");
        return face.Bytes;
    }

    private FontFaceData? ResolveFace(string family, int weight, FontStyle style)
    {
        var query = new FontQuery
        {
            Family = family,
            WeightCss = weight,
            Style = style,
        };
        var pending = _fontResolver.ResolveAsync(query, CancellationToken.None);
        return pending.IsCompleted
            ? pending.GetAwaiter().GetResult()
            : pending.AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var shaper in _cache.Values) shaper.Dispose();
        _cache.Clear();
    }

    /// <summary>Cache key: a resolved-font signature + size. Two styles
    /// that map to the same (family, weight, style, size) share a shaper.</summary>
    private readonly record struct ShaperKey(
        string Family, int WeightCss, FontStyle Style, double FontSizePx);
}
