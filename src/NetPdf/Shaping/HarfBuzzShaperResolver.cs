// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Text.Fonts;
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
/// same <see cref="ComputedStyle"/> yields the same instance. The cache
/// + disposal are lock-guarded, so a single resolver is safe to share
/// across parallel layout work (post-PR-#117 review P2).</para>
///
/// <para><b>Untrusted bytes.</b> An <see cref="IFontResolver"/> may be a
/// custom / CDN source, so resolved bytes are gated through
/// <c>FontSafetyValidator</c> (rejecting garbage / oversized / WOFF /
/// WOFF2) BEFORE HarfBuzz, exactly as <c>FontFace.Load</c> does
/// (post-PR-#117 review P1). And because shaping is synchronous, an
/// async resolver that doesn't complete synchronously fails fast rather
/// than blocking.</para>
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
    // Per post-PR-#117 review P2 — guards the cache + disposal so a shared
    // resolver is safe across parallel layout work (HbShaper.Shape itself
    // is concurrency-safe).
    private readonly object _gate = new();
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

        // Pure style reads (no shared state) — done outside the lock.
        //
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

        // Per post-PR-#117 review P2 — serialize the get-or-create + the
        // disposed check so concurrent Resolve calls (a shared resolver
        // across parallel layout) can't corrupt the cache or race Dispose.
        // Font resolution + HbShaper construction happen under the lock so
        // each (font, size) shaper is created EXACTLY once + owned for
        // disposal. The lock is brief for a synchronous IFontResolver.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var shaper = new HbShaper(ResolveFontBytes(family, weight, fontStyle), sizePx);
            _cache[key] = shaper;
            return shaper;
        }
    }

    /// <summary>Resolve a family (with a generic fallback) to VALIDATED
    /// font bytes ready for HarfBuzz.</summary>
    private ReadOnlyMemory<byte> ResolveFontBytes(string family, int weight, FontStyle style)
    {
        // A family the resolver can't find at all → fall back to the
        // default generic family (a not-found family is a fall-back case).
        var face = ResolveFace(family, weight, style);
        if (face is null && !string.Equals(family, _defaultFamily, StringComparison.OrdinalIgnoreCase))
            face = ResolveFace(_defaultFamily, weight, style);
        if (face is null)
            throw new InvalidOperationException(
                $"HarfBuzzShaperResolver: no font resolved for family '{family}' "
                + $"(weight {weight}, {style}) nor the fallback '{_defaultFamily}'. "
                + "Supply an IFontResolver that resolves a face, or install system "
                + "fonts. (A bundled deterministic last-resort font is a follow-up TODO.)");

        // Per post-PR-#117 review P1 — gate the resolved bytes through the
        // SAME pre-decode safety validator FontFace.Load uses, BEFORE
        // handing them to HarfBuzz. A custom / CDN IFontResolver is
        // untrusted: garbage, oversized, or WOFF/WOFF2-wrapped bytes would
        // otherwise reach the native shaper. A resolved-but-unsafe/wrapped
        // face is an ERROR (not a fall-back-to-another-font case) — mirror
        // FontFace.Load + throw a clear message.
        var verdict = FontSafetyValidator.Validate(face.Bytes.Span);
        if (!verdict.IsSafe)
            throw new InvalidOperationException(
                $"HarfBuzzShaperResolver: the font resolved for '{family}' was rejected "
                + $"by the pre-decode safety validator: {verdict.Reason}");
        if (verdict.DetectedFormat is FontSafetyValidator.FontFormat.Woff
            or FontSafetyValidator.FontFormat.Woff2)
            throw new InvalidOperationException(
                $"HarfBuzzShaperResolver: the font resolved for '{family}' is in "
                + $"{verdict.DetectedFormat} format; NetPdf cannot decode the wrapped "
                + "sfnt yet — supply an unwrapped TTF/OTF.");
        return face.Bytes;
    }

    /// <summary>Resolve a single family to a face. Per post-PR-#117 review
    /// P1, layout shaping is SYNCHRONOUS, so this must NOT block on an
    /// arbitrary async resolver (a CDN-fetching <see cref="IFontResolver"/>
    /// could hang/deadlock + ignore cancellation): it requires synchronous
    /// completion + FAILS FAST otherwise. The default
    /// <see cref="SystemFontResolver"/> completes synchronously (sync file
    /// I/O via its cache). Async font pre-resolution (a layout pre-pass
    /// that warms the cache off-thread) is a documented follow-up.</summary>
    private FontFaceData? ResolveFace(string family, int weight, FontStyle style)
    {
        var query = new FontQuery
        {
            Family = family,
            WeightCss = weight,
            Style = style,
        };
        var pending = _fontResolver.ResolveAsync(query, CancellationToken.None);
        if (!pending.IsCompleted)
            throw new NotSupportedException(
                "HarfBuzzShaperResolver: the IFontResolver did not complete "
                + "synchronously. Layout shaping is synchronous and must not block on "
                + "an async (e.g. CDN-fetching) resolver. Use a resolver that completes "
                + "synchronously (e.g. SystemFontResolver) or pre-warm fonts before "
                + "layout. (Async font pre-resolution is a documented follow-up.)");
        return pending.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Per post-PR-#117 review P2 — dispose under the same lock that
        // guards Resolve, so a concurrent Resolve can't hand out (or add) a
        // shaper while disposal is tearing the cache down.
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var shaper in _cache.Values) shaper.Dispose();
            _cache.Clear();
        }
    }

    /// <summary>Cache key: a resolved-font signature + size. Two styles
    /// that map to the same (family, weight, style, size) share a shaper.
    ///
    /// <para>TODO (post-PR-#117 review P3 — perf follow-up): the key
    /// includes size, and each <see cref="HbShaper"/> copies + pins the
    /// full font bytes, so a document with many computed sizes duplicates
    /// the same font payload. A future optimization is a blob cache keyed
    /// by VALIDATED font identity (so the bytes are pinned once) plus
    /// size-specific HarfBuzz Font objects over the shared blob. Not a
    /// correctness issue — tracked in
    /// <c>docs/deferrals.md#layout-to-pdf-pipeline</c>.</para></summary>
    private readonly record struct ShaperKey(
        string Family, int WeightCss, FontStyle Style, double FontSizePx);
}
