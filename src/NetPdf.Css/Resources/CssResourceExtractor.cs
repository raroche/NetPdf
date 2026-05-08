// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.Parser;

namespace NetPdf.Css.Resources;

/// <summary>
/// Per Phase D D-3 — finds every <c>url(...)</c> reference inside a
/// CSS declaration value, classifies it by property name, and emits
/// <see cref="CssResourceReference"/> records for downstream
/// <c>SafeResourceLoader</c> consumption (Phase 5).
///
/// <para><b>Why a dedicated extractor?</b> CSS has many resource sinks
/// scattered across properties + at-rules:
/// <list type="bullet">
///   <item><c>@import url(...)</c> + <c>@import "..."</c> (the bare
///   string form is also a URL).</item>
///   <item><c>@font-face src: url(...)</c> with optional
///   <c>format(...)</c> hint.</item>
///   <item><c>background-image: url(...)</c>,
///   <c>list-style-image: url(...)</c>,
///   <c>border-image-source: url(...)</c>,
///   <c>mask-image: url(...)</c>, <c>shape-outside: url(...)</c>
///   — image-class sinks.</item>
///   <item><c>cursor: url(cursor.png) 0 0, auto</c> — cursor sprite.</item>
///   <item><c>content: url(...)</c> on pseudo-elements.</item>
/// </list>
/// Phase 5 will route every extracted reference through
/// <c>SafeResourceLoader.FetchAsync</c>; until then, the extractor
/// gives test fixtures + the threat-model corpus a stable contract to
/// validate against.</para>
///
/// <para><b>What this is NOT.</b> Not a full CSS parser. Operates on
/// declaration value strings already produced by AngleSharp.Css /
/// <c>CssParserAdapter</c>. Handles the standard <c>url("...")</c> /
/// <c>url('...')</c> / <c>url(...)</c> shapes + skips inside CSS
/// strings ("..." / '...' as values, not URLs). Does NOT honor CSS
/// escapes inside the URL token (the loader sees the raw text and
/// decodes per the URL parser).</para>
/// </summary>
internal static class CssResourceExtractor
{
    /// <summary>Property names that name an image-class resource. Keep
    /// in sync with the property registry's <c>PropertyType.Url</c>
    /// surface.</summary>
    private static readonly HashSet<string> ImageProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "background-image",
        "border-image-source",
        "list-style-image",
        "mask-image",
        "shape-outside",
        // -webkit-* etc are out of scope (vendor-prefix dropped earlier
        // in the pipeline).
    };

    /// <summary>Extract every <c>url(...)</c> reference from
    /// <paramref name="declarationValue"/> tagged with the
    /// <see cref="CssResourceKind"/> appropriate for
    /// <paramref name="propertyName"/>. Empty when the property doesn't
    /// host URL references or the value has none.</summary>
    public static IReadOnlyList<CssResourceReference> ExtractFromDeclaration(
        string propertyName, string declarationValue, CssSourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(declarationValue);
        var kind = ClassifyProperty(propertyName);
        if (kind is null) return Array.Empty<CssResourceReference>();
        var urls = EnumerateUrls(declarationValue);
        if (urls.Count == 0) return Array.Empty<CssResourceReference>();
        var results = new List<CssResourceReference>(urls.Count);
        foreach (var url in urls)
        {
            results.Add(new CssResourceReference(url, kind.Value, location));
        }
        return results;
    }

    /// <summary>Extract <c>@import</c> URL — both <c>@import url(...)</c>
    /// + the bare <c>@import "..."</c> string form.</summary>
    public static CssResourceReference? ExtractFromImport(string importPrelude, CssSourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(importPrelude);
        var trimmed = importPrelude.TrimStart();
        if (trimmed.Length == 0) return null;
        // url(...) form.
        var urls = EnumerateUrls(trimmed);
        if (urls.Count > 0)
        {
            return new CssResourceReference(urls[0], CssResourceKind.Stylesheet, location);
        }
        // Bare string form: "..." or '...'.
        if (trimmed[0] == '"' || trimmed[0] == '\'')
        {
            var quote = trimmed[0];
            var end = trimmed.IndexOf(quote, 1);
            if (end > 1)
            {
                return new CssResourceReference(trimmed[1..end], CssResourceKind.Stylesheet, location);
            }
        }
        return null;
    }

    /// <summary>Classify a property name to its
    /// <see cref="CssResourceKind"/>. Returns null when the property
    /// doesn't host URL references.</summary>
    public static CssResourceKind? ClassifyProperty(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return null;
        var lower = propertyName.ToLowerInvariant();
        if (lower == "cursor") return CssResourceKind.Cursor;
        if (lower == "content") return CssResourceKind.Content;
        if (ImageProperties.Contains(lower)) return CssResourceKind.Image;
        // Shorthand `background: url(...)` resolves to background-image
        // by AngleSharp.Css's longhand expansion before we see it; treat
        // it as image just in case the expansion breaks.
        if (lower == "background") return CssResourceKind.Image;
        return null;
    }

    /// <summary>Walk <paramref name="value"/> + collect each
    /// <c>url(...)</c> token's URL text. Quote-aware (skips
    /// CSS strings) so <c>content: "url(notafetch)"</c> doesn't yield
    /// a fake URL. Handles bare + quoted url tokens. Does not decode
    /// CSS escapes — the URL parser at the loader does that.</summary>
    public static IReadOnlyList<string> EnumerateUrls(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length < 5) return Array.Empty<string>(); // shortest "url()"
        var results = new List<string>(2);
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            // Skip CSS string literals.
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < value.Length && value[i] != quote)
                {
                    if (value[i] == '\\' && i + 1 < value.Length) i += 2;
                    else i++;
                }
                if (i < value.Length) i++;
                continue;
            }
            // Match url( case-insensitively.
            if (i + 4 <= value.Length
                && (value[i] == 'u' || value[i] == 'U')
                && (value[i + 1] == 'r' || value[i + 1] == 'R')
                && (value[i + 2] == 'l' || value[i + 2] == 'L')
                && value[i + 3] == '(')
            {
                // Token boundary check — preceded by start-of-string /
                // whitespace / comma / operator, not by an ident-continue
                // that would make this a different function (`myurl(...)`).
                if (i > 0 && IsIdentContinue(value[i - 1]))
                {
                    i++;
                    continue;
                }
                var bodyStart = i + 4;
                var bodyEnd = FindMatchingCloseParen(value, bodyStart);
                if (bodyEnd < 0) break;
                var url = ExtractUrlBody(value, bodyStart, bodyEnd);
                if (!string.IsNullOrEmpty(url)) results.Add(url);
                i = bodyEnd + 1;
                continue;
            }
            i++;
        }
        return results;
    }

    private static int FindMatchingCloseParen(string value, int start)
    {
        var depth = 1;
        var i = start;
        while (i < value.Length)
        {
            var c = value[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < value.Length && value[i] != quote)
                {
                    if (value[i] == '\\' && i + 1 < value.Length) i += 2;
                    else i++;
                }
                if (i < value.Length) i++;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
            i++;
        }
        return -1;
    }

    private static string ExtractUrlBody(string value, int bodyStart, int bodyEnd)
    {
        var i = bodyStart;
        while (i < bodyEnd && IsCssWhitespace(value[i])) i++;
        if (i >= bodyEnd) return string.Empty;
        // Quoted form: "..." or '...'.
        if (value[i] == '"' || value[i] == '\'')
        {
            var quote = value[i];
            i++;
            var start = i;
            while (i < bodyEnd && value[i] != quote)
            {
                if (value[i] == '\\' && i + 1 < bodyEnd) i += 2;
                else i++;
            }
            return value[start..i];
        }
        // Bare form — read until trailing whitespace.
        var bareStart = i;
        var bareEnd = bodyEnd;
        while (bareEnd > bareStart && IsCssWhitespace(value[bareEnd - 1])) bareEnd--;
        return value[bareStart..bareEnd];
    }

    private static bool IsCssWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f';

    private static bool IsIdentContinue(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9') || c == '-' || c == '_';
}
