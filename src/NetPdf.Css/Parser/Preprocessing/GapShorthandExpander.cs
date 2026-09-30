// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Expander for the <c>gap</c> shorthand (CSS Box Alignment L3 §8.3) and its legacy alias
/// <c>grid-gap</c>: <c>&lt;row-gap&gt; &lt;column-gap&gt;?</c>. When the second value is omitted it is
/// the SAME as the first.
/// <para>
/// <b>Why this exists.</b> AngleSharp.Css 1.1.x expands a single-value <c>gap: 20px</c> into
/// <c>row-gap: 20px</c> plus an EMPTY <c>column-gap</c>, and when an explicit <c>row-gap</c> follows
/// in the same rule (<c>gap: 20px; row-gap: 30px</c>) the shorthand's 20px is gone from its object
/// model entirely — only <c>row-gap: 30px</c> and the empty column remain. So the correct column gap
/// cannot be reconstructed from AngleSharp's output; it has to come from the raw declaration, which
/// is what the <see cref="CssPreprocessor"/> recovery pass sees. Emitting both longhands as
/// shorthand-expansion records lets the adapter's existing source-order + <c>!important</c> merge
/// (<c>ShouldShorthandWinAgainstExplicitLonghands</c>) decide each longhand exactly as the cascade
/// would.
/// </para>
/// <para>
/// Values are split on top-level whitespace only, so a <c>calc(1px + 2px)</c> or <c>var(--g)</c>
/// stays one component. CSS-wide keywords pass through to both longhands. Zero or more than two
/// components returns <see langword="false"/> and the declaration is left to AngleSharp.
/// </para>
/// </summary>
internal static class GapShorthandExpander
{
    /// <summary>Expand a <c>gap</c> / <c>grid-gap</c> value (trimmed, <c>!important</c> stripped).</summary>
    public static bool TryExpand(string rawValue, out string rowGap, out string columnGap)
    {
        rowGap = columnGap = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        string? first = null;
        string? second = null;
        var depth = 0;
        var start = -1;
        var count = 0;

        for (var i = 0; i <= rawValue.Length; i++)
        {
            var atEnd = i == rawValue.Length;
            var c = atEnd ? ' ' : rawValue[i];
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;

            var boundary = atEnd || (depth == 0 && char.IsWhiteSpace(c));
            if (!boundary)
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;

            var token = rawValue[start..i];
            start = -1;
            count++;
            if (count == 1) first = token;
            else if (count == 2) second = token;
            else return false; // more than two components
        }

        // An unbalanced '(' leaves the whole tail as one "component"; reject rather than guess.
        if (depth != 0 || first is null) return false;

        rowGap = first;
        columnGap = second ?? first;
        return true;
    }
}
