// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 4 border-image (PR 4 review) — expander for the <c>border-image</c> shorthand (CSS Backgrounds &amp;
/// Borders L3 §6.1) into its five longhands: <c>border-image-source</c> / <c>-slice</c> / <c>-width</c> /
/// <c>-outset</c> / <c>-repeat</c>. AngleSharp.Css 1.0.0-beta.144 doesn't reliably round-trip the shorthand,
/// so <see cref="CssPreprocessor"/>'s recovery calls this to emit the longhands with the shorthand's source
/// ordinal — so the cascade resolves the FINAL longhand values by source order (CSS Cascade §3 shorthand
/// semantics: a later <c>border-image-source: none</c> beats an earlier <c>border-image: url(...)</c>, and
/// vice-versa). Grammar: <c>&lt;source&gt; || &lt;slice&gt; [ / &lt;width&gt; [ / &lt;outset&gt; ]? ]? ||
/// &lt;repeat&gt;</c>; an unset longhand resets to its initial.
/// </summary>
internal static class BorderImageShorthandExpander
{
    public static bool TryExpand(
        string rawValue,
        out string source, out string slice, out string width, out string outset, out string repeat)
    {
        // Initial longhand values (CSS B&B §6.1): source none, slice 100%, width 1, outset 0, repeat stretch.
        source = "none"; slice = "100%"; width = "1"; outset = "0"; repeat = "stretch";
        if (string.IsNullOrWhiteSpace(rawValue)) return false;
        var v = CssShorthandHelpers.StripBlockComments(rawValue).Trim();
        if (v.Length == 0) return false;

        // CSS-wide keywords pass through to every longhand.
        if (IsCssWideKeyword(v)) { source = slice = width = outset = repeat = v; return true; }

        // 1) Pull the <image> source (url(...) or a gradient function) out first — its inner spaces / commas
        // / slashes must NOT disturb tokenization. `||` means it can appear anywhere; remove it in place.
        var src = ExtractImageSource(v);
        if (src is not null) { source = src; v = RemoveFirst(v, src); }

        // 2) Pull the repeat keywords out (they can appear before OR after the slash groups via `||`),
        // keeping '/' and the slice / width / outset tokens for step 3.
        var repeatToks = new List<string>();
        var rest = new List<string>();
        foreach (var t in v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var lt = t.ToLowerInvariant();
            if (lt is "stretch" or "repeat" or "round" or "space") repeatToks.Add(lt);
            else rest.Add(t);
        }
        if (repeatToks.Count > 0) repeat = string.Join(' ', repeatToks);

        // 3) The remaining tokens are `<slice> [ / <width> [ / <outset> ] ]` (slashes may be their own
        // tokens or glued to numbers — re-join then split on '/').
        var groups = string.Join(' ', rest).Split('/');
        if (groups[0].Trim().Length > 0) slice = groups[0].Trim();
        if (groups.Length >= 2 && groups[1].Trim().Length > 0) width = groups[1].Trim();
        if (groups.Length >= 3 && groups[2].Trim().Length > 0) outset = groups[2].Trim();
        return true;
    }

    /// <summary>Extract the <c>&lt;image&gt;</c> source — a <c>url(...)</c> or a gradient function call —
    /// with balanced parentheses, or null if none. Keeps the wrapper verbatim so the consumer can route /
    /// diagnose it.</summary>
    private static string? ExtractImageSource(string value)
    {
        var lower = value.ToLowerInvariant();
        var best = -1;
        foreach (var fn in ImageFunctions)
        {
            var idx = lower.IndexOf(fn, StringComparison.Ordinal);
            if (idx >= 0 && (best < 0 || idx < best)) best = idx;
        }
        if (best < 0) return null;
        var open = value.IndexOf('(', best);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < value.Length; i++)
        {
            if (value[i] == '(') depth++;
            else if (value[i] == ')' && --depth == 0)
                return value.Substring(best, i - best + 1);
        }
        return null; // unbalanced
    }

    private static readonly string[] ImageFunctions =
    {
        "url(", "linear-gradient(", "radial-gradient(", "conic-gradient(",
        "repeating-linear-gradient(", "repeating-radial-gradient(", "repeating-conic-gradient(",
    };

    private static string RemoveFirst(string value, string token)
    {
        var idx = value.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return value;
        return (value.Substring(0, idx) + " " + value.Substring(idx + token.Length)).Trim();
    }

    private static bool IsCssWideKeyword(string v) => v.ToLowerInvariant() switch
    {
        "inherit" or "initial" or "unset" or "revert" or "revert-layer" => true,
        _ => false,
    };
}
