// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Layout.Inline;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Phase 3 Task 22 (<c>string-set</c> / <c>string()</c>) + Task 23 (<c>position: running()</c> /
/// <c>content: element()</c>) — collects, from the document, the named strings and running-element text
/// a page-margin box's <c>content</c> can pull via <c>string(name)</c> / <c>element(name)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Walks the element tree in DOCUMENT ORDER reading the cascade's raw declared values: an element with
/// <c>string-set: name &lt;content-list&gt;</c># (one or more comma-separated name/value pairs, CSS GCPM
/// L3 §2) sets each named string (later assignments win — the value "as of the end of the page", the
/// common running-header case); an element with <c>position: running(name)</c> registers its text for
/// <c>element(name)</c>. Both names are validated as strict CSS <c>&lt;custom-ident&gt;</c>s (so an
/// invalid name like <c>running(123)</c> is ignored, not mistaken for a real name). The result is a
/// <see cref="CssContentList.MarginContentContext"/> threaded to the margin-box painter.
/// </para>
/// <para>
/// <b>First-cut scope (single page).</b> The CSS GCPM L3 cross-page "running" semantics (a named string
/// / running element persists onto later pages until re-set) need the multi-page driver and are
/// deferred. A <c>string-set</c> pair resolves literal strings + <c>attr()</c> + <c>content()</c> (via
/// <see cref="CssContentList.TryParseStringSet(string, IElement, out string)"/>). <c>content()</c> (the element's own text — the common
/// running-header form <c>h1 { string-set: title content() }</c>) works end-to-end: AngleSharp.Css DROPS a
/// <c>string-set</c> whose value contains <c>content()</c> (an unknown function in an unknown property),
/// so <see cref="NetPdf.Css.Parser.Preprocessing.CssPreprocessor"/>'s recovery re-injects the declaration
/// into the cascade, where this collector reads it + resolves <c>content()</c> to the element's text.
/// <c>content(before|after)</c> resolves the host's pseudo content via the cascade (content-pseudo
/// cycle); <c>content(first-letter|marker)</c> stays deferred.
/// <c>element(name)</c> pulls the running element's TEXT; a STANDALONE <c>element(name)</c> renders the
/// running element's box AS the margin box's content box — its text in the element's OWN font + color +
/// <c>text-align</c> (inherited values walked from ancestors, CSS-wide keywords resolved — post-PR-#151
/// review P2 + post-PR-#153 review P2), plus the element's OWN (non-inherited) <c>background-color</c> +
/// <c>border-*</c> + <c>padding-*</c> as the box decoration / box model (cascaded UNDER the box's own
/// declarations) — all captured here by <see cref="CaptureOwnStyle"/>. The running element's nested BLOCK
/// children (laid-out sub-boxes) stay deferred (deferrals.md).
/// </para>
/// </remarks>
internal static class MarginContentCollector
{
    /// <summary>Collect the named strings (<c>string-set</c>) + running-element text
    /// (<c>position: running()</c>) reachable from <paramref name="root"/>, reading raw declared values
    /// from <paramref name="cascade"/>. Returns an empty context when nothing is found.</summary>
    public static CssContentList.MarginContentContext Collect(IElement root, ResolvedCascadeResult cascade)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(cascade);
        var c = new Collected();
        Walk(root, cascade, c);
        return new CssContentList.MarginContentContext(
            c.Named, c.Running, c.NamedFirst, c.RunningFirst, c.RunningStyles, c.RunningStylesFirst,
            c.RunningSegments, c.RunningSegmentsFirst,
            c.RunningContainers, c.RunningContainersFirst);
    }

    /// <summary>Mutable accumulator threaded through the document <see cref="Walk"/> (a reference type, so
    /// the recursion shares one instance rather than passing many <c>ref</c> dictionaries).</summary>
    private sealed class Collected
    {
        public Dictionary<string, string>? Named;        // LAST string-set assignment — string(name, last)
        public Dictionary<string, string>? NamedFirst;   // FIRST — string(name) default + first
        public Dictionary<string, string>? Running;      // LAST running element text — element(name, last)
        public Dictionary<string, string>? RunningFirst; // FIRST — element(name) default + first
        public Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>? RunningStyles;      // LAST own style
        public Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>? RunningStylesFirst; // FIRST own style
        public Dictionary<string, IReadOnlyList<CssContentList.RunningSegment>>? RunningSegments;      // LAST per-line segments
        public Dictionary<string, IReadOnlyList<CssContentList.RunningSegment>>? RunningSegmentsFirst; // FIRST per-line segments
        public Dictionary<string, IReadOnlyList<CssContentList.RunningContainer>>? RunningContainers;      // LAST container bands
        public Dictionary<string, IReadOnlyList<CssContentList.RunningContainer>>? RunningContainersFirst; // FIRST container bands
    }

    private static void Walk(IElement element, ResolvedCascadeResult cascade, Collected c)
    {
        var rules = cascade.TryGetStylesFor(element);
        if (rules is not null)
        {
            // string-set: "<custom-ident> <content-list>"# — one or more comma-separated name/value
            // pairs (CSS GCPM L3 section 2). Each resolved name's last assignment in document order wins.
            var stringSet = rules.GetWinner("string-set")?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(stringSet)
                && !stringSet.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pair in SplitTopLevelCommas(stringSet))
                {
                    if (TryResolveStringSetPair(pair, element, cascade, out var name, out var value))
                    {
                        (c.Named ??= new(StringComparer.Ordinal))[name] = value;            // last-wins
                        (c.NamedFirst ??= new(StringComparer.Ordinal)).TryAdd(name, value); // first-wins (kept)
                    }
                }
            }

            // position: running(<name>) — register the element's content for content: element(name). The
            // content is read BOUNDED (post-PR-#150 review P2 - a huge running element can't force an
            // unbounded TextContent + normalized-string allocation). A running element with BLOCK-LEVEL
            // children yields one U+000A-separated LINE per block child (nested BLOCK children first cut —
            // the painter stacks them as `white-space: pre`); a plain header yields its flat GCPM-normalized
            // text (no U+000A). Both the FIRST (element(name) default + first) and LAST (last) occurrence's
            // content + own style (font/color - for element()'s own-style rendering) are kept.
            var position = rules.GetWinner("position")?.ResolvedValue;
            if (position is not null && TryParseRunningName(position, out var runName))
            {
                // Per-line SEGMENTS (segment-style cycle) are recorded in the same walk, so the
                // selected occurrence's text/style/segments can never mix occurrences (the PR #151
                // lockstep rule). Stored as an array for the same no-downcast-mutation reason as
                // the own-style capture below.
                var segments = new List<CssContentList.RunningSegment>();
                var containers = new List<CssContentList.RunningContainer>();
                var text = ReadRunningElementContent(
                    element, cascade, MaxRunningTextChars, depth: 0, segments, containers);
                (c.Running ??= new(StringComparer.Ordinal))[runName] = text;             // last occurrence
                (c.RunningFirst ??= new(StringComparer.Ordinal)).TryAdd(runName, text);  // first occurrence (kept)
                var segmentArray = segments.ToArray();
                (c.RunningSegments ??= new(StringComparer.Ordinal))[runName] = segmentArray;
                (c.RunningSegmentsFirst ??= new(StringComparer.Ordinal)).TryAdd(runName, segmentArray);
                // Container bands (container-bands cycle) — lockstep with the segments above.
                var containerArray = containers.ToArray();
                (c.RunningContainers ??= new(StringComparer.Ordinal))[runName] = containerArray;
                (c.RunningContainersFirst ??= new(StringComparer.Ordinal)).TryAdd(runName, containerArray);

                // Capture this occurrence's OWN style IN LOCKSTEP with its text (post-PR-#151 review P1).
                // CaptureOwnStyle returns an EMPTY list (not null) when the element has no own font/color, and
                // it is recorded with the SAME last-wins ([key]=) / first-wins (TryAdd) semantics as the text
                // above — so element(name, first|last) can never pair the selected occurrence's TEXT with a
                // DIFFERENT occurrence's STYLE (e.g. a styled first + unstyled last must render the last text
                // in the box's own style, not the first's). An empty list makes the page-margin painter's
                // `TryGetRunningElementOwnStyle` return null (Count == 0) → the box's own style is used.
                var ownStyle = CaptureOwnStyle(element, rules, cascade);
                (c.RunningStyles ??= new(StringComparer.Ordinal))[runName] = ownStyle;            // last occurrence
                (c.RunningStylesFirst ??= new(StringComparer.Ordinal)).TryAdd(runName, ownStyle); // first occurrence
            }
        }

        foreach (var child in element.Children) // IElement children, in document order.
            Walk(child, cascade, c);
    }

    /// <summary>The CSS-INHERITED longhands element()'s own-style rendering pulls from the running element:
    /// the font/color for the CONTENT shaping + <c>text-align</c> for the content alignment within the box.
    /// The painter builds a content <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/> from the font/color
    /// (so a styled running header shapes in its own font + colour; relative units / <c>inherit</c> resolve
    /// against the page context — a documented approximation), and reads <c>text-align</c> directly (it isn't
    /// a <c>MarginBoxStyle</c> longhand) to align the line. All are inherited, so <see cref="CaptureOwnStyle"/>
    /// walks ANCESTORS for each (post-PR-#151 review P2).</summary>
    private static readonly string[] InheritedOwnProperties =
        // line-height joined for the per-segment pitch (segment-line-height cycle) — read straight
        // from the captured pairs by the painter (like text-align; not a MarginBoxStyle longhand).
        { "color", "font-family", "font-size", "font-weight", "font-style", "text-align", "line-height" };

    /// <summary>The NON-inherited <c>background-color</c> + <c>border-*</c> + <c>padding-*</c> longhands
    /// element()'s full-block first cut pulls from the running element for its DECORATION + box model (Task 23
    /// — the element's box becomes the margin box's content box: its own background paints a band, its own
    /// border strokes, and its own border-width + padding inset the text, cascaded UNDER the box's own
    /// declarations). Because these are NOT inherited, they're captured from the element's OWN winner only
    /// (NO ancestor walk — an ancestor's background/padding must not bleed onto the running element). A normal
    /// DOM element's <c>border</c> / <c>background</c> / <c>padding</c> shorthands are already expanded to
    /// these longhands by <c>CssParserAdapter</c>, so the cascade winners are read directly. (A non-absolute
    /// padding — <c>%</c> / <c>em</c> / <c>calc()</c> — is diagnosed + dropped by <c>MarginBoxStyle.Build</c>,
    /// like the box's own; nested block children stay deferred — deferrals.md.)</summary>
    private static readonly string[] DecorationOwnProperties =
    {
        "background-color",
        "border-top-width", "border-top-style", "border-top-color",
        "border-right-width", "border-right-style", "border-right-color",
        "border-bottom-width", "border-bottom-style", "border-bottom-color",
        "border-left-width", "border-left-style", "border-left-color",
        "padding-top", "padding-right", "padding-bottom", "padding-left",
    };

    /// <summary>The empty own-style list — the lockstep marker for a running element with no declared or
    /// inherited font/color and no own decoration (post-PR-#151 review P1). Recorded (not skipped) so the
    /// style dictionaries track the text dictionaries occurrence-for-occurrence; the page-margin painter's
    /// own-style builder treats it as "no own style" and keeps the box's style.</summary>
    private static readonly IReadOnlyList<KeyValuePair<string, string>> EmptyOwnStyle =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Capture the running element's OWN style for element()'s rendering: the inherited
    /// <see cref="InheritedOwnProperties"/> (font/color — nearest self-or-ancestor declared winner, an
    /// approximation of computed inheritance, post-PR-#151 review P2) used for the CONTENT, plus the
    /// non-inherited <see cref="DecorationOwnProperties"/> (background-color / border-* — the element's OWN
    /// winner only) used for the element's DECORATION. Returns <see cref="EmptyOwnStyle"/> (never
    /// <see langword="null"/>) when nothing is declared.</summary>
    /// <remarks>color / font-* / text-align are CSS-INHERITED, so the nearest ancestor that declares one is
    /// the running element's inherited computed value (the element is removed from normal flow BEFORE the
    /// box-builder computes a real <see cref="NetPdf.Css.ComputedValues.ComputedStyle"/>, so none exists to
    /// read — e.g. <c>.section { color: red } .rh { position: running(rh) }</c> makes the running element
    /// red). CSS-wide keywords ARE resolved by the walk (<see cref="NearestDeclaredWinner"/>):
    /// <c>inherit</c>/<c>unset</c>/<c>revert</c> continue to the ancestor value, <c>initial</c> resolves to
    /// the property's initial (so <c>.rh { text-align: inherit }</c> under <c>.section { text-align: right }</c>
    /// aligns right — post-PR-#153 review P2). APPROXIMATION: relative-unit resolution against an INTERMEDIATE
    /// ancestor is not modeled — a relative size resolves against the page context later (documented —
    /// deferrals.md).</remarks>
    private static IReadOnlyList<KeyValuePair<string, string>> CaptureOwnStyle(
        IElement element, ResolvedRuleSet rules, ResolvedCascadeResult cascade)
    {
        List<KeyValuePair<string, string>>? captured = null;
        // Inherited content props — nearest self-or-ancestor winner.
        foreach (var prop in InheritedOwnProperties)
        {
            var value = NearestDeclaredWinner(element, cascade, prop);
            if (!string.IsNullOrWhiteSpace(value))
                (captured ??= new()).Add(new KeyValuePair<string, string>(prop, value));
        }
        // Non-inherited decoration props — the element's OWN winner only (no ancestor walk).
        foreach (var prop in DecorationOwnProperties)
        {
            var value = rules.GetWinner(prop)?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(value))
                (captured ??= new()).Add(new KeyValuePair<string, string>(prop, value));
        }
        // Return an ARRAY, not the mutable builder List, so the stored IReadOnlyList can't be down-cast
        // to List<T> and mutated (the same instance is aliased into both the first + last dictionaries for
        // a single-occurrence element) — Copilot review.
        return captured is null ? EmptyOwnStyle : captured.ToArray();
    }

    /// <summary>The nearest self-or-ancestor declared winning value of <paramref name="prop"/>, walking up
    /// the element tree from <paramref name="element"/>, with CSS-wide keywords resolved per the cascade
    /// (CSS Cascade L5 §7) for these INHERITED properties: <c>inherit</c> / <c>unset</c> / <c>revert</c> /
    /// <c>revert-layer</c> take the PARENT's value, so the walk CONTINUES past them to the nearest concrete
    /// (or <c>initial</c>) ancestor value; <c>initial</c> and every concrete value STOP the walk (the
    /// callers map <c>initial</c> — <c>MarginBoxStyle.HorizontalAlignFactor</c> reads it as <c>start</c>,
    /// the style build resets the slot). Returns
    /// <see langword="null"/> when nothing in the chain declares a non-inherit-like value. Valid only for
    /// INHERITED properties (the caller's color / font-* / text-align), where the nearest concrete
    /// declaration is the inherited computed value modulo the documented relative-unit approximations.</summary>
    private static string? NearestDeclaredWinner(IElement element, ResolvedCascadeResult cascade, string prop)
    {
        for (IElement? e = element; e is not null; e = e.ParentElement)
        {
            var winner = cascade.TryGetStylesFor(e)?.GetWinner(prop)?.ResolvedValue;
            if (string.IsNullOrWhiteSpace(winner)) continue;   // undeclared here — keep walking up
            // An INHERITED property's `inherit`/`unset`/`revert`/`revert-layer` resolves to the PARENT's
            // value (post-PR-#153 review P2): skip this element and continue the walk. `initial` (and every
            // concrete value) stops here — the callers map a captured `initial` (text-align → start, the
            // style build → the property's initial), matching `MarginBoxStyle.ApplyCssWideKeyword`.
            if (IsInheritLikeKeyword(winner)) continue;
            return winner;
        }
        return null;
    }

    /// <summary><see langword="true"/> when <paramref name="value"/> is a CSS-wide keyword that, for an
    /// INHERITED property, resolves to the parent's value (<c>inherit</c> / <c>unset</c> / <c>revert</c> /
    /// <c>revert-layer</c>) — so <see cref="NearestDeclaredWinner"/>'s walk continues to the ancestor.
    /// <c>initial</c> is deliberately NOT here: it resolves to the property's initial value (the walk stops),
    /// matching <c>MarginBoxStyle.ApplyCssWideKeyword</c>'s inherited-property handling.</summary>
    private static bool IsInheritLikeKeyword(string value)
    {
        var v = value.Trim();
        return v.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || v.Equals("unset", StringComparison.OrdinalIgnoreCase)
            || v.Equals("revert", StringComparison.OrdinalIgnoreCase)
            || v.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Split a <c>string-set</c> value into its TOP-LEVEL comma-separated name/value pairs,
    /// honoring parentheses (a functional <c>attr()</c>) and quoted strings (a literal containing a
    /// comma), so <c>a attr(x), b "y,z"</c> → two pairs.</summary>
    private static List<string> SplitTopLevelCommas(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < value.Length) i++;   // escape — skip the next char
                else if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '"':
                case '\'': quote = c; break;
                case '(': depth++; break;
                case ')': if (depth > 0) depth--; break;
                case ',' when depth == 0:
                    parts.Add(value[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(value[start..]);
        return parts;
    }

    /// <summary>Resolve ONE <c>string-set</c> pair (<c>&lt;custom-ident&gt; &lt;content-list&gt;</c>):
    /// read + strictly validate the leading name (a CSS <c>&lt;custom-ident&gt;</c>), then resolve the
    /// content-list via <see cref="CssContentList.TryParseStringSet(string, IElement, out string)"/> (literal strings + <c>attr()</c> +
    /// <c>content()</c> → the element's own text). The <c>content()</c> form reaches the collector via the
    /// preprocessor recovery (see the class remarks). Returns <see langword="false"/> for an invalid name,
    /// a missing content-list, or an unresolvable one.</summary>
    private static bool TryResolveStringSetPair(string pair, IElement element, ResolvedCascadeResult cascade, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var trimmed = pair.Trim();
        if (trimmed.Length == 0) return false;

        // Leading custom-ident (the string name) — read its span, then validate it strictly.
        var i = 0;
        while (i < trimmed.Length && IsIdentChar(trimmed[i])) i++;
        if (i == 0) return false;                 // no name
        name = trimmed[..i];
        if (!CssContentList.IsValidCustomIdent(name)) return false;   // rejects e.g. a leading-digit name

        var rest = trimmed[i..].Trim();
        if (rest.Length == 0) return false;       // no content-list

        // Resolve a literal-string / attr() / content() content-list against the element (the attr() +
        // content() host). content() (the element's own text — the common running-header form) resolves
        // via TryParseStringSet when the raw declaration survives to the cascade; when AngleSharp drops it
        // (content() makes the declaration invalid), the raw-CSS pre-pass recovers it (see Collect).
        // content(before|after) (content-pseudo cycle) resolves the host's ::before/::after pseudo
        // `content` raw from the cascade — a missing pseudo/declaration yields the empty string.
        return CssContentList.TryParseStringSet(rest, element,
            pseudoName => cascade.TryGetStylesForPseudo(element, pseudoName)?.GetWinner("content")?.ResolvedValue,
            out value);
    }

    /// <summary><see langword="true"/> when <paramref name="rawPosition"/> is a
    /// <c>running(&lt;custom-ident&gt;)</c> value (CSS GCPM L3) — the element is removed from normal flow
    /// and its content pulled into a page-margin box by <c>content: element(name)</c>.
    /// <see cref="BoxBuilder"/> uses this to SKIP the element from the body box tree (single source of
    /// truth with the running-text collection here).</summary>
    internal static bool IsRunning(string? rawPosition) =>
        rawPosition is not null && TryParseRunningName(rawPosition, out _);

    /// <summary>Parse a <c>position</c> value of the form <c>running(&lt;custom-ident&gt;)</c>, returning
    /// the running-string name. Any other position value (static/relative/absolute/…) → false.</summary>
    private static bool TryParseRunningName(string raw, out string name)
    {
        name = string.Empty;
        var trimmed = raw.Trim();
        const string prefix = "running(";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(")", StringComparison.Ordinal))
            return false;
        var inner = trimmed[prefix.Length..^1].Trim();
        // Strict <custom-ident> validation: rejects empty, leading-digit (`running(123)`), or punctuation
        // shapes — so invalid CSS does NOT silently remove the element from flow (post-PR-#146 review P2).
        if (!CssContentList.IsValidCustomIdent(inner)) return false;
        name = inner;
        return true;
    }

    private static bool IsIdentChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

    /// <summary>Cap on the text captured from one <c>position: running()</c> element (post-PR-#150 review
    /// P2). Mirrors <see cref="CssContentList"/>'s 64 KiB generated-content output cap — generous for any
    /// real running header/footer while keeping an adversarial / accidental megabyte element bounded
    /// (otherwise <c>IElement.TextContent</c> would allocate the whole thing up front, here, for
    /// every running element, regardless of whether <c>element(name)</c> references it).</summary>
    private const int MaxRunningTextChars = 64 * 1024;

    /// <summary>The maximum BLOCK-nesting depth the running-content read recurses into
    /// (deep-recursion cycle) — a block child deeper than this flattens to one line (the pre-cycle
    /// behavior), so a pathological nest can't grow the stack; content is additionally bounded by
    /// the shared <see cref="MaxRunningTextChars"/> budget at every level.</summary>
    private const int MaxRunningBlockDepth = 16;

    /// <summary>The running element's content for <c>element(name)</c> (Task 23 — nested BLOCK children
    /// FIRST CUT). When the element has BLOCK-LEVEL child elements (per the production
    /// <see cref="DisplayMapper"/>, with the HTML UA tag default via <see cref="HtmlDefaultDisplay"/> when
    /// the cascade doesn't set <c>display</c>), each block child's flattened, GCPM-normalized text becomes
    /// its OWN line and runs of inline/text content between blocks coalesce into a line; the lines are joined
    /// by <c>U+000A</c>, which the page-margin painter honors as a mandatory break (stacked lines). With NO
    /// block-level child (the common single-line header) the flat GCPM-normalized text is returned (no
    /// <c>U+000A</c> → the painter keeps its single-line <c>nowrap</c> path, byte-identical). The whole
    /// result is BOUNDED to <paramref name="maxChars"/> TOTAL — a SINGLE budget is shared across every block
    /// read + inline run + newline separator, and the walk STOPS once exhausted, so N huge block children
    /// can't store N × the cap (post-PR-#154 review P1 / Copilot). SHIPPED here (post-PR-#160 review P3
    /// wording): nested-block TEXT SEGMENTATION — a block child that itself has block-level children
    /// RECURSES (deep-recursion cycle; each nested block its own stacked line) up to
    /// <see cref="MaxRunningBlockDepth"/> levels with the SAME total budget threading through every
    /// level (a deeper nest flattens to one line), and each line is recorded as a
    /// <c>CssContentList.RunningSegment</c> carrying its LEAF element's own font/colour (segment-style
    /// cycle) so the painter shapes it per line. Each LEAF segment also carries its
    /// own DECORATION (a per-line band; the running ROOT's is excluded — it rides the standalone
    /// element() decoration path) + <c>text-align</c> (per-line alignment, the box's winning) +
    /// pitch (segments part 2). STILL DEFERRED (deferrals.md): real nested block LAYOUT
    /// (separately laid-out sub-boxes), per-line margins/padding, per-segment
    /// <c>line-height</c>. A <c>display: none</c> child renders nothing; a <c>display: contents</c>
    /// child's block grandchildren aren't promoted (treated as an inline run).</summary>
    private static string ReadRunningElementContent(
        IElement element, ResolvedCascadeResult cascade, int maxChars, int depth = 0,
        List<CssContentList.RunningSegment>? segments = null,
        List<CssContentList.RunningContainer>? containers = null)
    {
        // No block-level child → the flat normalized text (the common single-line header — no U+000A, so the
        // painter's single-line path is byte-identical to before this first cut). One segment: the element's
        // own (ancestor-walked) style.
        if (!HasBlockLevelChild(element, cascade))
        {
            var flat = LineBuilder.PreprocessWhitespace(ReadBoundedDescendantText(element, maxChars), WhiteSpace.Normal);
            // Decoration only for a LEAF BLOCK segment (depth > 0): at depth 0 this element IS the
            // running ROOT, whose own background/border already paints through the standalone
            // element() decoration path — capturing it here would paint it TWICE (post-PR-#162
            // review P1: a flat `.rh { border: … }` got an outer border plus a per-line copy).
            AddSegment(segments, flat, element, cascade, captureDecoration: depth > 0);
            return flat;
        }

        // Block children → one U+000A-separated LINE per block child, bounded to maxChars TOTAL: `output`
        // never exceeds the cap (each block reads only the REMAINING budget, the inline buffer is capped to
        // it too, and the loop breaks once it's spent — so N huge block children can't store N × the cap).
        var output = new StringBuilder();
        var inlineBuf = new StringBuilder();
        foreach (var child in element.ChildNodes)   // direct children, document order
        {
            var remaining = maxChars - output.Length;
            if (remaining <= 0) break;
            if (child is IElement el)
            {
                var result = DisplayMapper.Map(ResolveDisplay(el, cascade), el.LocalName, out var kind);
                if (result == DisplayMapper.DisplayMappingResult.None) continue;   // display:none → no box, no content
                if (result == DisplayMapper.DisplayMappingResult.Resolved && BoxKindFacts.IsBlockLevelOuter(kind))
                {
                    // flush the pending inline run as a line (its segment owner: THIS element)
                    FlushInlineRun(inlineBuf, output, maxChars, segments, element, cascade, depth);
                    if (output.Length >= maxChars) break;
                    // DEEP RECURSION (deep-recursion cycle): a block child that ITSELF has block-level
                    // children recurses, so each NESTED block contributes its own stacked line —
                    // `<div><div>A</div><div>B</div></div><div>C</div>` renders three lines, not the
                    // flattened "A B" + "C". Depth-capped (a deeper nest flattens — the pre-cycle
                    // behavior) and budget-bounded: the REMAINING budget threads down, so the 64 KiB
                    // total cap holds across every level.
                    // SEGMENTS (segment-style cycle): the recursion records one segment per LEAF
                    // line itself (the nested call appends to the same list); a flattened leaf —
                    // no nested blocks, or past the depth cap — records one segment styled by the
                    // block child element. The recursive budget RESERVES the parent's pending '\n'
                    // separator (post-PR-#160 review P2): AppendLine consumes one char for it when
                    // output is non-empty, so without the reservation a nested call at the budget
                    // boundary could record segment text the joined string can't fit — the
                    // segments now concatenate to EXACTLY the stored capped text.
                    if (depth < MaxRunningBlockDepth && HasBlockLevelChild(el, cascade))
                    {
                        var nestedBudget = maxChars - output.Length - (output.Length > 0 ? 1 : 0);
                        // CONTAINER capture (container-bands cycle) — a DECORATED intermediate
                        // block's own band spans its descendants' segment range. The slot is
                        // RESERVED pre-recursion so the list stays PRE-order (outer before
                        // inner → paint order nests), then filled with the recursion's actual
                        // range; an undecorated container reserves nothing. Its VERTICAL
                        // margins fold into the boundary segments and its HORIZONTAL
                        // margin+padding propagate into its descendants (container-insets
                        // cycle) regardless of decoration.
                        var containerSlot = -1;
                        var firstSegment = segments?.Count ?? 0;
                        if (containers is not null && segments is not null
                            && CaptureSegmentDecoration(el, cascade) is { Count: > 0 })
                        {
                            containerSlot = containers.Count;
                            containers.Add(default);
                        }
                        // Descendant-container range start — captured AFTER the slot reserve so
                        // the container's OWN slot is never bumped by its own inset fold.
                        var firstDescendantContainer = containers?.Count ?? 0;
                        var block = ReadRunningElementContent(
                            el, cascade, nestedBudget, depth + 1, segments, containers);
                        AppendLine(output, block, maxChars);
                        if (segments is not null)
                        {
                            var (leadingInside, trailingInside) = FoldContainerBoxModel(
                                el, cascade, segments, firstSegment, containers, firstDescendantContainer);
                            if (containerSlot >= 0)
                            {
                                FillContainerBand(
                                    containers!, containerSlot, el, cascade, segments, firstSegment,
                                    leadingInside, trailingInside);
                            }
                        }
                    }
                    else
                    {
                        var block = LineBuilder.PreprocessWhitespace(
                            ReadBoundedDescendantText(el, maxChars - output.Length), WhiteSpace.Normal);
                        AddSegment(segments, AppendLine(output, block, maxChars), el, cascade,
                            captureDecoration: true);   // a true LEAF BLOCK child — its own band.
                    }
                }
                else if (inlineBuf.Length < remaining)             // inline / inline-block / contents / unsupported
                {
                    AppendBoundedText(el, inlineBuf, remaining);
                }
            }
            else if (child is IText t && inlineBuf.Length < remaining)
            {
                inlineBuf.Append(t.Data, 0, Math.Min(t.Data.Length, remaining - inlineBuf.Length));
            }
        }
        FlushInlineRun(inlineBuf, output, maxChars, segments, element, cascade, depth);
        return output.ToString();
    }

    /// <summary>An element's computed <c>display</c> for running-content classification: the cascade winner,
    /// or the HTML UA tag default (<see cref="HtmlDefaultDisplay"/> — <c>div</c>→block, <c>span</c>→inline)
    /// when the cascade doesn't set it. Never <see langword="null"/> (so <see cref="DisplayMapper.Map"/> can
    /// consume it).</summary>
    private static string ResolveDisplay(IElement element, ResolvedCascadeResult cascade)
    {
        var display = cascade.TryGetStylesFor(element)?.GetWinner("display")?.ResolvedValue;
        return string.IsNullOrWhiteSpace(display) ? HtmlDefaultDisplay.GetDefault(element.LocalName) : display;
    }

    /// <summary><see langword="true"/> when <paramref name="element"/> has at least one BLOCK-LEVEL child
    /// element — drives the up-front common-case split (no block child → one flat bounded read).</summary>
    private static bool HasBlockLevelChild(IElement element, ResolvedCascadeResult cascade)
    {
        foreach (var child in element.Children)   // IElement children only
            if (IsBlockLevelChild(child, cascade)) return true;
        return false;
    }

    /// <summary><see langword="true"/> when <paramref name="child"/>'s computed <c>display</c> maps to a
    /// BLOCK-LEVEL outer box (block / flow-root / list-item / flex / grid / table / block-replaced) — so it
    /// forces a stacked line in the running content. Routes through the PRODUCTION <see cref="DisplayMapper"/>
    /// + the shared <see cref="BoxKindFacts.IsBlockLevelOuter"/>, so running-content line boundaries stay
    /// aligned with <see cref="BoxBuilder"/>'s box tree (post-PR-#154 review P3): an inline-level value
    /// (incl. <c>inline-block</c>/<c>-flex</c>/<c>-grid</c>/<c>-table</c>), <c>none</c>, <c>contents</c>, or
    /// an unsupported value (ruby / …) is NOT block-level — no more defaulting unknowns to block.</summary>
    private static bool IsBlockLevelChild(IElement child, ResolvedCascadeResult cascade)
    {
        var result = DisplayMapper.Map(ResolveDisplay(child, cascade), child.LocalName, out var kind);
        return result == DisplayMapper.DisplayMappingResult.Resolved && BoxKindFacts.IsBlockLevelOuter(kind);
    }

    /// <summary>Flush the pending inline-content buffer as ONE GCPM-normalized line (when non-empty after
    /// normalization) into <paramref name="output"/> and clear it — a run of text / inline children between
    /// two block children becomes a single line. Whitespace-only runs (the inter-block whitespace text nodes
    /// of indented HTML like <c>&lt;div&gt;\n  &lt;div&gt;A&lt;/div&gt;…</c>) are dropped.</summary>
    private static void FlushInlineRun(
        StringBuilder inlineBuf, StringBuilder output, int maxChars,
        List<CssContentList.RunningSegment>? segments, IElement owner, ResolvedCascadeResult cascade,
        int depth)
    {
        if (inlineBuf.Length == 0) return;
        var line = LineBuilder.PreprocessWhitespace(inlineBuf.ToString(), WhiteSpace.Normal);
        inlineBuf.Clear();
        if (!string.IsNullOrWhiteSpace(line))
        {
            // An inline run is owned by the CURRENT frame element: at depth 0 that's the running
            // ROOT (decoration already on the standalone element() path — review P1), deeper it's
            // a block child whose own band applies to its direct content line.
            AddSegment(segments, AppendLine(output, line, maxChars), owner, cascade,
                captureDecoration: depth > 0);
        }
    }

    /// <summary>Append <paramref name="line"/> to <paramref name="output"/> as a stacked line (a leading
    /// <c>U+000A</c> separator before all but the first), capping the TOTAL at <paramref name="maxChars"/>
    /// (the single running-content budget) — both the separator and the line content count against it.
    /// Returns the line portion actually APPENDED (post-cap; empty when nothing fit) so the segment
    /// capture mirrors the budget-truncated text exactly (segment-style cycle).</summary>
    private static string AppendLine(StringBuilder output, string line, int maxChars)
    {
        if (line.Length == 0 || output.Length >= maxChars) return string.Empty;
        if (output.Length > 0)
        {
            output.Append('\n');
            if (output.Length >= maxChars) return string.Empty;
        }
        var count = Math.Min(line.Length, maxChars - output.Length);
        output.Append(line, 0, count);
        return count == line.Length ? line : line[..count];
    }

    /// <summary>Record one per-line SEGMENT (segment-style cycle): <paramref name="text"/> (the
    /// budget-capped appended line) styled by <paramref name="element"/>'s own inherited font/color
    /// (ancestor-walked via <see cref="CaptureSegmentStyle"/>, so the record is self-contained — an
    /// unstyled leaf inside a styled running root still carries the root's values). No-ops for a
    /// null list (a non-segment caller) or an empty line.</summary>
    private static void AddSegment(
        List<CssContentList.RunningSegment>? segments, string text,
        IElement element, ResolvedCascadeResult cascade, bool captureDecoration)
    {
        if (segments is null || string.IsNullOrEmpty(text)) return;
        var (marginTopPx, marginBottomPx, marginLeftPx, marginRightPx) = captureDecoration
            ? CaptureSegmentMargins(element, cascade)
            : (0.0, 0.0, 0.0, 0.0);   // the running ROOT's margins are the box's business, like its decoration.
        var (paddingTopPx, paddingBottomPx, paddingLeftPx, paddingRightPx) = captureDecoration
            ? CaptureSegmentPadding(element, cascade)
            : (0.0, 0.0, 0.0, 0.0);   // the root's padding already insets via the element() box-model path.
        segments.Add(new CssContentList.RunningSegment(
            text, CaptureSegmentStyle(element, cascade),
            captureDecoration ? CaptureSegmentDecoration(element, cascade) : EmptyOwnStyle,
            marginTopPx, marginBottomPx, paddingTopPx, paddingBottomPx,
            paddingLeftPx, paddingRightPx, marginLeftPx, marginRightPx));
    }

    /// <summary>The leaf block's OWN padding in used px (segment-padding + hpadding cycles) —
    /// the self-only winners (padding isn't inherited), ABSOLUTE lengths only (%/relative read 0,
    /// like the margins below). The vertical sides grow the line's band/pitch; the horizontal
    /// sides inset the line's glyphs + alignment extent.</summary>
    private static (double TopPx, double BottomPx, double LeftPx, double RightPx) CaptureSegmentPadding(
        IElement element, ResolvedCascadeResult cascade)
    {
        var rules = cascade.TryGetStylesFor(element);
        if (rules is null) return (0, 0, 0, 0);
        // Padding is a NON-NEGATIVE property (unlike the sign-preserving margins below) — clamp
        // a negative fold at 0 (post-PR-#164 review P2; defensive — AngleSharp drops invalid
        // negative padding upstream, the same boundary as the 16PX canary).
        return (Math.Max(0, AbsoluteSidePx(rules, "padding-top")),
                Math.Max(0, AbsoluteSidePx(rules, "padding-bottom")),
                Math.Max(0, AbsoluteSidePx(rules, "padding-left")),
                Math.Max(0, AbsoluteSidePx(rules, "padding-right")));
    }

    /// <summary>The self-only winner of an absolute-length side property in used px — the shared
    /// margin/padding capture fold (a non-absolute / negative-disallowed check stays the caller's
    /// concern; this returns the raw fold, 0 for unset/unsupported).</summary>
    private static double AbsoluteSidePx(ResolvedRuleSet rules, string property)
    {
        var raw = rules.GetWinner(property)?.ResolvedValue;
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var v = raw.Trim();
        if (v == "0") return 0;
        return LengthResolver.TrySplitNumberAndUnit(v, out var n, out var unit)
            && unit.Length > 0
            && LengthResolver.TryAbsoluteUnitToPx(unit.ToLowerInvariant(), n, out var px)
            && double.IsFinite(px)
            ? px : 0;
    }

    /// <summary>The leaf block's OWN margins in used px (segment-margins + segment-hmargins
    /// cycles) — the self-only winners (margins aren't inherited), ABSOLUTE lengths only (a
    /// %/relative/`auto` margin reads 0 — the per-line model has no containing-block/font
    /// context here; deferrals.md). VERTICAL values are CAPTURED sign-preserving (legal per CSS
    /// 2.2 §8.3) but the painter clamps the COLLAPSED gap at 0 (PR #163 Copilot ×2 — a
    /// net-negative collapsed margin is treated as TOUCHING, not overlapping: pulling a line up
    /// into its neighbour would overlap the per-line bands; a documented approximation).
    /// HORIZONTAL values clamp ≥ 0 at capture — a negative left/right margin would pull the
    /// line's band/glyphs OUTSIDE its box (the same out-of-box class the vertical clamp
    /// avoids).</summary>
    private static (double TopPx, double BottomPx, double LeftPx, double RightPx) CaptureSegmentMargins(
        IElement element, ResolvedCascadeResult cascade)
    {
        var rules = cascade.TryGetStylesFor(element);
        if (rules is null) return (0, 0, 0, 0);
        return (AbsoluteSidePx(rules, "margin-top"), AbsoluteSidePx(rules, "margin-bottom"),
                Math.Max(0, AbsoluteSidePx(rules, "margin-left")),
                Math.Max(0, AbsoluteSidePx(rules, "margin-right")));
    }

    /// <summary>Fold a recursed CONTAINER's box model into its descendants. VERTICAL
    /// (container-bands + container-vpad cycles): with NO vertical border/padding its margins
    /// max-collapse into the boundary segments' gap margins (CSS 2.2 §8.3.1's
    /// parent/first-last-child adjoining case — the shipped rule); a vertical border/padding
    /// BLOCKS the collapse (§8.3.1), so the gap becomes margin + border + padding + the
    /// pre-fold inner gap, and the INSIDE part (border + padding + inner — the strip the
    /// container's band must extend over) is RETURNED for the band record. HORIZONTAL
    /// (container-insets cycle): its margin + §4.3-gated border width + padding (left/right,
    /// absolute, ≥ 0 — the container's CONTENT box) propagate into every descendant SEGMENT's
    /// margin slots AND every descendant CONTAINER band's margin slots (the container's OWN
    /// reserved slot starts before <paramref name="firstDescendantContainer"/>, so it is never
    /// bumped by its own fold — its border+padding stay INSIDE its band). Runs for EVERY
    /// recursed container, decorated or not.</summary>
    private static (double LeadingInsidePx, double TrailingInsidePx) FoldContainerBoxModel(
        IElement el, ResolvedCascadeResult cascade,
        List<CssContentList.RunningSegment> segments, int firstSegment,
        List<CssContentList.RunningContainer>? containers, int firstDescendantContainer)
    {
        var lastSegment = segments.Count - 1;
        if (lastSegment < firstSegment) return (0, 0);
        var (top, bottom, left, right) = CaptureSegmentMargins(el, cascade);
        var (padTop, padBottom, padLeft, padRight) = CaptureSegmentPadding(el, cascade);
        var (bTop, bBottom, bLeft, bRight) = CaptureSegmentBorderWidths(el, cascade);

        var leadingInside = 0.0;
        if (bTop + padTop > 0)
        {
            // Border/padding BLOCK the collapse: the inner gap (the leaf's own margin, plus
            // anything an inner container already folded) sits INSIDE the band.
            leadingInside = bTop + padTop + segments[firstSegment].MarginTopPx;
            segments[firstSegment] = segments[firstSegment] with
            { MarginTopPx = top + leadingInside };
        }
        else if (top != 0)
        {
            segments[firstSegment] = segments[firstSegment] with
            { MarginTopPx = Math.Max(segments[firstSegment].MarginTopPx, top) };
        }
        var trailingInside = 0.0;
        if (bBottom + padBottom > 0)
        {
            trailingInside = bBottom + padBottom + segments[lastSegment].MarginBottomPx;
            segments[lastSegment] = segments[lastSegment] with
            { MarginBottomPx = bottom + trailingInside };
        }
        else if (bottom != 0)
        {
            segments[lastSegment] = segments[lastSegment] with
            { MarginBottomPx = Math.Max(segments[lastSegment].MarginBottomPx, bottom) };
        }

        var insetL = left + bLeft + padLeft;
        var insetR = right + bRight + padRight;
        if (insetL <= 0 && insetR <= 0) return (leadingInside, trailingInside);
        for (var i = firstSegment; i <= lastSegment; i++)
        {
            segments[i] = segments[i] with
            {
                MarginLeftPx = segments[i].MarginLeftPx + insetL,
                MarginRightPx = segments[i].MarginRightPx + insetR,
            };
        }
        if (containers is not null)
        {
            for (var c = firstDescendantContainer; c < containers.Count; c++)
            {
                var rc = containers[c];
                // Descendants only (prior siblings'/uncles' ranges end before firstSegment);
                // inert empty-recursion records are skipped.
                if (rc.LastSegment < rc.FirstSegment || rc.FirstSegment < firstSegment) continue;
                containers[c] = rc with
                {
                    MarginLeftPx = rc.MarginLeftPx + insetL,
                    MarginRightPx = rc.MarginRightPx + insetR,
                };
            }
        }
        return (leadingInside, trailingInside);
    }

    /// <summary>The container's §4.3-GATED border widths in used px (container-vpad cycle):
    /// an edge contributes its width only when its <c>border-&lt;side&gt;-style</c> is set and
    /// paints (<c>none</c>/<c>hidden</c> → 0); a painting edge with an unset / keyword /
    /// unparsable width uses the keyword map (<c>thin</c> 1 / <c>medium</c> 3 / <c>thick</c> 5;
    /// unset → the <c>medium</c> default, §4.3).</summary>
    private static (double TopPx, double BottomPx, double LeftPx, double RightPx) CaptureSegmentBorderWidths(
        IElement element, ResolvedCascadeResult cascade)
    {
        var rules = cascade.TryGetStylesFor(element);
        if (rules is null) return (0, 0, 0, 0);
        return (GatedBorderWidthPx(rules, "top"), GatedBorderWidthPx(rules, "bottom"),
                GatedBorderWidthPx(rules, "left"), GatedBorderWidthPx(rules, "right"));
    }

    private static double GatedBorderWidthPx(ResolvedRuleSet rules, string side)
    {
        var style = rules.GetWinner($"border-{side}-style")?.ResolvedValue?.Trim();
        if (string.IsNullOrEmpty(style)
            || style.Equals("none", StringComparison.OrdinalIgnoreCase)
            || style.Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        var raw = rules.GetWinner($"border-{side}-width")?.ResolvedValue?.Trim().ToLowerInvariant();
        var declared = raw switch
        {
            "thin" => 1.0,
            "medium" => 3.0,
            "thick" => 5.0,
            _ => Math.Max(0, AbsoluteSidePx(rules, $"border-{side}-width")),
        };
        return declared > 0 ? declared : 3.0;   // unset/unparsable width + painting style → medium.
    }

    /// <summary>Fill the PRE-reserved container slot (container-bands cycle) with the
    /// decorated container's band record — its self-only decoration + inherited colour
    /// (<see cref="CaptureSegmentStyle"/>, the band's currentcolor owner) + the segment range
    /// its recursion produced + its own horizontal margins (insetting ITS band only — the
    /// children's line geometry is untouched, the documented first cut). A recursion that
    /// produced NO lines leaves an inert record (Last &lt; First) the painter skips.</summary>
    private static void FillContainerBand(
        List<CssContentList.RunningContainer> containers, int slot, IElement el,
        ResolvedCascadeResult cascade, List<CssContentList.RunningSegment> segments, int firstSegment,
        double leadingInsidePx, double trailingInsidePx)
    {
        var lastSegment = segments.Count - 1;
        if (lastSegment < firstSegment)
        {
            containers[slot] = new CssContentList.RunningContainer(
                EmptyOwnStyle, EmptyOwnStyle, FirstSegment: 1, LastSegment: 0);
            return;
        }
        var (_, _, marginLeftPx, marginRightPx) = CaptureSegmentMargins(el, cascade);
        containers[slot] = new CssContentList.RunningContainer(
            CaptureSegmentDecoration(el, cascade), CaptureSegmentStyle(el, cascade),
            firstSegment, lastSegment, marginLeftPx, marginRightPx,
            leadingInsidePx, trailingInsidePx);
    }

    /// <summary>The leaf block's OWN (self-only, no ancestor walk — decoration isn't inherited)
    /// background/border longhand winners for its PER-LINE band (segment-decor cycle). The
    /// background-color + 12 border longhands only — per-line padding/margins stay deferred.
    /// Empty (never <see langword="null"/>) for an undecorated or unstyled leaf.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> CaptureSegmentDecoration(
        IElement element, ResolvedCascadeResult cascade)
    {
        var rules = cascade.TryGetStylesFor(element);
        if (rules is null) return EmptyOwnStyle;
        List<KeyValuePair<string, string>>? captured = null;
        foreach (var prop in DecorationOwnProperties)
        {
            if (prop.StartsWith("padding-", StringComparison.Ordinal)) continue; // band = the line rect; padding deferred.
            var value = rules.GetWinner(prop)?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(value))
                (captured ??= new()).Add(new KeyValuePair<string, string>(prop, value));
        }
        return captured is null ? EmptyOwnStyle : captured.ToArray();
    }

    /// <summary>The INHERITED own-style slice for one segment — the same nearest-self-or-ancestor
    /// winner walk as <see cref="CaptureOwnStyle"/>'s inherited loop. The leaf's self-only
    /// decoration is captured SEPARATELY (<see cref="CaptureSegmentDecoration"/> — its per-line
    /// band, segment-decor cycle); per-segment <c>text-align</c> here aligns the segment's own
    /// line (segment-align cycle — the box's own declared <c>text-align</c> still wins).</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> CaptureSegmentStyle(
        IElement element, ResolvedCascadeResult cascade)
    {
        List<KeyValuePair<string, string>>? captured = null;
        foreach (var prop in InheritedOwnProperties)
        {
            var value = NearestDeclaredWinner(element, cascade, prop);
            if (!string.IsNullOrWhiteSpace(value))
                (captured ??= new()).Add(new KeyValuePair<string, string>(prop, value));
        }
        return captured is null ? EmptyOwnStyle : captured.ToArray();
    }

    /// <summary>The element's descendant text (DOM <c>textContent</c>) read BOUNDED to
    /// <paramref name="maxChars"/>: walks child nodes in document order accumulating <see cref="IText"/>
    /// data and STOPS once the cap is reached, so a huge subtree never materializes a full string. The
    /// (already-bounded) result is then normalized + capped again at resolution by
    /// <see cref="CssContentList"/>.</summary>
    internal static string ReadBoundedDescendantText(IElement element, int maxChars)
    {
        var sb = new StringBuilder();
        AppendBoundedText(element, sb, maxChars);
        return sb.ToString();
    }

    private static void AppendBoundedText(INode node, StringBuilder sb, int maxChars)
    {
        foreach (var child in node.ChildNodes)
        {
            if (sb.Length >= maxChars) return;
            switch (child)
            {
                case IText text:
                    var data = text.Data;
                    var room = maxChars - sb.Length;
                    if (data.Length <= room) sb.Append(data);
                    else { sb.Append(data, 0, room); return; }
                    break;
                case IElement: // recurse — textContent concatenates ALL descendant text in document order.
                    AppendBoundedText(child, sb, maxChars);
                    break;
            }
        }
    }
}
