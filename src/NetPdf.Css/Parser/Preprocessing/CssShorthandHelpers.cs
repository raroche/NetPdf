// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L17 — shared helpers used by the shorthand
/// expanders (<see cref="FlexShorthandExpander"/>,
/// <see cref="FlexFlowShorthandExpander"/>) for value pre-normalization
/// per CSS Syntax §4.
/// </summary>
internal static class CssShorthandHelpers
{
    /// <summary>Replace CSS block comments (<c>/* ... */</c>) with a
    /// single space per CSS Syntax §4.3.2 — comments are syntactic
    /// whitespace + don't change the value's parsed meaning. Strings
    /// (single + double-quoted) are passed through unchanged so a
    /// comment-like sequence INSIDE a string (e.g.,
    /// <c>"/* not a comment */"</c>) survives intact. Returns the
    /// original input when it contains no comments (fast path).
    ///
    /// <para>Unterminated comments at end-of-input are dropped
    /// silently (= same as the rest of the input after the
    /// <c>/*</c>) — matches the CSS Syntax §4.3.2 error recovery rule
    /// that treats unterminated comments as if they had a closing
    /// <c>*/</c> at EOF.</para></summary>
    /// <param name="input">The raw value text potentially containing
    /// block comments.</param>
    /// <returns>The same text with each <c>/* ... */</c> block
    /// replaced by a single space. When the input has no
    /// <c>/*</c> sequence the original reference is returned (no
    /// allocation).</returns>
    public static string StripBlockComments(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.IndexOf("/*", StringComparison.Ordinal) < 0) return input;

        var sb = new StringBuilder(input.Length);
        var i = 0;
        var n = input.Length;
        while (i < n)
        {
            var c = input[i];
            // String literals — emit verbatim through the matching
            // close-quote so a comment-like sequence inside a string
            // isn't stripped.
            if (c == '\'' || c == '"')
            {
                var quote = c;
                sb.Append(c);
                i++;
                while (i < n)
                {
                    var inner = input[i];
                    sb.Append(inner);
                    i++;
                    if (inner == '\\' && i < n)
                    {
                        // Escaped char — pass through verbatim.
                        sb.Append(input[i]);
                        i++;
                    }
                    else if (inner == quote)
                    {
                        break;
                    }
                }
                continue;
            }
            // Block comment — replace with one space.
            if (c == '/' && i + 1 < n && input[i + 1] == '*')
            {
                i += 2;
                while (i < n)
                {
                    if (input[i] == '*' && i + 1 < n && input[i + 1] == '/')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
