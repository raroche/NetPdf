// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
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
/// <see cref="CssContentList.TryParseStringSet"/>). <c>content()</c> (the element's own text — the common
/// running-header form <c>h1 { string-set: title content() }</c>) works end-to-end: AngleSharp.Css DROPS a
/// <c>string-set</c> whose value contains <c>content()</c> (an unknown function in an unknown property),
/// so <see cref="NetPdf.Css.Parser.Preprocessing.CssPreprocessor"/>'s recovery re-injects the declaration
/// into the cascade, where this collector reads it + resolves <c>content()</c> to the element's text.
/// (The typographic targets <c>content(before|after|first-letter|marker)</c> stay deferred.)
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
            c.Named, c.Running, c.NamedFirst, c.RunningFirst, c.RunningStyles, c.RunningStylesFirst);
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
                    if (TryResolveStringSetPair(pair, element, out var name, out var value))
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
                var text = ReadRunningElementContent(element, cascade, MaxRunningTextChars);
                (c.Running ??= new(StringComparer.Ordinal))[runName] = text;             // last occurrence
                (c.RunningFirst ??= new(StringComparer.Ordinal)).TryAdd(runName, text);  // first occurrence (kept)

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
        { "color", "font-family", "font-size", "font-weight", "font-style", "text-align" };

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
    /// content-list via <see cref="CssContentList.TryParseStringSet"/> (literal strings + <c>attr()</c> +
    /// <c>content()</c> → the element's own text). The <c>content()</c> form reaches the collector via the
    /// preprocessor recovery (see the class remarks). Returns <see langword="false"/> for an invalid name,
    /// a missing content-list, or an unresolvable one.</summary>
    private static bool TryResolveStringSetPair(string pair, IElement element, out string name, out string value)
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
        return CssContentList.TryParseStringSet(rest, element, out value);
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

    /// <summary>The running element's content for <c>element(name)</c> (Task 23 — nested BLOCK children
    /// FIRST CUT). When the element has BLOCK-LEVEL child elements (per computed <c>display</c>), each
    /// block child's flattened, GCPM-normalized text becomes its OWN line and runs of inline/text content
    /// between blocks coalesce into a line; the lines are joined by <c>U+000A</c>, which the page-margin
    /// painter honors as a mandatory break (<c>white-space: pre</c>) → stacked lines. With NO block-level
    /// child (the common single-line header) the flat GCPM-normalized text is returned (no <c>U+000A</c> →
    /// the painter keeps its single-line <c>nowrap</c> path, byte-identical). Read BOUNDED to
    /// <paramref name="maxChars"/>. APPROXIMATION: nested blocks are FLATTENED (each direct block child is
    /// one line — its own further block structure / box decoration / margins stay deferred, deferrals.md);
    /// a <c>display: none</c> child's text is still included (parity with the flat read, a pre-existing
    /// gap).</summary>
    private static string ReadRunningElementContent(IElement element, ResolvedCascadeResult cascade, int maxChars)
    {
        List<string>? lines = null;
        var inlineBuf = new StringBuilder();
        foreach (var child in element.ChildNodes)   // direct children, document order
        {
            switch (child)
            {
                case IElement blockEl when IsBlockLevelChild(blockEl, cascade):
                    AppendInlineLine(inlineBuf, ref lines);   // flush the pending inline run as a line
                    var block = LineBuilder.PreprocessWhitespace(
                        ReadBoundedDescendantText(blockEl, maxChars), WhiteSpace.Normal);
                    if (block.Length > 0) (lines ??= new()).Add(block);
                    break;
                case IElement inlineEl:                       // inline element → buffer its descendant text
                    AppendBoundedText(inlineEl, inlineBuf, maxChars);
                    break;
                case IText t when inlineBuf.Length < maxChars:
                    inlineBuf.Append(t.Data, 0, Math.Min(t.Data.Length, maxChars - inlineBuf.Length));
                    break;
            }
        }
        AppendInlineLine(inlineBuf, ref lines);

        // No block-level child → the flat normalized text (the common single-line header — no U+000A, so
        // the painter's single-line path is byte-identical to before this first cut).
        return lines is null
            ? LineBuilder.PreprocessWhitespace(ReadBoundedDescendantText(element, maxChars), WhiteSpace.Normal)
            : string.Join('\n', lines);
    }

    /// <summary>Flush the pending inline-content buffer as ONE GCPM-normalized line (when non-empty after
    /// normalization) and clear it — a run of text / inline children between two block children becomes a
    /// single line.</summary>
    private static void AppendInlineLine(StringBuilder buf, ref List<string>? lines)
    {
        if (buf.Length == 0) return;
        var line = LineBuilder.PreprocessWhitespace(buf.ToString(), WhiteSpace.Normal);
        buf.Clear();
        // Drop a whitespace-only run (the inter-block whitespace text nodes of indented HTML like
        // `<div>\n  <div>A</div>\n  <div>B</div>\n</div>`) — it is not a meaningful stacked line.
        if (!string.IsNullOrWhiteSpace(line)) (lines ??= new()).Add(line);
    }

    /// <summary><see langword="true"/> when <paramref name="child"/>'s computed <c>display</c> is
    /// BLOCK-LEVEL (block / flow-root / list-item / flex / grid / table / …) — i.e. NOT an inline-level /
    /// <c>none</c> / <c>contents</c> value. Reads the cascade winner, falling back to the HTML UA tag
    /// default (<see cref="HtmlDefaultDisplay"/>) when the cascade doesn't set <c>display</c> (so an
    /// unstyled <c>&lt;div&gt;</c> is block and an unstyled <c>&lt;span&gt;</c> is inline) — the same
    /// resolution <see cref="BoxBuilder"/> uses. Splits the running element's content into stacked lines
    /// (nested BLOCK children first cut).</summary>
    private static bool IsBlockLevelChild(IElement child, ResolvedCascadeResult cascade)
    {
        var display = cascade.TryGetStylesFor(child)?.GetWinner("display")?.ResolvedValue;
        if (string.IsNullOrWhiteSpace(display))
            display = HtmlDefaultDisplay.GetDefault(child.LocalName);   // UA tag default (div→block, span→inline)
        return display.Trim().ToLowerInvariant() switch
        {
            "inline" or "inline-block" or "inline-flex" or "inline-grid"
                or "inline-table" or "ruby" or "none" or "contents" => false,
            _ => true,   // block / flow-root / list-item / flex / grid / table / … → block-level
        };
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
