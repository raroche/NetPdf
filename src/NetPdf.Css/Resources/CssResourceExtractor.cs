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

    /// <summary>Per PR #18 review #3 — extract URLs from an
    /// <c>@font-face</c> rule's <c>src</c> declaration. <c>src</c> in
    /// other contexts is not URL-bearing, so the regular
    /// <see cref="ExtractFromDeclaration"/> path doesn't classify it —
    /// callers walking <c>@font-face</c> AST nodes use this method to
    /// emit <see cref="CssResourceKind.Font"/> references.
    ///
    /// <para>The CSS Fonts L4 §3.1 grammar for <c>@font-face src</c> is
    /// a comma-separated list where each entry is either
    /// <c>url(URL) format(...)</c> (with an optional format hint) or
    /// <c>local(...)</c> (system-font reference, NOT a fetch sink).
    /// This method extracts every <c>url()</c> + drops the
    /// <c>format()</c> hint + ignores <c>local()</c>. Multiple <c>url()</c>
    /// entries are kept in source order (the loader picks the first
    /// successful fetch per fallback semantics).</para>
    ///
    /// <para>This is the attack surface that drove DomPDF RCE-style
    /// remote-font issues — without explicit extraction, a Phase 5
    /// font loader could miss <c>@font-face { src: url(...) }</c>
    /// entirely.</para>
    /// </summary>
    public static IReadOnlyList<CssResourceReference> ExtractFromFontFaceSrc(
        string srcValue, CssSourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(srcValue);
        // Reuse EnumerateUrls — it already handles url() with quote-aware
        // string skipping. local(...) entries don't begin with url(, so
        // they don't match the prefix; format() hints sit between
        // url() entries + don't match either.
        var urls = EnumerateUrls(srcValue);
        if (urls.Count == 0) return Array.Empty<CssResourceReference>();
        var results = new List<CssResourceReference>(urls.Count);
        foreach (var url in urls)
        {
            results.Add(new CssResourceReference(url, CssResourceKind.Font, location));
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
    /// <c>url(...)</c> token's URL text PLUS every string-form URL inside
    /// resource-bearing functions (<c>image-set()</c>, <c>cross-fade()</c>,
    /// <c>image()</c>). Quote-aware (skips CSS strings OUTSIDE those
    /// functions) so <c>content: "url(notafetch)"</c> doesn't yield a
    /// fake URL, but <c>background-image: image-set("a.png" 1x)</c> still
    /// extracts <c>"a.png"</c>. Handles bare + quoted url tokens. Does
    /// not decode CSS escapes — the URL parser at the loader does that.
    ///
    /// <para><b>Per PR #18 review #8 — function-aware string
    /// extraction.</b> CSS Images L4 §6 specifies <c>image-set()</c> +
    /// <c>cross-fade()</c> + <c>image()</c> as resource-list functions
    /// that accept either <c>url(...)</c> tokens OR string literals
    /// (<c>"path"</c>) as URL entries. The pre-fix string-skip logic
    /// missed every string-form URL inside these functions, leaving an
    /// extractor-bypass class:
    /// <c>background-image: image-set("https://attacker/a.png" 1x)</c>
    /// would not be reported. The fix tracks whether we're inside one
    /// of these functions + treats string literals there as URLs.</para>
    ///
    /// <para><b>Per post-Task-7 review — nested non-resource functions.</b>
    /// CSS Images L4 §6 also allows resource lists to contain
    /// <i>non-resource</i> helper functions like <c>type("image/png")</c>
    /// or <c>format("avif")</c>. Pre-fix the parser:
    /// <list type="bullet">
    ///   <item>Treated string args of those helpers as URLs (so
    ///   <c>image-set("a.png" 1x, type("image/png"))</c> would emit
    ///   <c>"image/png"</c> as a phantom URL).</item>
    ///   <item>Decremented the resource-fn depth when seeing the
    ///   helper's closing <c>)</c>, prematurely exiting the
    ///   <c>image-set</c> context (so legitimate strings AFTER the
    ///   helper would not be captured).</item>
    /// </list>
    /// The fix tracks an inner non-resource paren depth separately
    /// from the resource-fn depth: only strings at the IMMEDIATE
    /// level of a resource fn (innerNonResourceDepth == 0) count as
    /// URLs, and the closing <c>)</c> of a helper decrements the
    /// inner depth, leaving the resource-fn depth intact. Nested
    /// resource fns (<c>image-set(image-set("nested.png" 1x) 2x)</c>)
    /// are unaffected — the inner image-set still goes through
    /// <see cref="TryMatchResourceFunctionStart"/>.</para>
    /// </summary>
    public static IReadOnlyList<string> EnumerateUrls(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length < 5) return Array.Empty<string>(); // shortest "url()"
        var results = new List<string>(2);
        // Track resource-function nesting depth. > 0 means we're inside
        // image-set() / cross-fade() / image() and string literals are
        // URL-bearing rather than skip-only.
        var resourceFnDepth = 0;
        // Per post-Task-7 review — track non-resource paren nesting
        // INSIDE the current resource fn body. While > 0 we're inside
        // a helper like type() or format(); strings are NOT URLs
        // and the helper's closing ) decrements this counter, NOT
        // the resource-fn depth.
        var innerNonResourceDepth = 0;
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            // CSS string literal handling — when inside a resource fn
            // AT THE IMMEDIATE LEVEL (not nested in a helper), capture
            // it as a URL; otherwise skip it entirely.
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                var stringStart = i;
                while (i < value.Length && value[i] != quote)
                {
                    if (value[i] == '\\' && i + 1 < value.Length) i += 2;
                    else i++;
                }
                if (resourceFnDepth > 0
                    && innerNonResourceDepth == 0
                    && i <= value.Length)
                {
                    var url = value[stringStart..Math.Min(i, value.Length)];
                    if (!string.IsNullOrEmpty(url)) results.Add(url);
                }
                if (i < value.Length) i++;
                continue;
            }
            // Match url( case-insensitively. url() is captured regardless
            // of whether we're inside a helper (its semantics are
            // unambiguous + matches the CSS Images L4 §6 grammar).
            if (TryMatchKeywordOpenParen(value, i, "url"))
            {
                if (i > 0 && IsIdentContinue(value[i - 1])) { i++; continue; }
                var bodyStart = i + 4;
                var bodyEnd = FindMatchingCloseParen(value, bodyStart);
                if (bodyEnd < 0) break;
                var url = ExtractUrlBody(value, bodyStart, bodyEnd);
                if (!string.IsNullOrEmpty(url)) results.Add(url);
                i = bodyEnd + 1;
                continue;
            }
            // Per PR #18 review #8 — track entry / exit of the three
            // resource-list functions. We don't recurse into a separate
            // routine because url() inside these functions still needs
            // the same regular extraction; only string literals get
            // additional treatment.
            if (TryMatchResourceFunctionStart(value, i, out var consumed))
            {
                resourceFnDepth++;
                // Reset inner-non-resource depth — the new resource-fn
                // body starts fresh at depth 0.
                // (Nested non-resource depth is per-resource-fn — when
                // we re-enter the outer fn after the inner closes, the
                // outer's inner depth is whatever it was before. We
                // track this implicitly via the close-paren branch
                // below, which decrements innerNonResourceDepth before
                // resourceFnDepth.)
                i += consumed;
                continue;
            }
            // Inside a resource fn: track nested non-resource ( and ).
            if (resourceFnDepth > 0)
            {
                if (c == '(')
                {
                    // Some non-resource function opens. Could be type(),
                    // format(), calc(), etc. Track its nesting so its
                    // strings + closing ) don't pollute the outer
                    // resource-fn state.
                    innerNonResourceDepth++;
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    if (innerNonResourceDepth > 0)
                    {
                        // Closing a helper inside the resource fn.
                        // Resource-fn depth STAYS — we're still
                        // collecting from its body.
                        innerNonResourceDepth--;
                    }
                    else
                    {
                        // Closing the resource fn itself.
                        resourceFnDepth--;
                    }
                    i++;
                    continue;
                }
            }
            i++;
        }
        return results;
    }

    /// <summary>Per PR #18 review #8 — true when <paramref name="value"/>
    /// at <paramref name="pos"/> begins one of the resource-list
    /// functions: <c>image-set(</c>, <c>cross-fade(</c>, or <c>image(</c>.
    /// Sets <paramref name="consumed"/> to the number of chars that
    /// matched (function name + opening paren) on success.</summary>
    private static bool TryMatchResourceFunctionStart(string value, int pos, out int consumed)
    {
        consumed = 0;
        // image-set( — 10 chars
        if (TryMatchKeywordOpenParen(value, pos, "image-set"))
        {
            if (pos > 0 && IsIdentContinue(value[pos - 1])) return false;
            consumed = "image-set(".Length;
            return true;
        }
        // cross-fade(
        if (TryMatchKeywordOpenParen(value, pos, "cross-fade"))
        {
            if (pos > 0 && IsIdentContinue(value[pos - 1])) return false;
            consumed = "cross-fade(".Length;
            return true;
        }
        // image( — must NOT also match image-set; check image- first
        // and exit early. Actually image( matches "image(" — image-set(
        // would match image( as a prefix, so we need to disambiguate.
        // Fix: check the char AFTER "image" — if it's '-', it's
        // image-set/image-rendering/etc., not bare image(.
        if (pos + 6 <= value.Length
            && (value[pos] == 'i' || value[pos] == 'I')
            && (value[pos + 1] == 'm' || value[pos + 1] == 'M')
            && (value[pos + 2] == 'a' || value[pos + 2] == 'A')
            && (value[pos + 3] == 'g' || value[pos + 3] == 'G')
            && (value[pos + 4] == 'e' || value[pos + 4] == 'E')
            && value[pos + 5] == '(')
        {
            if (pos > 0 && IsIdentContinue(value[pos - 1])) return false;
            consumed = "image(".Length;
            return true;
        }
        return false;
    }

    /// <summary>Match <paramref name="keyword"/> followed by <c>(</c>
    /// at <paramref name="pos"/>, ASCII case-insensitive.</summary>
    private static bool TryMatchKeywordOpenParen(string value, int pos, string keyword)
    {
        var len = keyword.Length;
        if (pos + len + 1 > value.Length) return false;
        for (var k = 0; k < len; k++)
        {
            var a = value[pos + k];
            var b = keyword[k];
            if (a == b) continue;
            if (a is >= 'A' and <= 'Z') a = (char)(a + 32);
            if (b is >= 'A' and <= 'Z') b = (char)(b + 32);
            if (a != b) return false;
        }
        return value[pos + len] == '(';
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
