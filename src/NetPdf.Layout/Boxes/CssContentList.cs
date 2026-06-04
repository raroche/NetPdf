// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Task 14 cycle 1 — parses a CSS <c>content</c> property value (per CSS
/// Generated Content L3 §1.3) into a single concatenated text string. Extends
/// <see cref="CssStringParser"/>'s single-string acceptance to handle the
/// shapes that compose without external state:
/// <list type="bullet">
///   <item>One or more <c>&lt;string&gt;</c> tokens, separated by whitespace —
///     concatenated in source order. Example: <c>"prefix " "suffix"</c>.</item>
///   <item><c>attr(<i>name</i>)</c> — substituted with the host element's
///     attribute value (case-insensitive name match per HTML5; missing
///     attribute → empty string). Per Task 14 review Rec 4, the modern
///     multi-arg form <c>attr(<i>name</i> <i>type</i>?, <i>fallback</i>?)</c>
///     (CSS Values L4 §10) is REJECTED — the parse fails cleanly rather
///     than silently dropping the type / fallback args and treating as
///     bare <c>attr(name)</c>. Cycle 2 will deliver the typed-value
///     pipeline + fallback handling.</item>
///   <item>Mixtures of strings + <c>attr()</c>: <c>"Item " attr(data-name)</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Returns <see langword="false"/> when the value contains tokens this parser
/// doesn't yet handle:
/// <c>counter(<i>name</i>)</c> for a non-page name + <c>counters()</c> (need the
/// counter-reset / counter-increment machinery) — though <c>counter(page)</c> /
/// <c>counter(pages)</c> ARE resolved when a <see cref="PageCounters"/> context is
/// supplied (Task 21 cycle 9 — page-margin-box content), <c>url()</c> / <c>image()</c> / <c>image-set()</c>
/// (needs the resource pipeline), <c>open-quote</c> / <c>close-quote</c> /
/// <c>no-open-quote</c> / <c>no-close-quote</c> (needs the quotation stack with
/// depth tracking + the <c>quotes</c> property).
/// </para>
/// <para>
/// <b>Task 16 cycle 1 — diagnostic emission.</b> The
/// <see cref="TryParse(string, IElement, ICssDiagnosticsSink?, CssSourceLocation, out string, PageCounters?)"/>
/// overload emits one of two diagnostic codes when the parse fails:
/// <see cref="CssDiagnosticCodes.CssContentFunctionUnsupported001"/> for
/// unsupported functions (<c>counter()</c> / <c>url()</c> / etc.) or unsupported
/// keywords (<c>open-quote</c> / <c>close-quote</c>); and
/// <see cref="CssDiagnosticCodes.CssAttrMultiArgUnsupported001"/> when the
/// modern multi-arg <c>attr()</c> form is detected. The sink-less overload
/// remains for callers that don't have a sink in scope.
/// </para>
/// </remarks>
internal static class CssContentList
{
    /// <summary>The page counters available to <c>counter(page)</c> / <c>counter(pages)</c> when
    /// resolving a CSS Paged Media §6.4 margin box's <c>content</c> (Task 21 cycle 9). <c>Page</c> is
    /// the 1-based number of the page being painted; <c>Pages</c> is the total. Supplied only on the
    /// page-margin-box path — body / pseudo-element content has no page context, so <c>counter(page)</c>
    /// stays unsupported there.</summary>
    public readonly record struct PageCounters(int Page, int Pages);

    /// <summary>Sink-less convenience overload — see the four-argument form
    /// for the diagnostic-emitting path. Returns <see langword="true"/> + the
    /// concatenated text on success; <see langword="false"/> when the value
    /// uses unsupported tokens (no diagnostic emitted).</summary>
    public static bool TryParse(string raw, IElement host, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result);

    /// <summary>Sink-less overload that also resolves <c>counter(page)</c> / <c>counter(pages)</c>
    /// against <paramref name="pageCounters"/> (Task 21 cycle 9 — page-margin-box content).</summary>
    public static bool TryParse(string raw, IElement host, PageCounters pageCounters, out string result)
        => TryParse(raw, host, sink: null, CssSourceLocation.Unknown, out result, pageCounters);

    // Per Phase A security hardening A-5 — cap on the concatenated output of
    // one ::before / ::after / ::marker content-list parse. A pseudo-element's
    // content is rendered into the output PDF; without a cap, an attacker can
    // craft a content value with a multi-megabyte attribute (e.g., a span
    // with data-x set to 10 MiB + ::before { content: attr(data-x) ... }) that
    // bloats the box tree + downstream PDF without bound. 64 KiB per pseudo is
    // generous for any sane label / numeration string while keeping
    // adversarial input bounded.
    private const int MaxContentOutputChars = 64 * 1024;

    /// <summary>Parse <paramref name="raw"/> as a CSS content-list value
    /// against <paramref name="host"/> for <c>attr()</c> resolution; emit a
    /// diagnostic to <paramref name="sink"/> when the parse fails on an
    /// unsupported token. Returns <see langword="true"/> + the concatenated
    /// text on success; <see langword="false"/> on rejection.</summary>
    public static bool TryParse(
        string raw,
        IElement host,
        ICssDiagnosticsSink? sink,
        CssSourceLocation location,
        out string result,
        PageCounters? pageCounters = null)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var sb = new StringBuilder(raw.Length);
        var i = 0;
        var span = raw.AsSpan();

        while (i < span.Length)
        {
            i = SkipWhitespace(span, i);
            if (i >= span.Length) break;

            var c = span[i];

            if (c == '"' || c == '\'')
            {
                if (!ReadString(span, ref i, out var stringPart)) return false;
                if (!CssStringParser.TryParseSingleString(stringPart, out var decoded)) return false;
                if (!TryAppendBounded(sb, decoded, sink, raw, location)) return false;
                continue;
            }

            // attr() — case-insensitive function name per CSS Syntax §4.
            if (StartsWithCaseInsensitive(span, i, "attr("))
            {
                i += "attr(".Length;
                var attrAccepted = ReadAttrArgs(span, ref i, host, out var attrValue,
                    out var rejectedReason);
                if (!attrAccepted)
                {
                    EmitAttrRejection(sink, raw, location, rejectedReason);
                    return false;
                }
                if (!TryAppendBounded(sb, attrValue, sink, raw, location)) return false;
                continue;
            }

            // counter(page) / counter(pages) — only in a page context (Task 21 cycle 9). Other
            // counter names / styles, or no page context, fall through to the unsupported path.
            if (StartsWithCaseInsensitive(span, i, "counter("))
            {
                var counterTokenStart = i;
                i += "counter(".Length;
                if (pageCounters is { } pc && TryReadPageCounter(span, ref i, pc, out var counterText))
                {
                    if (!TryAppendBounded(sb, counterText, sink, raw, location)) return false;
                    continue;
                }
                EmitContentFunctionUnsupported(sink, span, counterTokenStart, raw, location);
                return false;
            }

            // Anything else: counters()/url()/image()/image-set()/
            // linear-gradient()/open-quote/close-quote/identifier — bail.
            EmitContentFunctionUnsupported(sink, span, i, raw, location);
            return false;
        }

        result = sb.ToString();
        return true;
    }

    /// <summary>Per Phase A A-5 — append <paramref name="text"/> to
    /// <paramref name="sb"/> only if doing so stays under the per-pseudo
    /// output cap. Returns false on overflow + emits one diagnostic so the
    /// author sees why the pseudo-element generated no box. The cap is hit
    /// LAZILY (we don't refuse partial output beforehand) so a benign label
    /// near the boundary still works.</summary>
    private static bool TryAppendBounded(
        StringBuilder sb, string text, ICssDiagnosticsSink? sink, string raw, CssSourceLocation location)
    {
        if (sb.Length + text.Length > MaxContentOutputChars)
        {
            sink?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssContentFunctionUnsupported001,
                $"Generated content for this pseudo-element exceeded the {MaxContentOutputChars / 1024} KiB output cap. The pseudo generates no box. Raw value: '{Truncate(raw)}'.",
                CssDiagnosticSeverity.Warning,
                location));
            return false;
        }
        sb.Append(text);
        return true;
    }

    private static void EmitAttrRejection(
        ICssDiagnosticsSink? sink, string raw, CssSourceLocation location, AttrRejectReason reason)
    {
        if (sink is null) return;
        switch (reason)
        {
            case AttrRejectReason.MultiArg:
                sink.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssAttrMultiArgUnsupported001,
                    $"Modern attr() multi-arg form rejected — cycle 1 supports the bare attr(name) form only. Value: '{Truncate(raw)}'.",
                    CssDiagnosticSeverity.Warning,
                    location));
                return;
            case AttrRejectReason.MalformedSyntax:
            default:
                // Fall through to the generic content-function diagnostic for
                // truly malformed attr() (no name, unterminated, etc.).
                sink.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssContentFunctionUnsupported001,
                    $"Malformed attr() in content value: '{Truncate(raw)}'.",
                    CssDiagnosticSeverity.Warning,
                    location));
                return;
        }
    }

    private static void EmitContentFunctionUnsupported(
        ICssDiagnosticsSink? sink, ReadOnlySpan<char> span, int i, string raw, CssSourceLocation location)
    {
        if (sink is null) return;
        // Try to identify the unsupported token kind so the message names what
        // the author wrote — `counter(items)` is more useful than `unrecognized
        // content token`.
        var kind = IdentifyUnsupportedToken(span, i);
        // Per Phase A A-6 — the token name is sliced from raw author CSS, so it
        // can carry C0/C1 control chars or extreme length. Sanitize before
        // interpolation; cap at 40 chars (the function-name + paren shape stays
        // well under that for any sane input).
        var safeKind = NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(kind, maxLength: 40);
        sink.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssContentFunctionUnsupported001,
            $"Unsupported content token '{safeKind}' in '{Truncate(raw)}'. Cycle 1 supports string + attr(name) only.",
            CssDiagnosticSeverity.Warning,
            location));
    }

    /// <summary>Best-effort identification of the unsupported token at
    /// <paramref name="i"/> so the diagnostic message can name it. Returns the
    /// function name (with trailing <c>(</c>) when followed by a paren, or the
    /// identifier text otherwise. Falls back to a single-char snippet when the
    /// token shape isn't recognizable.</summary>
    private static string IdentifyUnsupportedToken(ReadOnlySpan<char> span, int i)
    {
        var start = i;
        var end = i;
        while (end < span.Length)
        {
            var c = span[end];
            if (c == '(' || IsCssWhitespace(c) || c == ',' || c == ')') break;
            end++;
        }
        if (start == end) return "?";
        var name = span[start..end].ToString();
        // If followed by '(' it's a function call.
        if (end < span.Length && span[end] == '(') return name + "()";
        return name;
    }

    /// <summary>Per Phase A A-6 — uses the central diagnostic-text sanitizer
    /// so this helper applies BOTH length truncation + control-char stripping
    /// (ANSI / NUL injection defense) when the raw content-value is
    /// interpolated into a diagnostic message.</summary>
    private static string Truncate(string s) =>
        NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(s, maxLength: 80);

    private enum AttrRejectReason
    {
        MalformedSyntax = 0,
        MultiArg = 1,
    }

    /// <summary>Advance <paramref name="i"/> past the leading-whitespace run.</summary>
    private static int SkipWhitespace(ReadOnlySpan<char> span, int i)
    {
        while (i < span.Length && IsCssWhitespace(span[i])) i++;
        return i;
    }

    /// <summary>Scan one quoted string token starting at <paramref name="i"/>
    /// (which must point at <c>'"'</c> or <c>'\''</c>). Advances
    /// <paramref name="i"/> past the closing quote and returns the substring
    /// inclusive of both quotes (which <see cref="CssStringParser"/> needs).
    /// CSS Syntax §4.3.4 escapes are honored — backslash-quote is not the
    /// terminator.</summary>
    private static bool ReadString(ReadOnlySpan<char> span, ref int i, out string token)
    {
        token = string.Empty;
        var start = i;
        var quote = span[i];
        i++;
        while (i < span.Length)
        {
            if (span[i] == '\\' && i + 1 < span.Length)
            {
                // Skip the backslash + next char (CssStringParser handles the
                // actual escape decoding when we hand it the substring).
                i += 2;
                continue;
            }
            if (span[i] == quote)
            {
                i++; // consume closing quote
                token = span[start..i].ToString();
                return true;
            }
            i++;
        }
        return false; // unterminated
    }

    /// <summary>Parse the argument list of an <c>attr(...)</c> call. Cycle 1
    /// supports ONLY the bare <c>attr(name)</c> form — per Task 14 review
    /// Rec 4, multi-arg forms (<c>attr(name type)</c>,
    /// <c>attr(name, fallback)</c>, <c>attr(name type, fallback)</c>) cause
    /// the entire content-list parse to fail rather than silently treating
    /// them as bare <c>attr(name)</c>. The full modern syntax (CSS Values L4
    /// §10) needs a typed-value pipeline + fallback handling that cycle 2
    /// will deliver. Resolves the attribute case-insensitively against
    /// <paramref name="host"/>; missing attribute → empty string per
    /// Generated Content L3 §1.3.</summary>
    /// <remarks>
    /// Per Task 16 review Rec 5 — the attribute name is validated as a CSS
    /// <i>ident-token</i> per CSS Syntax §4.3.11 (start: letter / <c>_</c> /
    /// non-ASCII / escape; continuation: start chars + digits + <c>-</c>;
    /// optional leading <c>-</c>). Accepting arbitrary punctuation (e.g.,
    /// <c>attr(.foo)</c>) and reporting a missing attribute would have masked
    /// real authoring errors. Cycle-1 simplification: ASCII-only validation
    /// — escape decoding + non-ASCII identifier ranges defer to cycle 2
    /// alongside the typed-value pipeline.
    /// </remarks>
    private static bool ReadAttrArgs(
        ReadOnlySpan<char> span, ref int i, IElement host,
        out string value, out AttrRejectReason reason)
    {
        value = string.Empty;
        reason = AttrRejectReason.MalformedSyntax;
        i = SkipWhitespace(span, i);

        // Per Rec 5: ident-token shape per CSS Syntax §4.3.11. Read ASCII
        // ident characters (start + continuation rules); reject if the run
        // is empty OR starts with a non-ident char.
        var nameStart = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            if (!IsCssIdentChar(c)) return false; // malformed punctuation
            i++;
        }
        if (i == nameStart) return false; // empty name
        if (!IsValidCssIdentStart(span[nameStart..i])) return false;

        var attrName = span[nameStart..i].ToString();

        // Reject extra args per Rec 4 — anything between the name and the
        // close-paren that isn't pure whitespace is an unsupported form.
        i = SkipWhitespace(span, i);
        if (i >= span.Length) return false; // no close-paren at all
        if (span[i] != ')')
        {
            // Multi-arg form (type and/or fallback) — explicit Rec 4 rejection.
            reason = AttrRejectReason.MultiArg;
            return false;
        }
        i++; // consume the close-paren

        var attr = host.GetAttribute(attrName);
        value = attr ?? string.Empty;
        return true;
    }

    /// <summary>Parse a <c>counter(...)</c> call (the <c>counter(</c> already consumed) as a page
    /// counter (Task 21 cycle 9). Supports ONLY <c>counter(page)</c> / <c>counter(pages)</c>, with an
    /// optional <c>decimal</c> style (the default). Resolves <c>page</c> → the current page number and
    /// <c>pages</c> → the total from <paramref name="pc"/>. Returns <see langword="false"/> (→ the
    /// caller bails to the unsupported diagnostic) for any other counter name, a non-<c>decimal</c>
    /// style, or malformed syntax. Other counter styles (<c>lower-roman</c> / …) and the
    /// <c>counters()</c> form are tracked follow-ups.</summary>
    private static bool TryReadPageCounter(ReadOnlySpan<char> span, ref int i, PageCounters pc, out string text)
    {
        text = string.Empty;
        i = SkipWhitespace(span, i);

        var nameStart = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            if (!IsCssIdentChar(c)) return false; // malformed punctuation
            i++;
        }
        if (i == nameStart) return false; // empty name
        var name = span[nameStart..i];
        int value;
        if (name.Equals("page", StringComparison.OrdinalIgnoreCase)) value = pc.Page;
        else if (name.Equals("pages", StringComparison.OrdinalIgnoreCase)) value = pc.Pages;
        else return false; // a non-page counter — not supported here.

        i = SkipWhitespace(span, i);
        // Optional `, <counter-style>` — only the default `decimal` is supported this cycle.
        if (i < span.Length && span[i] == ',')
        {
            i++;
            i = SkipWhitespace(span, i);
            var styleStart = i;
            while (i < span.Length)
            {
                var c = span[i];
                if (IsCssWhitespace(c) || c == ')') break;
                if (!IsCssIdentChar(c)) return false;
                i++;
            }
            if (!span[styleStart..i].Equals("decimal", StringComparison.OrdinalIgnoreCase)) return false;
            i = SkipWhitespace(span, i);
        }

        if (i >= span.Length || span[i] != ')') return false; // missing close-paren.
        i++; // consume ')'
        text = value.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary><see langword="true"/> when <paramref name="c"/> is a valid
    /// continuation char of a CSS ident-token (ASCII subset per cycle-1
    /// scope). Rec 5: letters / digits / hyphen / underscore.</summary>
    private static bool IsCssIdentChar(char c) =>
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c == '-' || c == '_';

    /// <summary><see langword="true"/> when <paramref name="ident"/> is a
    /// valid CSS ident-token start per §4.3.11. ASCII-only check: must start
    /// with letter / <c>_</c>, OR <c>-</c> followed by another valid start
    /// char (so <c>--custom</c> is OK). Pure-numeric / leading-digit are
    /// rejected per the spec.</summary>
    private static bool IsValidCssIdentStart(ReadOnlySpan<char> ident)
    {
        if (ident.IsEmpty) return false;
        var first = ident[0];
        if ((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || first == '_')
            return true;
        if (first == '-')
        {
            if (ident.Length < 2) return false;
            var second = ident[1];
            return (second >= 'a' && second <= 'z')
                || (second >= 'A' && second <= 'Z')
                || second == '_' || second == '-';
        }
        return false;
    }

    private static bool StartsWithCaseInsensitive(ReadOnlySpan<char> span, int i, string ascii)
    {
        if (i + ascii.Length > span.Length) return false;
        for (var k = 0; k < ascii.Length; k++)
        {
            var a = span[i + k];
            var b = ascii[k];
            if (a == b) continue;
            // ASCII case fold.
            if (a is >= 'A' and <= 'Z') a = (char)(a + 32);
            if (b is >= 'A' and <= 'Z') b = (char)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

    private static bool IsCssWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f';
}
