// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues.PropertyResolvers;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 18 cycle 8 — expander for the <c>grid</c> shorthand
/// per CSS Grid L1 §7.4. Decomposes one declaration into the six
/// grid-template + grid-auto-* longhands:
/// <c>grid-template-rows</c>, <c>grid-template-columns</c>,
/// <c>grid-template-areas</c>, <c>grid-auto-rows</c>,
/// <c>grid-auto-columns</c>, <c>grid-auto-flow</c>.
///
/// <para><b>Grammar per §7.4:</b>
/// <code>
/// &lt;'grid'&gt; =
///   &lt;'grid-template'&gt;
/// | &lt;'grid-template-rows'&gt; / [ auto-flow &amp;&amp; dense? ] &lt;'grid-auto-columns'&gt;?
/// | [ auto-flow &amp;&amp; dense? ] &lt;'grid-auto-rows'&gt;? / &lt;'grid-template-columns'&gt;
/// </code>
/// where <c>&lt;'grid-template'&gt;</c> in cycle-8 scope is
/// <c>none | &lt;rows&gt; / &lt;columns&gt;</c>. The
/// <c>&lt;line-names&gt;? &lt;string&gt; &lt;track-size&gt;?
/// &lt;line-names&gt;?</c> form (= the inline template-areas string
/// shape, e.g., <c>grid: "head head" 50px "main side" 1fr / 1fr 100px</c>)
/// is deferred to a follow-up cycle; this expander falls through to
/// atomic invalidation on it so the declaration drops cleanly per
/// CSS Cascade L4 §4.2.</para>
///
/// <para><b>Spec reset rule:</b> §7.4 states "the <c>grid</c>
/// shorthand also resets all the implicit grid longhands to their
/// initial values". So even forms that only set the explicit
/// template longhands MUST also reset <c>grid-auto-rows</c>,
/// <c>grid-auto-columns</c>, and <c>grid-auto-flow</c> to their
/// initial values (<c>auto</c>, <c>auto</c>, <c>row</c>). Similarly,
/// the auto-flow forms reset the unmentioned template longhand to
/// <c>none</c>.</para>
///
/// <para><b>Why expand at the preprocessor:</b> AngleSharp.Css
/// 1.0.0-beta.144 doesn't reliably round-trip the <c>grid</c>
/// shorthand into its six longhand declarations. The recovery path
/// calls this expander at emission time + emits SIX longhand
/// recovery records sharing the source ordinal. Mirrors the
/// <see cref="GridAreaShorthandExpander"/> + <see cref="FlexShorthandExpander"/>
/// pattern.</para>
///
/// <para><b>Atomic validation</b> per post-PR-#111 review P1#1 +
/// CSS Cascade L4 §4.2: the expander validates EVERY author-derived
/// track-list segment (the <c>&lt;rows&gt;</c> / <c>&lt;columns&gt;</c>
/// values + any <c>&lt;auto-tracks&gt;</c> tail) via
/// <see cref="GridTemplateListResolver.TryValidate"/> BEFORE returning
/// success. A single invalid segment fails the whole expansion so the
/// shorthand contributes NONE of its longhands. Without this, an
/// input like <c>grid: bogus / 100px</c> would have applied the valid
/// <c>grid-template-columns: 100px</c> + reset the auto-* longhands
/// while silently dropping the invalid rows value — a partial
/// application that violates §4.2.</para>
///
/// <para><b>Atomic invalidation</b> on failed expansion: the
/// <see cref="CssPreprocessor"/> emits guaranteed-invalid sentinel
/// longhand recovery records for all six longhands (per post-PR-#111
/// review P1#2 — NOT the raw shorthand value, which can be valid for
/// a track-list longhand, e.g. a no-slash <c>grid: 100px 100px</c>).
/// The sentinel is rejected by every grid longhand resolver, so the
/// cascade falls back to the property initial values.</para>
/// </summary>
internal static class GridShorthandExpander
{
    /// <summary>Attempt to expand a <c>grid</c> shorthand value into
    /// its six longhand values per §7.4.</summary>
    /// <param name="rawValue">The raw value text (already trimmed,
    /// <c>!important</c> already stripped).</param>
    /// <param name="templateRows">Emitted on success — the
    /// <c>grid-template-rows</c> longhand value.</param>
    /// <param name="templateColumns">Emitted on success — the
    /// <c>grid-template-columns</c> longhand value.</param>
    /// <param name="templateAreas">Emitted on success — the
    /// <c>grid-template-areas</c> longhand value.</param>
    /// <param name="autoRows">Emitted on success — the
    /// <c>grid-auto-rows</c> longhand value.</param>
    /// <param name="autoColumns">Emitted on success — the
    /// <c>grid-auto-columns</c> longhand value.</param>
    /// <param name="autoFlow">Emitted on success — the
    /// <c>grid-auto-flow</c> longhand value.</param>
    /// <returns><see langword="true"/> when the value parses as one of
    /// the §7.4 shorthand shapes covered by cycle 8;
    /// <see langword="false"/> otherwise.</returns>
    public static bool TryExpand(
        string rawValue,
        out string templateRows,
        out string templateColumns,
        out string templateAreas,
        out string autoRows,
        out string autoColumns,
        out string autoFlow)
    {
        templateRows = string.Empty;
        templateColumns = string.Empty;
        templateAreas = string.Empty;
        autoRows = string.Empty;
        autoColumns = string.Empty;
        autoFlow = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var stripped = CssShorthandHelpers.StripBlockComments(rawValue);
        if (string.IsNullOrWhiteSpace(stripped)) return false;
        var trimmed = stripped.Trim();

        // CSS-wide keywords pass through verbatim to all six longhands.
        if (GridShorthandHelpers.IsCssWideKeyword(trimmed))
        {
            templateRows = trimmed;
            templateColumns = trimmed;
            templateAreas = trimmed;
            autoRows = trimmed;
            autoColumns = trimmed;
            autoFlow = trimmed;
            return true;
        }

        // Per the same rationale as the other grid expanders — skip
        // expansion when var() is present so post-substitution
        // re-expansion can ship in a future cycle.
        if (ContainsCaseInsensitive(trimmed, "var("))
        {
            return false;
        }

        // grid: none → full reset.
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            templateRows = "none";
            templateColumns = "none";
            templateAreas = "none";
            autoRows = "auto";
            autoColumns = "auto";
            autoFlow = "row";
            return true;
        }

        // The §7.4 forms covered by cycle 8 all contain exactly one '/'
        // (the row/column separator). SplitOnSlash returns null on > 1
        // slash; reject the no-slash case here too.
        var parts = GridLineShorthandExpander.SplitOnSlash(trimmed, max: 2);
        if (parts is null || parts.Length != 2) return false;

        var left = parts[0].Trim();
        var right = parts[1].Trim();
        if (left.Length == 0 || right.Length == 0) return false;

        var leftHasAutoFlow = ContainsAutoFlowToken(left);
        var rightHasAutoFlow = ContainsAutoFlowToken(right);

        // auto-flow on BOTH sides is not a §7.4-valid shape — reject.
        if (leftHasAutoFlow && rightHasAutoFlow) return false;

        // Per §7.4 grammar — `dense` only appears inside the
        // <c>[ auto-flow &amp;&amp; dense? ]</c> production. A bare
        // `dense` token in a side that doesn't contain `auto-flow` is
        // never valid (= can't appear in <c>&lt;rows&gt;</c> /
        // <c>&lt;columns&gt;</c> track lists). Catch this at the
        // expander so the §4.2 atomic-invalidation contract triggers
        // a clean cascade reset instead of partial application via
        // per-longhand resolver rejection.
        if (!leftHasAutoFlow && ContainsDenseToken(left)) return false;
        if (!rightHasAutoFlow && ContainsDenseToken(right)) return false;

        if (leftHasAutoFlow)
        {
            // Form: [ auto-flow && dense? ] <auto-rows>? / <columns>
            if (!TryParseAutoFlowSegment(left,
                    out var dense, out var autoTracks))
            {
                return false;
            }
            // Per post-PR-#111 review P1#1 — atomically validate the
            // author-derived <columns> + the <auto-rows> tail before
            // committing. An invalid segment drops the whole shorthand.
            if (!GridTemplateListResolver.TryValidate(right)) return false;
            if (!string.IsNullOrEmpty(autoTracks)
                && !GridTemplateListResolver.TryValidate(autoTracks))
            {
                return false;
            }
            autoFlow = dense ? "row dense" : "row";
            autoRows = string.IsNullOrEmpty(autoTracks) ? "auto" : autoTracks;
            // Per §7.4 reset rule: <columns> sets grid-template-columns;
            // grid-template-rows + grid-template-areas reset to initial.
            templateRows = "none";
            templateColumns = right;
            templateAreas = "none";
            // grid-auto-columns resets to initial (= auto) since this
            // form only configures grid-auto-rows.
            autoColumns = "auto";
            return true;
        }
        if (rightHasAutoFlow)
        {
            // Form: <rows> / [ auto-flow && dense? ] <auto-columns>?
            if (!TryParseAutoFlowSegment(right,
                    out var dense, out var autoTracks))
            {
                return false;
            }
            // Per post-PR-#111 review P1#1 — atomically validate the
            // author-derived <rows> + the <auto-columns> tail.
            if (!GridTemplateListResolver.TryValidate(left)) return false;
            if (!string.IsNullOrEmpty(autoTracks)
                && !GridTemplateListResolver.TryValidate(autoTracks))
            {
                return false;
            }
            autoFlow = dense ? "column dense" : "column";
            autoColumns = string.IsNullOrEmpty(autoTracks) ? "auto" : autoTracks;
            templateRows = left;
            templateColumns = "none";
            templateAreas = "none";
            autoRows = "auto";
            return true;
        }

        // Form: <rows> / <columns>. Resets all the auto-* longhands +
        // grid-template-areas per §7.4.
        // Per post-PR-#111 review P1#1 — atomically validate BOTH
        // author-derived track lists before committing. This rejects
        // the deferred inline-template-string form
        // (`grid: "a a" 50px / 1fr 100px`) — the string token isn't a
        // valid track list — plus any `grid: bogus / 100px` /
        // `grid: 100px / bogus` shape, so the cascade falls back to
        // initial values rather than partially applying.
        if (!GridTemplateListResolver.TryValidate(left)) return false;
        if (!GridTemplateListResolver.TryValidate(right)) return false;
        templateRows = left;
        templateColumns = right;
        templateAreas = "none";
        autoRows = "auto";
        autoColumns = "auto";
        autoFlow = "row";
        return true;
    }

    /// <summary>Parse a <c>[ auto-flow &amp;&amp; dense? ] &lt;auto-tracks&gt;?</c>
    /// segment per §7.4. The <c>auto-flow</c> token MUST appear; the
    /// <c>dense</c> token is optional and may appear before OR after
    /// <c>auto-flow</c> per the <c>&amp;&amp;</c> grammar combinator.
    /// Any tokens remaining after the auto-flow [+ dense] head form
    /// the <c>&lt;auto-tracks&gt;</c> value (concatenated by single
    /// spaces). Empty tail = no explicit auto-tracks (= caller defaults
    /// to <c>auto</c>).</summary>
    /// <param name="segment">The trimmed segment text (left or right
    /// of the row/column slash).</param>
    /// <param name="dense">True when the segment contains the
    /// <c>dense</c> token alongside <c>auto-flow</c>.</param>
    /// <param name="autoTracks">The remaining tokens after the
    /// auto-flow head, joined by single spaces. Empty when no track
    /// value follows.</param>
    /// <returns>True when the segment matches the auto-flow grammar;
    /// false when (a) the segment doesn't contain <c>auto-flow</c>
    /// (= caller error — caller should have used the non-auto-flow
    /// form path), (b) the <c>dense</c> token appears without
    /// <c>auto-flow</c>, (c) the auto-flow / dense tokens are
    /// duplicated, or (d) per post-PR-#111 review P1#3 — an
    /// <c>auto-flow</c> or <c>dense</c> token appears AFTER the
    /// auto-tracks tail begins (= they belong only to the head per
    /// the <c>[ auto-flow &amp;&amp; dense? ]</c> production, never
    /// inside the <c>&lt;track-size&gt;+</c> tail).</returns>
    private static bool TryParseAutoFlowSegment(
        string segment, out bool dense, out string autoTracks)
    {
        dense = false;
        autoTracks = string.Empty;

        // Tokenize by ASCII whitespace. Preserves balanced parens (=
        // a `repeat(2, 100px)` token survives as one piece). For cycle
        // 8 we assume no quoted strings + no nested-slash content in
        // the auto-tracks tail.
        var tokens = SplitTokens(segment);
        if (tokens.Length == 0) return false;

        var sawAutoFlow = false;
        var sawDense = false;
        var tailStart = -1;

        // Per §7.4 the auto-flow [&& dense?] head must appear at the
        // START of the segment (= before any track values). Walk
        // tokens until we've consumed both optional pieces; the next
        // token starts the auto-tracks tail.
        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            if (tok.Equals("auto-flow", StringComparison.OrdinalIgnoreCase))
            {
                if (sawAutoFlow) return false; // duplicate
                sawAutoFlow = true;
                continue;
            }
            if (tok.Equals("dense", StringComparison.OrdinalIgnoreCase))
            {
                if (sawDense) return false; // duplicate
                sawDense = true;
                continue;
            }
            // First non-(auto-flow|dense) token: the rest is the
            // auto-tracks tail. We require auto-flow to have appeared
            // BEFORE this token (= the spec's "the auto-flow keyword
            // must be present" + spec grammar puts it at the head).
            tailStart = i;
            break;
        }

        if (!sawAutoFlow) return false;
        // `dense` alone (without auto-flow) is invalid in this segment.
        // Already covered by the !sawAutoFlow gate above when dense
        // appears alone before any tail.

        if (tailStart >= 0)
        {
            // Per post-PR-#111 review P1#3 — the head tokens
            // (auto-flow / dense) belong only to the
            // <c>[ auto-flow &amp;&amp; dense? ]</c> production, never
            // inside the <c>&lt;track-size&gt;+</c> tail. Reject a
            // standalone `auto-flow` or `dense` token anywhere in the
            // tail (e.g. `auto-flow 200px dense` or
            // `auto-flow 50px auto-flow`). Without this, those tokens
            // leaked into the auto-tracks value, which the §7.4 grammar
            // forbids; the downstream track-list validator MIGHT catch
            // a bare `dense`/`auto-flow` ident as an invalid track, but
            // making the rejection explicit here keeps the grammar
            // contract self-evident + independent of track-list
            // validator internals.
            for (var i = tailStart; i < tokens.Length; i++)
            {
                if (tokens[i].Equals("auto-flow", StringComparison.OrdinalIgnoreCase)
                    || tokens[i].Equals("dense", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            // Join remaining tokens with single spaces (preserves
            // multi-token track values like `100px 200px`).
            autoTracks = string.Join(" ", tokens, tailStart, tokens.Length - tailStart);
        }
        dense = sawDense;
        return true;
    }

    /// <summary>Return true when <paramref name="segment"/> contains a
    /// standalone <c>auto-flow</c> ASCII-whitespace-delimited token
    /// (case-insensitive). Used to dispatch between the auto-flow
    /// forms vs the plain <c>&lt;rows&gt; / &lt;columns&gt;</c> form
    /// before paying the full segment-tokenize cost.</summary>
    private static bool ContainsAutoFlowToken(string segment)
    {
        var tokens = SplitTokens(segment);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Equals("auto-flow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Return true when <paramref name="segment"/> contains a
    /// standalone <c>dense</c> ASCII-whitespace-delimited token
    /// (case-insensitive). Used to catch <c>dense</c> appearing
    /// without <c>auto-flow</c> — invalid per §7.4 grammar; the
    /// expander rejects so atomic invalidation fires.</summary>
    private static bool ContainsDenseToken(string segment)
    {
        var tokens = SplitTokens(segment);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Equals("dense", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Split <paramref name="segment"/> on ASCII whitespace
    /// runs into non-empty tokens. Parens-aware: a balanced
    /// <c>repeat(2, 100px)</c> stays one token even though it contains
    /// internal whitespace. This keeps multi-token track values
    /// (e.g., <c>100px 200px</c>) as separate tokens while preserving
    /// function-like values intact.</summary>
    private static string[] SplitTokens(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return Array.Empty<string>();

        var result = new System.Collections.Generic.List<string>();
        var sb = new System.Text.StringBuilder(segment.Length);
        var depth = 0;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '(') { depth++; sb.Append(c); continue; }
            if (c == ')') { if (depth > 0) depth--; sb.Append(c); continue; }
            if (depth == 0 && c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result.ToArray();
    }

    private static bool ContainsCaseInsensitive(string source, string match)
        => source.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
}
