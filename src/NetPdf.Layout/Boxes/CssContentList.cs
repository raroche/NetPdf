// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
/// <c>counter()</c> / <c>counters()</c> (needs counter-reset / counter-increment
/// machinery — cycle 2), <c>url()</c> / <c>image()</c> / <c>image-set()</c>
/// (needs the resource pipeline), <c>open-quote</c> / <c>close-quote</c> /
/// <c>no-open-quote</c> / <c>no-close-quote</c> (needs the quotation stack with
/// depth tracking + the <c>quotes</c> property).
/// </para>
/// <para>
/// <b>Task 16 cycle 1 — diagnostic emission.</b> The
/// <see cref="TryParse(string, IElement, ICssDiagnosticsSink?, CssSourceLocation, out string)"/>
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
    /// <summary>Sink-less convenience overload — see the four-argument form
    /// for the diagnostic-emitting path. Returns <see langword="true"/> + the
    /// concatenated text on success; <see langword="false"/> when the value
    /// uses unsupported tokens (no diagnostic emitted).</summary>
    public static bool TryParse(string raw, IElement host, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result);

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
        out string result)
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
                sb.Append(decoded);
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
                sb.Append(attrValue);
                continue;
            }

            // Anything else: counter()/counters()/url()/image()/image-set()/
            // linear-gradient()/open-quote/close-quote/identifier — bail.
            EmitContentFunctionUnsupported(sink, span, i, raw, location);
            return false;
        }

        result = sb.ToString();
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
        sink.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssContentFunctionUnsupported001,
            $"Unsupported content token '{kind}' in '{Truncate(raw)}'. Cycle 1 supports string + attr(name) only.",
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

    private static string Truncate(string s)
    {
        const int max = 80;
        if (s.Length <= max) return s;
        return s[..max] + "…";
    }

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
    /// Generated Content L3 §1.3. The <paramref name="reason"/> out param
    /// distinguishes <see cref="AttrRejectReason.MultiArg"/> from
    /// <see cref="AttrRejectReason.MalformedSyntax"/> so the caller can
    /// emit the appropriate diagnostic code.</summary>
    private static bool ReadAttrArgs(
        ReadOnlySpan<char> span, ref int i, IElement host,
        out string value, out AttrRejectReason reason)
    {
        value = string.Empty;
        reason = AttrRejectReason.MalformedSyntax;
        i = SkipWhitespace(span, i);

        // The attribute name is an ident-token (CSS Syntax §4.3.11); we
        // accept letters, digits, hyphens, underscores, ASCII identifier
        // characters. Stop at whitespace, comma, or close-paren.
        var nameStart = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            i++;
        }
        if (i == nameStart) return false; // empty name

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
