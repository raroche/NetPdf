// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
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
    /// <summary>Split <paramref name="value"/> into whitespace-separated tokens at paren depth 0, so a
    /// functional value (<c>rgb(255, 0, 0)</c>, <c>calc(1px + 2px)</c>) stays a single token despite
    /// its inner spaces/commas. Returns <see langword="false"/> on unbalanced parentheses. Shared by
    /// the <c>border</c> / <c>padding</c> margin-box shorthand expanders.</summary>
    public static bool SplitTopLevel(string value, out List<string> tokens)
    {
        tokens = new List<string>(4);
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch == '(') depth++;
            else if (ch == ')') { if (--depth < 0) return false; }

            if (char.IsWhiteSpace(ch) && depth == 0)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (depth != 0) return false;
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return true;
    }

    /// <summary>Map a 1–4-value CSS box shorthand list to its (top, right, bottom, left) edges per the
    /// CSS box convention: 1 = all four; 2 = vertical horizontal; 3 = top horizontal bottom; 4 = top
    /// right bottom left. <paramref name="values"/> must have 1–4 entries (the caller validates the
    /// count). Shared by the <c>padding</c> / <c>border-width</c> / <c>border-style</c> /
    /// <c>border-color</c> margin-box box-shorthand expanders.</summary>
    public static (string Top, string Right, string Bottom, string Left) ExpandBoxEdges(IReadOnlyList<string> values) =>
        values.Count switch
        {
            1 => (values[0], values[0], values[0], values[0]),
            2 => (values[0], values[1], values[0], values[1]),
            3 => (values[0], values[1], values[2], values[1]),
            _ => (values[0], values[1], values[2], values[3]),
        };

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
