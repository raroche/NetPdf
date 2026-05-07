// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using AngleSharp.Dom;

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
///     attribute → empty string). Cycle-1 ignores the <c>type</c> + fallback
///     parameters of the modern <c>attr()</c> form per CSS Values L4 §10.</item>
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
/// The parser is forgiving: any unrecognized token aborts the parse cleanly
/// (returns false) so the caller can drop the pseudo rather than emit garbled
/// text. Failures are NOT diagnosed — generating no box for unsupported content
/// is the spec-compliant fallback per Generated Content L3 §1.3 (<c>content</c>
/// has computed value <c>normal</c> when invalid).
/// </para>
/// </remarks>
internal static class CssContentList
{
    /// <summary>Parse <paramref name="raw"/> as a CSS content-list value
    /// against <paramref name="host"/> for <c>attr()</c> resolution. Returns
    /// <see langword="true"/> + the concatenated text on success;
    /// <see langword="false"/> when the value uses unsupported tokens.</summary>
    public static bool TryParse(string raw, IElement host, out string result)
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
                if (!ReadAttrArgs(span, ref i, host, out var attrValue)) return false;
                sb.Append(attrValue);
                continue;
            }

            // Anything else: counter()/counters()/url()/image()/image-set()/
            // linear-gradient()/open-quote/close-quote/identifier — bail.
            return false;
        }

        result = sb.ToString();
        return true;
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
    /// supports the bare <c>attr(name)</c> form and tolerates the
    /// <c>attr(name, type)</c> / <c>attr(name, type, fallback)</c> forms by
    /// reading only the attribute name + ignoring the rest. Resolves the
    /// attribute case-insensitively against <paramref name="host"/>; missing
    /// attribute → empty string per Generated Content L3 §1.3.</summary>
    private static bool ReadAttrArgs(
        ReadOnlySpan<char> span, ref int i, IElement host, out string value)
    {
        value = string.Empty;
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

        // Skip the rest of the args (type + fallback) — cycle 1 ignores them.
        // Walk to the matching close-paren, balancing nesting just in case.
        var depth = 1;
        while (i < span.Length && depth > 0)
        {
            var c = span[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            i++;
            if (depth == 0) break;
        }
        if (depth != 0) return false; // unterminated

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
