// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Text;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Cycle-1 helper that recognises a single CSS <c>&lt;string&gt;</c> token per
/// CSS Syntax 3 §4.3.5 — decodes the escape sequences and returns the literal
/// text. Used by <see cref="BoxBuilder"/>'s pseudo-element materialization to
/// extract <c>::before { content: "PRE" }</c>'s "PRE" payload while
/// <b>rejecting</b> richer content forms (<c>attr()</c>, <c>counter()</c>,
/// <c>url()</c>, <c>open-quote</c>, <c>close-quote</c>, image references,
/// concatenated tokens) — those need the typed content-list parser that lands
/// in cycle 2 and would otherwise render as literal text in the box tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accepted shape</b> (cycle-1): optional whitespace, then a single quoted
/// string (<c>"..."</c> or <c>'...'</c>), then optional whitespace, then
/// end-of-input. Anything else returns <see langword="false"/>.
/// </para>
/// <para>
/// <b>Escape decoding</b> per CSS Syntax 3 §4.3.7:
/// <list type="bullet">
///   <item><c>\</c> followed by 1–6 hex digits + optional whitespace →
///     the Unicode codepoint with that hex value. <c>\A</c> + space →
///     U+000A (newline); <c>\41</c> → 'A'.</item>
///   <item><c>\</c> followed by a newline → the newline is consumed (line
///     continuation, no character emitted).</item>
///   <item><c>\</c> followed by any other character → that character literally
///     (so <c>\"</c> inside a double-quoted string is a literal <c>"</c>).</item>
/// </list>
/// </para>
/// </remarks>
internal static class CssStringParser
{
    /// <summary>Try to parse <paramref name="value"/> as a single CSS string
    /// token + decode its escapes. Returns <see langword="false"/> when the
    /// input isn't a single-string token (multi-token content lists,
    /// functional values, identifiers like <c>none</c>, dangling escapes,
    /// unterminated strings, trailing tokens after the close quote).</summary>
    public static bool TryParseSingleString(string value, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrEmpty(value)) return false;

        var span = value.AsSpan().Trim();
        if (span.Length < 2) return false;

        var quote = span[0];
        if (quote != '"' && quote != '\'') return false;

        var sb = new StringBuilder(span.Length);
        var i = 1;
        while (i < span.Length)
        {
            var c = span[i];

            if (c == quote)
            {
                // Close quote — make sure no further tokens follow.
                var rest = span[(i + 1)..].TrimStart();
                if (!rest.IsEmpty) return false;
                content = sb.ToString();
                return true;
            }

            if (c == '\\')
            {
                if (i + 1 >= span.Length) return false; // dangling backslash

                var next = span[i + 1];

                // Line continuation: backslash + newline (LF, CR, CR LF, FF).
                if (next == '\n' || next == '\f')
                {
                    i += 2;
                    continue;
                }
                if (next == '\r')
                {
                    i += 2;
                    if (i < span.Length && span[i] == '\n') i++;
                    continue;
                }

                // Hex escape: 1–6 hex digits + optional whitespace.
                if (IsHexDigit(next))
                {
                    var hexStart = i + 1;
                    var hexEnd = hexStart;
                    while (hexEnd < span.Length
                        && hexEnd - hexStart < 6
                        && IsHexDigit(span[hexEnd]))
                    {
                        hexEnd++;
                    }
                    if (!int.TryParse(span[hexStart..hexEnd], NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var codepoint))
                    {
                        return false;
                    }
                    if (codepoint == 0 || codepoint > 0x10FFFF
                        || (codepoint >= 0xD800 && codepoint <= 0xDFFF))
                    {
                        // Per Syntax §4.3.7 a NULL or surrogate codepoint becomes
                        // U+FFFD. Conservative: reject so cycle-2 parser handles it.
                        sb.Append('�');
                    }
                    else
                    {
                        sb.Append(char.ConvertFromUtf32(codepoint));
                    }
                    // One trailing whitespace char is consumed per the spec.
                    if (hexEnd < span.Length && IsCssWhitespace(span[hexEnd])) hexEnd++;
                    i = hexEnd;
                    continue;
                }

                // Any other escaped char: literal next char.
                sb.Append(next);
                i += 2;
                continue;
            }

            // Newlines inside a string are not allowed per Syntax §4.3.5
            // (use \A escape or line continuation instead). Reject.
            if (c == '\n' || c == '\r' || c == '\f') return false;

            sb.Append(c);
            i++;
        }

        // Reached end of input without seeing the close quote.
        return false;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsCssWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f';
}
