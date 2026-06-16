// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Parser;

/// <summary>
/// Validates a CSS <c>&lt;custom-ident&gt;</c> token (CSS Values 4 §3.2 / CSS Syntax 3 §4.3.11) usable
/// as a NAMED PAGE (CSS Page 3 §3.4 <c>page: auto | &lt;custom-ident&gt;</c>). Shared by
/// <c>PageNameResolver</c> (the <c>page</c> property + <c>@supports</c>) and
/// <c>AtPageRules</c> (the <c>@page &lt;name&gt;</c> selector, <c>DeclaredPageNames</c>, and the used-name
/// walk) so the two stay in lock-step.
///
/// <para><b>Post-PR-#183 review P2.</b> The two validators were duplicated and BOTH wrongly rejected a
/// valid author DASHED ident (<c>--chapter</c>): a dashed ident IS a valid <c>&lt;custom-ident&gt;</c> —
/// CSS Syntax 3 §4.3.9 ("would start an identifier") accepts a leading <c>-</c> FOLLOWED BY another
/// <c>-</c>. Centralizing them here fixes <c>page: --chapter</c>, <c>@page --chapter</c>,
/// <c>@page --chapter:first</c>, and <c>@supports (page: --chapter)</c> in one place.</para>
/// </summary>
internal static class CssCustomIdent
{
    /// <summary>True when <paramref name="value"/> is a valid CSS <c>&lt;custom-ident&gt;</c> usable as a
    /// NAMED PAGE: a single <c>&lt;ident-token&gt;</c> (no whitespace) per <see cref="IsIdentToken"/>, and
    /// NOT a reserved page name (<c>auto</c>, a CSS-wide keyword, or <c>default</c>). Dashed idents
    /// (<c>--chapter</c>) ARE accepted; a leading <c>-</c> before a DIGIT (<c>-1</c> — a number), a
    /// digit-start (<c>1up</c>), and a lone <c>-</c> are rejected.</summary>
    public static bool IsValidPageName(string value) =>
        IsIdentToken(value) && !IsReservedPageName(value);

    /// <summary>True when <paramref name="value"/> is a valid CSS <c>&lt;ident-token&gt;</c> (CSS Syntax 3
    /// §4.3.9 "would start an identifier" + §4.3.11 ident code points): a non-empty run of ident code
    /// points (an ident-start / a digit / <c>-</c>) whose FIRST code point either is an ident-START (a
    /// letter / <c>_</c> / a non-ASCII code point ≥ U+0080), or is a <c>-</c> FOLLOWED BY an ident-start
    /// OR another <c>-</c> (the dashed-ident form, <c>--name</c>). A lone <c>-</c>, a leading <c>-</c>
    /// before a digit, a digit-start, and any non-ident code point (whitespace / <c>:</c> / punctuation)
    /// all reject. (Escape sequences aren't modelled — page names are read raw from the recovered
    /// declaration.)</summary>
    public static bool IsIdentToken(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var first = value[0];
        bool startOk;
        if (IsIdentStart(first))
        {
            startOk = true;
        }
        else if (first == '-' && value.Length > 1)
        {
            // CSS Syntax §4.3.9 — a leading '-' starts an ident when the SECOND code point is an
            // ident-start OR another '-' (the `--name` dashed-ident form). A '-' before a digit (`-1`)
            // tokenizes as a number, and a lone `-` is the delim token — neither is an ident.
            startOk = IsIdentStart(value[1]) || value[1] == '-';
        }
        else
        {
            startOk = false;   // a digit-start, punctuation, or a lone '-'
        }
        if (!startOk) return false;
        foreach (var ch in value)
            if (!IsIdentStart(ch) && !(ch >= '0' && ch <= '9') && ch != '-') return false;
        return true;
    }

    /// <summary>The names a valid ident-token still can't be USED as a page name (CSS Page 3 §3.4): the
    /// CSS-wide keywords (CSS Cascade), the <c>page</c> initial <c>auto</c>, and the reserved
    /// <c>default</c>. Case-insensitive (the keywords are ASCII case-insensitive; a custom name's casing
    /// is otherwise significant).</summary>
    private static bool IsReservedPageName(string value) =>
        value.Equals("auto", StringComparison.OrdinalIgnoreCase)
        || value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
        || value.Equals("initial", StringComparison.OrdinalIgnoreCase)
        || value.Equals("unset", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert", StringComparison.OrdinalIgnoreCase)
        || value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase)
        || value.Equals("default", StringComparison.OrdinalIgnoreCase);

    /// <summary>A CSS Syntax 3 §4.3.11 ident-START code point: an ASCII letter, <c>_</c>, or a non-ASCII
    /// code point (≥ U+0080 — a surrogate half passes too, an accepted approximation since a valid astral
    /// ident code point is itself ≥ U+0080).</summary>
    private static bool IsIdentStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c >= (char)0x80;
}
