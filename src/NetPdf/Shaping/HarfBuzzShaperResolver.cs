// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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
/// <para><b>Reads the resolved cascade.</b> As of Phase 5 layout→PDF
/// cycle 4 the CSS font properties resolve in the cascade, so this
/// resolver reads them live through the normal style readers:</para>
/// <list type="bullet">
///   <item><b>font-size</b> — read via
///   <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrDefault"/>
///   (<c>FontSizeResolver</c>). A resolved <c>0</c> is honored as
///   zero-advance text; the configured default applies only when the
///   cascade left no usable value.</item>
///   <item><b>font-family</b> — read via
///   <see cref="FontReaders.ReadFontFamily"/> as a prioritized stack
///   (<c>FontFamilyListResolver</c>); <see cref="ResolveFontBytes"/>
///   walks it in author order, then the configured generic default as a
///   last resort.</item>
///   <item><b>font-weight</b> — read via
///   <see cref="FontReaders.ReadFontWeight"/> (<c>FontWeightResolver</c>).</item>
///   <item><b>font-style</b> — read live (normal / italic / oblique).</item>
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
    /// fallback applied only when the cascade leaves <c>font-size</c>
    /// unresolved (<c>FontSizeResolver</c> resolves real sizes, including
    /// a deliberate <c>0</c>).</summary>
    public const double DefaultFontSizePx = 16.0;

    private readonly IFontResolver _fontResolver;
    private readonly string _defaultFamily;
    private readonly double _defaultFontSizePx;
    private readonly Dictionary<ShaperKey, HbShaper> _cache = new();
    // Resolved font PROGRAM cache, keyed size-independently (family stack + weight + style).
    // Shared by Resolve (layout shaping) AND ResolveFontProgram (paint subsetting) so a
    // stateful / CDN IFontResolver that returns DIFFERENT bytes on a later call can't make the
    // painter subset a different program than layout shaped — the program is resolved ONCE per
    // query and reused (post-PR-#127 review P1). The program identity is a content hash of the
    // validated bytes, so distinct family stacks that fall back to the SAME face share one
    // embedded subset (review P3).
    private readonly Dictionary<ProgramKey, ResolvedFontProgram> _programCache = new();
    // Per post-PR-#117 review P2 — guards the cache + disposal so a shared
    // resolver is safe across parallel layout work (HbShaper.Shape itself
    // is concurrency-safe).
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Construct a resolver over a font source.</summary>
    /// <param name="fontResolver">Resolves a <see cref="FontQuery"/> to
    /// font bytes; e.g. <see cref="SystemFontResolver"/> or a caller's
    /// <see cref="HtmlPdfOptions.FontResolver"/>.</param>
    /// <param name="defaultFamily">The last-resort family tried after the
    /// author <c>font-family</c> stack is exhausted (a generic like
    /// <c>sans-serif</c> the resolver maps to a real face).</param>
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
        // font-size: honored live via the standard reader. CSS Fonts 4 §3.4 allows
        // font-size in [0, ∞], so a resolved 0 stays 0 — HbShaper shapes it to
        // zero-advance (invisible) glyphs. Only a NEGATIVE or non-finite size (which
        // the cascade should already have rejected) falls back to the default.
        var sizePx = style.ReadLengthPxOrDefault(PropertyId.FontSize, _defaultFontSizePx);
        if (sizePx < 0 || !double.IsFinite(sizePx)) sizePx = _defaultFontSizePx;

        // font-style: live keyword (0 normal / 1 italic / 2 oblique).
        var fontStyle = style.ReadKeywordOrDefault(PropertyId.FontStyle, defaultIndex: 0) switch
        {
            1 => FontStyle.Italic,
            2 => FontStyle.Oblique,
            _ => FontStyle.Normal,
        };

        // Per Phase 5 layout→PDF cycle 4 — read the resolved font-family STACK +
        // weight (FontFamilyListResolver / FontWeightResolver are wired). The family
        // resolver yields a prioritized list; ResolveFontBytes walks it in author
        // order (CSS Fonts 4 §2.1) and falls back to `_defaultFamily` last. An
        // element with no author font-family inherits the CSS initial (`serif`).
        var families = style.ReadFontFamily().Families;
        var weight = style.ReadFontWeight();

        // Per post-PR-#117 review — CSS family names match case-insensitively
        // (CSS Fonts L4 §4.1), so the cache key normalizes the stack to lower case
        // to avoid duplicate shapers (e.g. `Arial` vs `arial`). The original-case
        // entries are still used for the resolver query + error messages.
        var key = new ShaperKey(
            FamilyStackKey(families), weight, fontStyle, sizePx);

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
            // Resolve the program bytes through the shared cache so the painter later subsets
            // the EXACT bytes this HbShaper shaped (post-PR-#127 review P1).
            var program = ResolveProgramCachedLocked(families, weight, fontStyle);
            var shaper = new HbShaper(program.Bytes, sizePx);
            _cache[key] = shaper;
            return shaper;
        }
    }

    /// <summary>
    /// Resolve the VALIDATED font-program bytes for a style — the SAME bytes
    /// <see cref="Resolve"/> hands HarfBuzz (both read the shared program cache) — together with
    /// a stable content <see cref="ResolvedFontProgram.Identity"/> for that program. The result
    /// is cached size-INDEPENDENTLY (a font program is identical at every size; the subset /
    /// embedded font is shared across sizes and the size is applied via the content stream's
    /// <c>Tf</c> operand). The text painter calls this AFTER layout to subset the EXACT program
    /// layout shaped, so the shaped (original) glyph ids index the same font.
    /// Throws — exactly like the shaping path (<see cref="ResolveFontBytes"/>) — when no font
    /// resolves or the bytes are unsafe / WOFF-wrapped; the painter catches it and skips +
    /// diagnoses that run rather than failing the whole render.
    /// </summary>
    internal ResolvedFontProgram ResolveFontProgram(ComputedStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        // Mirror Resolve's family / weight / style reads. font-size is deliberately NOT read:
        // it doesn't affect the font program or its glyph ids, only the Tf scale at emit.
        var fontStyle = style.ReadKeywordOrDefault(PropertyId.FontStyle, defaultIndex: 0) switch
        {
            1 => FontStyle.Italic,
            2 => FontStyle.Oblique,
            _ => FontStyle.Normal,
        };
        var families = style.ReadFontFamily().Families;
        var weight = style.ReadFontWeight();

        // Read through the SAME program cache Resolve populates, under the same gate. If
        // layout already shaped this query, this returns the cached bytes (no second resolver
        // call) — so the painter can't drift to a different program than layout used.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ResolveProgramCachedLocked(families, weight, fontStyle);
        }
    }

    /// <summary>Get-or-resolve the font program (validated bytes + content-hash identity) for a
    /// query, cached size-independently. MUST be called holding <see cref="_gate"/>. The single
    /// resolution point shared by <see cref="Resolve"/> and <see cref="ResolveFontProgram"/>.</summary>
    private ResolvedFontProgram ResolveProgramCachedLocked(
        ImmutableArray<string> families, int weight, FontStyle style)
    {
        var key = new ProgramKey(FamilyStackKey(families), weight, style);
        if (_programCache.TryGetValue(key, out var existing)) return existing;

        var bytes = ResolveFontBytes(families, weight, style);
        // Identity = content hash of the validated program. Distinct family stacks that resolve
        // to the SAME face share one identity ⇒ one subset/embedded font (review P3).
        var identity = Convert.ToHexString(SHA256.HashData(bytes.Span));
        var program = new ResolvedFontProgram(identity, bytes);
        _programCache[key] = program;
        return program;
    }

    /// <summary>Walk the resolved font-family stack (author order, CSS Fonts 4
    /// §2.1) to VALIDATED font bytes ready for HarfBuzz: the first entry that
    /// resolves to a face wins; the configured generic default is tried last.</summary>
    private ReadOnlyMemory<byte> ResolveFontBytes(ImmutableArray<string> families, int weight, FontStyle style)
    {
        // Walk the author stack in priority order — the first family that resolves
        // to a face wins (e.g. `MissingFont, Arial, sans-serif` falls through to
        // Arial). A not-found family is a fall-back case, not an error.
        FontFaceData? face = null;
        string? resolvedFrom = null;
        if (!families.IsDefaultOrEmpty)
        {
            for (var i = 0; i < families.Length && face is null; i++)
            {
                face = ResolveFace(families[i], weight, style);
                if (face is not null) resolvedFrom = families[i];
            }
        }

        // Last resort: the configured generic default, unless the stack already tried it.
        var triedDefault = false;
        if (face is null && !StackContainsDefault(families))
        {
            triedDefault = true;
            face = ResolveFace(_defaultFamily, weight, style);
            if (face is not null) resolvedFrom = _defaultFamily;
        }

        if (face is null)
        {
            // Per post-PR-#117 review — only mention the fallback when one was
            // actually attempted (it isn't when the stack already includes the default).
            var requested = families.IsDefaultOrEmpty
                ? $"'{_defaultFamily}'"
                : $"the font-family stack [{string.Join(", ", families)}]";
            var fallbackNote = triedDefault ? $" nor the fallback '{_defaultFamily}'" : string.Empty;
            throw new FontResolutionException(
                $"HarfBuzzShaperResolver: no font resolved for {requested}{fallbackNote} "
                + $"(weight {weight}, {style}). Supply an IFontResolver that resolves a "
                + "face, or install system fonts. (A bundled deterministic last-resort "
                + "font is a follow-up TODO.)");
        }

        // Per post-PR-#117 review P1 — gate the resolved bytes through the
        // SAME pre-decode safety validator FontFace.Load uses, BEFORE
        // handing them to HarfBuzz. A custom / CDN IFontResolver is
        // untrusted: garbage, oversized, or WOFF/WOFF2-wrapped bytes would
        // otherwise reach the native shaper. A resolved-but-unsafe/wrapped
        // face is an ERROR (not a fall-back-to-another-font case) — mirror
        // FontFace.Load + throw a clear message.
        var verdict = FontSafetyValidator.Validate(face.Bytes.Span);
        if (!verdict.IsSafe)
            throw new FontResolutionException(
                $"HarfBuzzShaperResolver: the font resolved for '{resolvedFrom}' was rejected "
                + $"by the pre-decode safety validator: {verdict.Reason}");
        if (verdict.DetectedFormat is FontSafetyValidator.FontFormat.Woff
            or FontSafetyValidator.FontFormat.Woff2)
            throw new FontResolutionException(
                $"HarfBuzzShaperResolver: the font resolved for '{resolvedFrom}' is in "
                + $"{verdict.DetectedFormat} format; NetPdf cannot decode the wrapped "
                + "sfnt yet — supply an unwrapped TTF/OTF.");
        return face.Bytes;
    }

    /// <summary>Case-insensitive membership test for the generic default in the
    /// resolved stack (so it isn't queried twice).</summary>
    private bool StackContainsDefault(ImmutableArray<string> families)
    {
        if (families.IsDefaultOrEmpty) return false;
        for (var i = 0; i < families.Length; i++)
            if (string.Equals(families[i], _defaultFamily, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Cache-key signature for a resolved family stack: the entries
    /// lower-cased (CSS families match case-insensitively, CSS Fonts 4 §4.1) and
    /// joined. Identical author stacks share one shaper. Each entry is LENGTH-PREFIXED
    /// so the join stays unambiguous even when a decoded family name contains the
    /// separator — a CSS escape can put any char (incl. a newline) in a name
    /// (post-PR-#121 review P2).</summary>
    private static string FamilyStackKey(ImmutableArray<string> families)
    {
        if (families.IsDefaultOrEmpty) return "serif";
        if (families.Length == 1) return families[0].ToLowerInvariant();
        var sb = new StringBuilder();
        for (var i = 0; i < families.Length; i++)
        {
            var f = families[i].ToLowerInvariant();
            sb.Append(f.Length).Append(':').Append(f);
        }
        return sb.ToString();
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
            _programCache.Clear();
        }
    }

    /// <summary>Cache key: a resolved-font signature + size. Two styles
    /// that map to the same (family stack, weight, style, size) share a shaper.
    /// <c>Family</c> holds the whole resolved family stack, case-normalized +
    /// joined by <see cref="FamilyStackKey"/> (CSS families match
    /// case-insensitively), so `Arial` and `arial` share one entry.
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

    /// <summary>Size-independent key for the resolved-program cache: the family stack
    /// (case-normalized join via <see cref="FamilyStackKey"/>) + weight + style. A font program
    /// is identical at every size, so size is deliberately excluded.</summary>
    private readonly record struct ProgramKey(string Family, int WeightCss, FontStyle Style);

    /// <summary>A resolved font program for the text painter: a stable content
    /// <paramref name="Identity"/> (a hash of the validated bytes) plus the VALIDATED sfnt
    /// <paramref name="Bytes"/> layout shaped. Same <paramref name="Identity"/> ⇒ same program ⇒
    /// one shared subset + embedded font across every run that uses it — even across distinct
    /// family stacks that fall back to the same face.</summary>
    internal readonly record struct ResolvedFontProgram(string Identity, ReadOnlyMemory<byte> Bytes);
}
