// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Resolves the page size declared by a bare <c>@page { size: … }</c> descriptor into px, per
/// CSS Paged Media L3 §3.3. Phase 3 Task 21 cycle 2.
/// </summary>
/// <remarks>
/// <para>
/// The <c>size</c> descriptor is dropped by AngleSharp.Css; the pre-pass
/// (<c>CssPreprocessor.ParsePageRule</c>) recovers it and the adapter re-attaches it as a
/// synthetic <c>size</c> declaration, which this resolver reads. Applicability + ordering use the
/// shared <see cref="AtPageRules.EnumerateBarePageRulesWithMediaInfo"/> (cascade-style media /
/// disabled filtering, bare <c>@page</c> only). Among contributing declarations the cascade
/// winner is chosen by importance then source order: an <c>!important</c> <c>size</c> beats a
/// normal one, and among equal importance the LAST wins. Per CSS Page 3 §3.3 a <c>size</c>
/// qualified by a paper-size <c>@media</c> (or sheet media query) — <c>width</c> / <c>height</c>
/// / <c>aspect-ratio</c> / <c>orientation</c> / device-* — is IGNORED (it would be a circular
/// page-size dependency); such rules are skipped here.
/// </para>
/// <para>
/// Supported value forms: a named size (<c>A5</c>/<c>A4</c>/<c>A3</c>/<c>B5</c>/<c>B4</c>/
/// <c>JIS-B5</c>/<c>JIS-B4</c>/<c>letter</c>/<c>legal</c>/<c>ledger</c>) optionally combined with
/// <c>portrait</c>/<c>landscape</c>; <c>portrait</c>/<c>landscape</c> alone (re-orients the
/// configured page size); one absolute <c>&lt;length&gt;</c> (square) or two (width height); and
/// <c>auto</c> (no override). An unrecognized / unsupported value resolves to <see langword="null"/>
/// so the caller keeps the configured page size.
/// </para>
/// </remarks>
internal static class AtPageSizeResolver
{
    /// <summary>A resolved page size in px.</summary>
    internal readonly record struct ResolvedPageSize(double WidthPx, double HeightPx);

    /// <summary>Resolve the effective bare-<c>@page</c> size. Returns <see langword="null"/> when
    /// no contributing rule sets a concrete size (none declared, or the winner is <c>auto</c>) —
    /// the caller then keeps its configured <c>PageSize</c>. <paramref name="media"/>'s viewport
    /// is the configured page size, used to re-orient a bare <c>portrait</c>/<c>landscape</c>.</summary>
    public static ResolvedPageSize? Resolve(IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        var seen = false;
        var important = false;
        ResolvedPageSize? dims = null;
        foreach (var bare in AtPageRules.EnumerateBarePageRulesWithMediaInfo(sheets, media))
        {
            // CSS Page 3 §3.3 — a `size` qualified by a paper-size @media (or sheet media query)
            // is ignored to avoid a circular page-size dependency. Margins from the same rule
            // still apply (the margin resolver doesn't consult this flag).
            if (bare.PaperSizeConditioned) continue;
            foreach (var decl in bare.Rule.Declarations)
            {
                if (!string.Equals(decl.Property, "size", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryResolveSize(decl.Value.RawText, media.ViewportWidthPx, media.ViewportHeightPx,
                        out var w, out var h, out var isAuto))
                    continue;

                var imp = decl.IsImportant;
                if (seen && important && !imp) continue; // a normal decl can't override a winning !important
                seen = true;
                important = imp;
                dims = isAuto ? null : new ResolvedPageSize(w, h);
            }
        }
        return dims;
    }

    /// <summary>Parse a <c>size</c> value to (width, height) px, or <paramref name="isAuto"/> for
    /// <c>auto</c>. Returns <see langword="false"/> for unrecognized / unsupported values.</summary>
    internal static bool TryResolveSize(
        string text, double pageWidthPx, double pageHeightPx,
        out double width, out double height, out bool isAuto)
    {
        width = 0; height = 0; isAuto = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        if (tokens.Length == 1 && tokens[0] == "auto") { isAuto = true; return true; }

        // Classify tokens: a named size, an orientation, and/or length(s). A repeated page-size
        // keyword (`A4 letter`) or orientation (`portrait landscape`) is invalid grammar per CSS
        // Page 3 §3.3 — reject it rather than silently letting the last token win.
        string? keyword = null, orientation = null;
        var lengths = new List<string>(2);
        var keywordCount = 0;
        var orientationCount = 0;
        foreach (var t in tokens)
        {
            if (t is "portrait" or "landscape") { orientation = t; orientationCount++; }
            else if (NamedSizePx(t) is not null) { keyword = t; keywordCount++; }
            else lengths.Add(t);
        }
        if (keywordCount > 1 || orientationCount > 1) return false;

        // Named size, optionally re-oriented.
        if (keyword is not null && lengths.Count == 0)
        {
            var (kw, kh) = NamedSizePx(keyword)!.Value;
            if (orientation is null) { width = kw; height = kh; }   // keyword's intrinsic (portrait)
            else Orient(kw, kh, orientation, out width, out height);
            return true;
        }

        // Orientation alone → re-orient the configured page size.
        if (keyword is null && orientation is not null && lengths.Count == 0)
        {
            Orient(pageWidthPx, pageHeightPx, orientation, out width, out height);
            return true;
        }

        // One absolute length (square) or two (width height). Not combinable with keyword/orientation.
        if (keyword is null && orientation is null && lengths.Count is 1 or 2)
        {
            if (!AtPageMarginResolver.TryParseAbsoluteLengthPx(lengths[0], out var l1) || l1 <= 0) return false;
            if (lengths.Count == 1) { width = l1; height = l1; return true; }
            if (!AtPageMarginResolver.TryParseAbsoluteLengthPx(lengths[1], out var l2) || l2 <= 0) return false;
            width = l1; height = l2; return true;
        }

        return false; // unrecognized combination
    }

    private static void Orient(double a, double b, string orientation, out double width, out double height)
    {
        var min = Math.Min(a, b);
        var max = Math.Max(a, b);
        if (orientation == "landscape") { width = max; height = min; }
        else { width = min; height = max; }
    }

    /// <summary>The named CSS page sizes (CSS Page 3 §3.3), in PORTRAIT px at 96 dpi.</summary>
    private static (double W, double H)? NamedSizePx(string keyword) => keyword switch
    {
        "a5" => Mm(148, 210),
        "a4" => Mm(210, 297),
        "a3" => Mm(297, 420),
        "b5" => Mm(176, 250),
        "b4" => Mm(250, 353),
        "jis-b5" => Mm(182, 257),
        "jis-b4" => Mm(257, 364),
        "letter" => Inch(8.5, 11),
        "legal" => Inch(8.5, 14),
        "ledger" => Inch(11, 17),
        _ => null,
    };

    private static (double, double) Mm(double w, double h) => (w * 96.0 / 25.4, h * 96.0 / 25.4);
    private static (double, double) Inch(double w, double h) => (w * 96.0, h * 96.0);
}
