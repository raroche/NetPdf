// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Expander for the <c>overflow</c> shorthand (CSS Overflow L3 §3.1): <c>&lt;overflow-x&gt;
/// &lt;overflow-y&gt;?</c>. With one value both longhands take it; with two, the first is
/// <c>overflow-x</c> and the second <c>overflow-y</c>.
/// <para>
/// <b>Why this exists.</b> AngleSharp.Css (1.0.0-beta.144 and 1.1.x alike) keeps <c>overflow</c> as a
/// single unexpanded declaration, and the engine registers only <c>overflow-x</c> / <c>overflow-y</c>, so
/// <c>overflow: hidden</c> never reached the cascade. That matters wherever layout asks whether a box is a
/// scroll container (the flex automatic minimum size, the inline-block baseline exception).
/// </para>
/// <para>
/// Each component must be one of the <c>overflow-x</c> keywords. A CSS-wide keyword (<c>inherit</c>,
/// <c>initial</c>, …) is valid only as the WHOLE value (CSS Values 4 §7.3), so <c>overflow: inherit hidden</c>
/// is rejected. Anything else returns <see langword="false"/> and the declaration is left alone. The value
/// is split with the same top-level whitespace rule as <see cref="GapShorthandExpander"/>.
/// </para>
/// </summary>
internal static class OverflowShorthandExpander
{
    /// <summary>Expand an <c>overflow</c> value (trimmed, <c>!important</c> stripped).</summary>
    public static bool TryExpand(string rawValue, out string overflowX, out string overflowY)
    {
        overflowX = overflowY = string.Empty;
        if (!GapShorthandExpander.TryExpand(rawValue, out var first, out var second))
        {
            return false;
        }
        if (IsCssWideKeyword(first) || IsCssWideKeyword(second))
        {
            // Valid only alone: one component (GapShorthandExpander repeats it as the second value).
            if (!rawValue.Trim().Equals(first, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!IsOverflowKeyword(first) || !IsOverflowKeyword(second))
        {
            return false;
        }
        overflowX = first;
        overflowY = second;
        return true;
    }

    private static bool IsOverflowKeyword(string value) =>
        value.Equals("visible", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("hidden", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("clip", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("scroll", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("auto", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsCssWideKeyword(string value) =>
        value.Equals("inherit", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("initial", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("unset", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert-layer", System.StringComparison.OrdinalIgnoreCase);
}
