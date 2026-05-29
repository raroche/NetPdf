// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 2 Task 3 pre-pass: tokenizes raw CSS text and recovers information that
/// AngleSharp.Css 1.0.0-beta.144 will discard or mangle. Output is a side-channel
/// (<see cref="CssPreprocessResult"/>) that the <see cref="CssParserAdapter"/> merges
/// with the AngleSharp-derived CSSOM to produce a complete AST.
/// </summary>
/// <remarks>
/// Closes (or scaffolds for) the following AngleSharp gaps:
/// <list type="bullet">
///   <item><description>Drops <c>@page</c> selectors and margin-boxes — recovered via
///   <see cref="CssPreprocessResult.PageRecoveries"/>.</description></item>
///   <item><description>Mangles <c>@import</c> with <c>layer(...)</c> / <c>supports(...)</c>
///   into a malformed <c>"not all"</c> media query — recovered via
///   <see cref="CssPreprocessResult.ImportRecoveries"/>.</description></item>
///   <item><description>Silently drops <c>@container</c> / <c>@layer</c> — captured as
///   slots with kind <see cref="CssRuleSlotKind.AtRule"/> and a non-empty
///   <see cref="CssRuleSlot.RawBody"/> for adapter splicing.</description></item>
///   <item><description>Silently corrupts <c>oklch()</c> to bogus rgba and drops
///   <c>color-mix()</c> / <c>light-dark()</c> declarations — raw values captured via
///   <see cref="CssPreprocessResult.StyleRuleRecoveries"/>.</description></item>
///   <item><description>No source positions on rules — every slot carries its line/column.</description></item>
/// </list>
/// <para>
/// <b>Robust slot merge:</b> every top-level rule produces exactly one
/// <see cref="CssRuleSlot"/>, regardless of whether AngleSharp will preserve or drop it.
/// The adapter pairs slots with AngleSharp's emitted rules sequentially, demoting any slot
/// to opaque when the AngleSharp rule kind doesn't match. This means a future AngleSharp
/// regression that drops a previously-supported at-rule won't reintroduce ordinal drift —
/// the adapter just emits an opaque <see cref="CssAtRule"/> for it.
/// </para>
/// <para>
/// <b>Robustness:</b> never throws on malformed CSS. Whatever it can't parse it skips,
/// advancing past the next <c>;</c> or balanced <c>{...}</c>. AngleSharp remains the
/// canonical parser; the pre-pass only fills gaps.
/// </para>
/// </remarks>
internal static class CssPreprocessor
{
    /// <summary>
    /// CSS Paged Media L3 §6.4 margin-box names. Other <c>@&lt;ident&gt; { ... }</c> blocks
    /// inside <c>@page</c> are silently skipped (treated as malformed CSS).
    /// </summary>
    private static readonly FrozenSet<string> KnownMarginBoxNames = new[]
    {
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner",
        "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right", "bottom-right-corner",
        "left-top", "left-middle", "left-bottom",
        "right-top", "right-middle", "right-bottom",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Modern CSS value functions whose authored text the preprocessor preserves verbatim
    /// because AngleSharp.Css mishandles them (<c>oklch</c> → bogus rgba; <c>color-mix</c>
    /// / <c>light-dark</c> → empty rule body; <c>oklab</c> / <c>lab</c> / <c>lch</c> /
    /// <c>color()</c> → similar). Per Task 16 review Rec 2 — the canonical list lives in
    /// <see cref="NetPdf.Css.ComputedValues.ModernColorFunctions"/> so the preprocessor's
    /// recovery + <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.ColorResolver"/>'s
    /// diagnostic emission stay in lockstep.
    /// </summary>
    private static FrozenSet<string> ModernValueFunctions =>
        NetPdf.Css.ComputedValues.ModernColorFunctions.Names;

    /// <summary>
    /// Per Phase 3 Task 10 cycle 3 review (User #1) — CSS Text L3
    /// properties that AngleSharp.Css 1.0.0-beta.144's grammar drops
    /// silently. The ScanDeclarations pass recovers them from the
    /// raw rule body so the cascade resolver receives them.
    /// </summary>
    private static readonly FrozenSet<string> KnownDroppedProperties = new[]
    {
        "overflow-wrap",
        "hyphens",
        // Per Phase 3 Task 10 cycle 3 review (User #2) — `word-wrap`
        // is the legacy alias for `overflow-wrap`. ScanDeclarations
        // also normalizes it to `overflow-wrap` at recovery time,
        // closing the production-path gap that previously left
        // word-wrap declarations dropped at the parser layer.
        "word-wrap",
        // Per Phase 3 Task 10 cycle 3 review (User #3) — AngleSharp.Css
        // accepts white-space:normal/pre/nowrap/pre-wrap/pre-line but
        // drops white-space:break-spaces (CSS Text L3 §3 newer value).
        // The recovery path emits the declaration verbatim so the
        // cascade resolver gets it; the duplicate vs AngleSharp's
        // valid-value emit is tolerated by the cascade per CSS
        // last-decl-wins rules.
        "white-space",
        // Per Phase 3 Task 15 L2 post-PR-#62 review hardening F#4 —
        // AngleSharp.Css 1.0.0-beta.144 accepts the bare
        // `justify-content` values (flex-start / flex-end / center /
        // space-between / etc.) but DROPS the compound
        // `<overflow-position> <content-position>` forms
        // (= `safe center`, `unsafe flex-end`, etc.) per CSS Box
        // Alignment L3 §4.5 — these are the modern (2022) grammar
        // additions AngleSharp's older parser doesn't recognize. Our
        // `KeywordResolver.BuildJustifyContentTable` already produces
        // all 26 indices including the 14 compound forms; routing the
        // recovery path through ScanDeclarations preserves the raw
        // declaration text so the cascade resolver receives it
        // verbatim + the KeywordResolver decodes it correctly. The
        // duplicate vs AngleSharp's bare-value emit (when authors
        // write a bare value) is tolerated by the cascade per CSS
        // last-decl-wins rules — same precedent as the white-space
        // entry above.
        "justify-content",
        // Per Phase 3 Task 15 L3 post-PR-#63 review hardening F#3 —
        // same precedent as `justify-content` above:
        // AngleSharp.Css 1.0.0-beta.144 accepts the bare `align-items`
        // values but DROPS the compound `<overflow-position>
        // <self-position>` forms (= `safe center`, `unsafe flex-end`,
        // etc.) per CSS Box Alignment L3 §6 + §5.3. Our
        // `KeywordResolver.BuildAlignItemsTable` already produces all
        // 27 indices including the 14 compound forms; routing the
        // recovery path through ScanDeclarations preserves the raw
        // declaration text so the cascade resolver receives it
        // verbatim + the KeywordResolver decodes it correctly.
        // Empirical confirmation: the L3 production test
        // `L3_production_html_align_items_unsafe_flex_end_with_overflow_honors_alignment`
        // failed pre-fix (expected -50 block offset got 0 because the
        // compound was dropped + the cascade fell back to the
        // `normal` → `stretch` default).
        "align-items",
        // Per Phase 3 Task 15 L7 post-PR-#67 review hardening F#3 —
        // same precedent as `justify-content` + `align-items` above:
        // AngleSharp.Css 1.0.0-beta.144 accepts the bare `align-content`
        // values but DROPS the compound `<overflow-position>
        // <content-position>` forms (= `safe center`, `unsafe flex-end`,
        // etc.) per CSS Box Alignment L3 §4.5. Our
        // `KeywordResolver.BuildAlignContentTable` produces all 26
        // indices including the 14 compound forms; routing the recovery
        // path through ScanDeclarations preserves the raw declaration
        // text so the cascade resolver receives it verbatim + the
        // KeywordResolver decodes it correctly.
        "align-content",
        // Per Phase 3 Task 15 L9 post-PR-#69 review hardening F#1 —
        // same precedent as the four sibling alignment properties
        // above. `align-self` (CSS Box Alignment L3 §6.2) admits the
        // same `<overflow-position> <self-position>` compound grammar
        // as `align-items`; AngleSharp.Css drops the safe / unsafe
        // compound forms (`safe center`, `unsafe flex-end`, etc.) for
        // the same reason. `KeywordResolver.BuildAlignSelfTable`
        // produces all 28 indices including the 14 compound forms;
        // adding `align-self` here routes the dropped declarations
        // through the recovery path so the cascade + KeywordResolver
        // see them verbatim. Pinned by the L9 hardening's production
        // tests on safe + unsafe compounds.
        "align-self",
        // Per Phase 3 Task 15 L13 — `flex` shorthand. AngleSharp.Css
        // 1.0.0-beta.144 only correctly handles `flex: <number>` (it
        // sets `flex-grow` to the number but doesn't always expand
        // `flex: none` / `flex: auto` / `flex: 100px` / two- or
        // three-value forms into all three longhands per CSS Flexbox
        // L1 §7.4). The recovery path calls
        // <see cref="FlexShorthandExpander.TryExpand"/> at emission
        // time + emits THREE longhand recovery records
        // (`flex-grow` / `flex-shrink` / `flex-basis`) in place of
        // the single dropped `flex` declaration. This is the first
        // shorthand to use the multi-emit recovery pattern; the same
        // shape would extend to `font` / `border` / `background` /
        // etc. when they land.
        "flex",
        // Per Phase 3 Task 15 L16 — `flex-flow` shorthand. Mirrors the
        // L13 pattern for the `<flex-direction> || <flex-wrap>`
        // shorthand per CSS Flexbox L1 §6.1.
        // <see cref="FlexFlowShorthandExpander.TryExpand"/> emits TWO
        // longhand recovery records (`flex-direction` / `flex-wrap`)
        // in place of the single dropped `flex-flow` declaration.
        "flex-flow",
        // Per Phase 3 Task 17 cycle 0c — grid-line shorthands per CSS
        // Grid L1 §8.4. AngleSharp.Css 1.0.0-beta.144 doesn't reliably
        // round-trip these into their two-longhand expansions; the
        // recovery path calls
        // <see cref="GridLineShorthandExpander.TryExpand"/> at emission
        // time + emits TWO longhand recovery records (the start +
        // end longhand for the row or column).
        "grid-row",
        "grid-column",
        // Per Phase 3 Task 17 cycle 0c — grid-area shorthand per §8.4.
        // <see cref="GridAreaShorthandExpander.TryExpand"/> emits FOUR
        // longhand recovery records (the four grid-line longhands).
        // Named-area references (= grid-area: my-area-name resolving
        // to a grid-template-areas name) are cycle 7's scope; cycle 0c
        // treats every identifier as a <custom-ident> per the §8.4
        // fallback rule.
        "grid-area",
        // Per Phase 3 Task 18 cycle 8 — the `grid` shorthand per §7.4.
        // <see cref="GridShorthandExpander.TryExpand"/> emits SIX
        // longhand recovery records (the three grid-template-* +
        // the three grid-auto-* longhands). Covers the `none` reset,
        // the `<rows> / <columns>` plain template form, and both
        // auto-flow forms with optional `dense`. The inline
        // template-areas string form (`grid: "a a" 50px / 100px`)
        // is deferred to a follow-up cycle.
        "grid",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per Phase 3 Task 10 cycle 3 review (User #2) — central
    /// property-name normalizer for legacy aliases. Resolves
    /// `word-wrap` → `overflow-wrap` per CSS Text L3 §5.1 (legacy
    /// alias). Other aliases land as the cascade pipeline grows.
    /// </summary>
    private static readonly FrozenDictionary<string, string> LegacyPropertyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["word-wrap"] = "overflow-wrap",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apply legacy-alias normalization to a property name.
    /// Returns the modern name (lowercased) if the input is a known
    /// alias; otherwise returns the input lowercased.</summary>
    internal static string NormalizePropertyName(string name)
    {
        var lower = name.ToLowerInvariant();
        return LegacyPropertyAliases.TryGetValue(lower, out var modern)
            ? modern
            : lower;
    }

    /// <summary>
    /// Grouping at-rules that AngleSharp emits with a <c>Rules</c> child list. The
    /// preprocessor recurses into their bodies to find nested modern at-rules.
    /// </summary>
    private static readonly FrozenSet<string> GroupingAtRules = new[]
    {
        "media", "supports", "keyframes", "-webkit-keyframes",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Walks <paramref name="css"/> in source order and produces recovery side-data for the
    /// adapter. <paramref name="source"/> identifies the input (a stylesheet URL or the
    /// literal <c>"&lt;style&gt;"</c>) for source-location reporting.
    /// </summary>
    public static CssPreprocessResult Process(string css, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(css);

        var pageRecoveries = ImmutableArray.CreateBuilder<CssPageRuleRecovery>();
        var importRecoveries = ImmutableArray.CreateBuilder<CssImportRuleRecovery>();
        var styleRuleRecoveries = ImmutableArray.CreateBuilder<CssStyleRuleRecovery>();
        var styleRuleOrdinal = 0;
        var pageOrdinal = 0;
        var importOrdinal = 0;

        var tok = new CssTokenizer(css.AsSpan(), source);
        var slots = WalkRules(ref tok, pageRecoveries, importRecoveries, styleRuleRecoveries,
            ref styleRuleOrdinal, ref pageOrdinal, ref importOrdinal);

        return new CssPreprocessResult(
            pageRecoveries.Count == 0 ? ImmutableArray<CssPageRuleRecovery>.Empty : pageRecoveries.ToImmutable(),
            importRecoveries.Count == 0 ? ImmutableArray<CssImportRuleRecovery>.Empty : importRecoveries.ToImmutable(),
            styleRuleRecoveries.Count == 0 ? ImmutableArray<CssStyleRuleRecovery>.Empty : styleRuleRecoveries.ToImmutable(),
            slots);
    }

    /// <summary>
    /// Walks rules at the current tokenizer position until end-of-input or a closing
    /// <c>}</c>. Used both for the top-level walk and for recursing into grouping rule
    /// bodies. Returns the collected slot list.
    /// </summary>
    private static ImmutableArray<CssRuleSlot> WalkRules(
        ref CssTokenizer tok,
        ImmutableArray<CssPageRuleRecovery>.Builder pageRecoveries,
        ImmutableArray<CssImportRuleRecovery>.Builder importRecoveries,
        ImmutableArray<CssStyleRuleRecovery>.Builder styleRuleRecoveries,
        ref int styleRuleOrdinal,
        ref int pageOrdinal,
        ref int importOrdinal)
    {
        var slots = ImmutableArray.CreateBuilder<CssRuleSlot>();
        tok.SkipWhitespaceAndComments();
        while (!tok.IsEnd && tok.PeekChar() != '}')
        {
            var ruleStart = tok.CurrentLocation;
            if (tok.PeekChar() == '@')
            {
                slots.Add(WalkAtRule(ref tok, ruleStart,
                    pageRecoveries, importRecoveries, styleRuleRecoveries,
                    ref styleRuleOrdinal, ref pageOrdinal, ref importOrdinal));
            }
            else
            {
                slots.Add(WalkStyleRule(ref tok, ruleStart, styleRuleRecoveries, ref styleRuleOrdinal));
            }
            tok.SkipWhitespaceAndComments();
        }
        return slots.Count == 0 ? ImmutableArray<CssRuleSlot>.Empty : slots.ToImmutable();
    }

    private static CssRuleSlot WalkStyleRule(
        ref CssTokenizer tok,
        CssSourceLocation start,
        ImmutableArray<CssStyleRuleRecovery>.Builder styleRuleRecoveries,
        ref int styleRuleOrdinal)
    {
        var ordinal = styleRuleOrdinal++;
        var preludeSpan = tok.ReadUntilAnyTopLevel("{;");
        var prelude = preludeSpan.ToString().Trim();
        var rawBody = string.Empty;

        if (tok.PeekChar() == '{')
        {
            var bodyWithBraces = tok.ReadCurlyBlock();
            rawBody = bodyWithBraces.Length >= 2 ? bodyWithBraces[1..^1].ToString() : string.Empty;

            // Walk the body for modern function declarations.
            // Per Phase 3 Task 15 L17 post-PR-#77 — also pick up the
            // list of explicit longhand declarations + their source
            // ordinals + importance so the merge respects CSS Cascade
            // §5 importance + §7.4 source-order for shorthand-vs-
            // explicit-longhand conflicts.
            var (modernDecls, explicitLonghands) =
                ScanForModernDeclarationsWithOrder(rawBody);
            if (!modernDecls.IsEmpty)
            {
                styleRuleRecoveries.Add(
                    new CssStyleRuleRecovery(
                        ordinal,
                        modernDecls,
                        explicitLonghands));
            }
        }
        else if (tok.PeekChar() == ';')
        {
            tok.ReadChar();
        }

        return new CssRuleSlot(
            Kind: CssRuleSlotKind.StyleRule,
            AtKeyword: string.Empty,
            Prelude: prelude,
            RawBody: rawBody,
            NestedSlots: ImmutableArray<CssRuleSlot>.Empty,
            Location: start);
    }

    private static CssRuleSlot WalkAtRule(
        ref CssTokenizer tok,
        CssSourceLocation start,
        ImmutableArray<CssPageRuleRecovery>.Builder pageRecoveries,
        ImmutableArray<CssImportRuleRecovery>.Builder importRecoveries,
        ImmutableArray<CssStyleRuleRecovery>.Builder styleRuleRecoveries,
        ref int styleRuleOrdinal,
        ref int pageOrdinal,
        ref int importOrdinal)
    {
        var atKeyword = tok.ReadAtKeyword().ToString();
        var keywordLower = atKeyword.ToLowerInvariant();

        // Per-keyword routing below — but every path produces a CssRuleSlot in the end so
        // the slot list stays exactly aligned with source order.
        if (keywordLower.Equals("page", StringComparison.Ordinal))
        {
            var rec = ParsePageRule(ref tok, pageOrdinal++, start);
            pageRecoveries.Add(rec);
            return new CssRuleSlot(
                Kind: CssRuleSlotKind.AtRule,
                AtKeyword: keywordLower,
                Prelude: rec.SelectorText,
                RawBody: string.Empty,
                NestedSlots: ImmutableArray<CssRuleSlot>.Empty,
                Location: start);
        }

        if (keywordLower.Equals("import", StringComparison.Ordinal))
        {
            var rec = ParseImportRule(ref tok, importOrdinal++, start);
            importRecoveries.Add(rec);
            return new CssRuleSlot(
                Kind: CssRuleSlotKind.AtRule,
                AtKeyword: keywordLower,
                Prelude: rec.Url,
                RawBody: string.Empty,
                NestedSlots: ImmutableArray<CssRuleSlot>.Empty,
                Location: start);
        }

        // Generic at-rule: read prelude, then either a balanced curly body or a ';' terminator.
        tok.SkipWhitespaceAndComments();
        var preludeSpan = tok.ReadUntilAnyTopLevel("{;");
        var prelude = preludeSpan.ToString().Trim();

        var rawBody = string.Empty;
        var nestedSlots = ImmutableArray<CssRuleSlot>.Empty;

        if (tok.PeekChar() == '{')
        {
            // For grouping at-rules (@media/@supports/@keyframes), recurse into the body to
            // catch nested modern at-rules. For everything else (@font-face, @container,
            // @layer, unknown), capture the body as opaque raw text.
            if (GroupingAtRules.Contains(keywordLower))
            {
                tok.ReadChar(); // consume '{'
                nestedSlots = WalkRules(ref tok,
                    pageRecoveries, importRecoveries, styleRuleRecoveries,
                    ref styleRuleOrdinal, ref pageOrdinal, ref importOrdinal);
                if (tok.PeekChar() == '}') tok.ReadChar();
            }
            else
            {
                var bodyWithBraces = tok.ReadCurlyBlock();
                rawBody = bodyWithBraces.Length >= 2 ? bodyWithBraces[1..^1].ToString() : string.Empty;
            }
        }
        else if (tok.PeekChar() == ';')
        {
            tok.ReadChar();
        }

        return new CssRuleSlot(
            Kind: CssRuleSlotKind.AtRule,
            AtKeyword: keywordLower,
            Prelude: prelude,
            RawBody: rawBody,
            NestedSlots: nestedSlots,
            Location: start);
    }

    /// <summary>
    /// Parses an <c>@page</c> rule's prelude (selector) and body (declarations + margin-box
    /// at-rules). Position is on the character right after the <c>page</c> at-keyword.
    /// </summary>
    private static CssPageRuleRecovery ParsePageRule(ref CssTokenizer tok, int ordinal, CssSourceLocation location)
    {
        tok.SkipWhitespaceAndComments();
        var selectorSpan = tok.ReadUntilAnyTopLevel("{;");
        var selectorText = selectorSpan.ToString().Trim();

        var marginBoxes = ImmutableArray.CreateBuilder<CssMarginBoxRecovery>();
        if (tok.PeekChar() == '{')
        {
            tok.ReadChar();
            tok.SkipWhitespaceAndComments();
            while (!tok.IsEnd && tok.PeekChar() != '}')
            {
                if (tok.PeekChar() == '@')
                {
                    var boxStart = tok.CurrentLocation;
                    var boxKeyword = tok.ReadAtKeyword().ToString();
                    tok.SkipWhitespaceAndComments();
                    if (tok.PeekChar() == '{')
                    {
                        var bodySpan = tok.ReadCurlyBlock();
                        if (KnownMarginBoxNames.Contains(boxKeyword))
                        {
                            var body = bodySpan.Length >= 2
                                ? bodySpan[1..^1].ToString().Trim()
                                : string.Empty;
                            marginBoxes.Add(new CssMarginBoxRecovery(boxKeyword, body, boxStart));
                        }
                    }
                    else
                    {
                        tok.ReadUntilAnyTopLevel(";}");
                        if (tok.PeekChar() == ';') tok.ReadChar();
                    }
                }
                else
                {
                    tok.ReadUntilAnyTopLevel(";}");
                    if (tok.PeekChar() == ';') tok.ReadChar();
                }
                tok.SkipWhitespaceAndComments();
            }
            if (tok.PeekChar() == '}') tok.ReadChar();
        }
        else if (tok.PeekChar() == ';')
        {
            tok.ReadChar();
        }

        return new CssPageRuleRecovery(
            ordinal,
            selectorText,
            marginBoxes.Count == 0 ? ImmutableArray<CssMarginBoxRecovery>.Empty : marginBoxes.ToImmutable(),
            location);
    }

    /// <summary>
    /// Parses an <c>@import</c> rule's prelude per the CSS Cascade L5 §2.4 grammar:
    /// <c>@import &lt;url&gt; [layer | layer(name)] [supports(condition)] [media-query];</c>.
    /// Clauses appear in this fixed order — <c>layer</c> first, then <c>supports</c>, then
    /// the (optional) media query, which always comes last and absorbs the remainder of the
    /// prelude. The parser accepts <c>layer</c> and <c>supports</c> in either order before
    /// the media query (per L5 they're commutative pre-media), but anything that isn't a
    /// recognized layer/supports keyword starts the media query and ends layer/supports
    /// recognition.
    /// </summary>
    private static CssImportRuleRecovery ParseImportRule(ref CssTokenizer tok, int ordinal, CssSourceLocation location)
    {
        tok.SkipWhitespaceAndComments();

        var url = ReadImportUrl(ref tok);
        tok.SkipWhitespaceAndComments();

        string? layerName = null;
        string? supportsCondition = null;
        var media = string.Empty;

        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == ';' || c == '\0') break;

            if (TryConsumeKeyword(ref tok, "layer"))
            {
                if (tok.PeekChar() == '(')
                {
                    var paren = tok.ReadParenthesizedBlock();
                    layerName = TrimSurroundingParens(paren).Trim().ToString();
                }
                else
                {
                    layerName = string.Empty;
                }
                tok.SkipWhitespaceAndComments();
                continue;
            }

            if (TryConsumeKeyword(ref tok, "supports"))
            {
                if (tok.PeekChar() == '(')
                {
                    var paren = tok.ReadParenthesizedBlock();
                    supportsCondition = TrimSurroundingParens(paren).Trim().ToString();
                }
                tok.SkipWhitespaceAndComments();
                continue;
            }

            var rest = tok.ReadUntilAnyTopLevel(";").ToString().Trim();
            media = rest;
            break;
        }

        if (tok.PeekChar() == ';') tok.ReadChar();

        return new CssImportRuleRecovery(
            ordinal,
            url,
            media,
            layerName,
            supportsCondition,
            location);
    }

    /// <summary>
    /// Walks a style-rule body looking for declarations whose value contains a modern
    /// CSS function (<c>oklch</c>, <c>oklab</c>, <c>color-mix</c>, <c>light-dark</c>).
    /// Returns recovered raw declarations to merge over AngleSharp's mishandled output.
    /// </summary>
    /// <summary>Per Phase 2 deep review Rec 3 — exposed to <see cref="CssParserAdapter"/>
    /// so inline <c>style="..."</c> attributes can be run through the same recovery
    /// layer as <c>&lt;style&gt;</c> blocks. Without this, AngleSharp.Css 1.0.0-beta.144's
    /// inline-style parser loses modern colors + multi-arg attr() before the typed
    /// pipeline gets a chance to diagnose them.</summary>
    internal static ImmutableArray<CssDeclarationRecovery> ScanForModernDeclarations(string body) =>
        ScanDeclarations(body, modernOnly: true).Recoveries;

    /// <summary>Per Phase 3 Task 15 L17 post-PR-#77 review — extended
    /// scan that also returns the list of EXPLICIT longhand
    /// declarations (non-shorthand) with their source ordinals + importance.
    /// The merge in
    /// <see cref="CssParserAdapter.AdaptDeclarationsWithRecovery"/> uses
    /// this list together with the recovery records' own
    /// <see cref="CssDeclarationRecovery.SourceOrdinal"/> /
    /// <see cref="CssDeclarationRecovery.IsImportant"/> to apply CSS
    /// Cascade §5 importance + §7.4 source-order rules properly. This
    /// supports the multi-shorthand case
    /// (<c>flex-flow ...; flex-wrap ...; flex-flow ...</c>) AND
    /// <c>!important</c> interactions that a per-property set could not
    /// represent.</summary>
    internal static (ImmutableArray<CssDeclarationRecovery> Recoveries,
        ImmutableArray<ExplicitLonghandRef> ExplicitLonghands)
        ScanForModernDeclarationsWithOrder(string body) =>
        ScanDeclarations(body, modernOnly: true);

    /// <summary>
    /// Per Phase 2 deep review Rec 2 — parses every declaration in
    /// <paramref name="body"/> regardless of whether it contains a modern
    /// function. Used by <see cref="CssParserAdapter"/>'s opaque-rule
    /// fallback when AngleSharp drops a style rule entirely (e.g.,
    /// <c>li::marker { content: counter(items); color: red }</c> in
    /// AngleSharp.Css 1.0.0-beta.144). Without this, the dropped rule's
    /// declarations were lost — making <c>::marker</c> content/style
    /// rules + their CSS-CONTENT-FUNCTION-UNSUPPORTED-001 diagnostic
    /// unreachable through the production path.
    /// </summary>
    /// <remarks>The same scan logic as <see cref="ScanForModernDeclarations"/>
    /// but emits every parsed declaration. Property name is lower-cased per
    /// CSS Syntax §2 (case-insensitive).</remarks>
    internal static ImmutableArray<CssDeclarationRecovery> ScanAllDeclarations(string body) =>
        ScanDeclarations(body, modernOnly: false).Recoveries;

    /// <summary>Per Phase 3 Task 15 L17 post-PR-#77 review — scan
    /// declarations + emit:
    /// <list type="bullet">
    ///   <item><c>Recoveries</c>: the recovered declarations (each
    ///   carrying its own <see cref="CssDeclarationRecovery.SourceOrdinal"/>);
    ///   shorthand expansions emit multiple records that share an
    ///   ordinal.</item>
    ///   <item><c>ExplicitLonghands</c>: every EXPLICIT longhand
    ///   declaration (= NOT a shorthand expansion) with its source
    ///   ordinal + importance flag. The merge in
    ///   <see cref="CssParserAdapter.AdaptDeclarationsWithRecovery"/>
    ///   uses this list to apply CSS Cascade §5 importance + §7.4
    ///   source-order rules when a shorthand-expansion recovery
    ///   conflicts with an explicit longhand.</item>
    /// </list>
    /// Allocation profile: the explicit-longhand list is built
    /// LAZILY (= no allocation until the first shorthand expansion
    /// is detected, since the list is only consumed for rules that
    /// contain shorthand recovery).</summary>
    private static (ImmutableArray<CssDeclarationRecovery> Recoveries,
        ImmutableArray<ExplicitLonghandRef> ExplicitLonghands)
        ScanDeclarations(string body, bool modernOnly)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (ImmutableArray<CssDeclarationRecovery>.Empty,
                ImmutableArray<ExplicitLonghandRef>.Empty);

        var output = ImmutableArray.CreateBuilder<CssDeclarationRecovery>();
        // Per Phase 3 Task 15 L17 post-PR-#77 — lazily-allocated state
        // for cascade-correct merge:
        //   - explicitLonghands: the per-rule list of explicit (non-
        //     shorthand-expansion) longhand declarations w/ their
        //     source ordinals + importance flags. Allocated lazily on
        //     first need; the typical rule (no shorthands or only
        //     shorthands) doesn't allocate this list.
        // Per PR-#91 review F3 — explicit longhands are recorded
        // UNCONDITIONALLY (= the prior `sawShorthand` gate had a
        // correctness gap for `longhand !important; shorthand`
        // ordering). The list is still lazily allocated so
        // shorthand-and-longhand-free rules don't pay; rules with at
        // least one declaration of either family pay one small list.
        List<ExplicitLonghandRef>? explicitLonghands = null;
        var ordinal = 0;

        var tok = new CssTokenizer(body.AsSpan(), null);
        tok.SkipWhitespaceAndComments();

        while (!tok.IsEnd)
        {
            var name = tok.ReadIdentifier();
            if (name.IsEmpty)
            {
                // Defensive: advance past stray characters.
                tok.ReadChar();
                continue;
            }
            var propertyName = name.ToString();

            tok.SkipWhitespaceAndComments();
            if (tok.PeekChar() != ':')
            {
                tok.ReadUntilAnyTopLevel(";");
                if (tok.PeekChar() == ';') tok.ReadChar();
                tok.SkipWhitespaceAndComments();
                continue;
            }
            tok.ReadChar(); // consume ':'
            tok.SkipWhitespaceAndComments();

            var valueSpan = tok.ReadUntilAnyTopLevel(";");
            var rawValue = valueSpan.ToString().Trim();

            // Per Task 16 review Rec 1 — also recover declarations whose
            // value contains a multi-arg attr() form. AngleSharp.Css normalizes
            // attr() calls before they reach the cascade, so without this
            // recovery the production path never delivers `attr(name type,
            // fallback)` to CssContentList — the diagnostic
            // CSS-ATTR-MULTI-ARG-UNSUPPORTED-001 would only fire on direct
            // unit-test calls.
            //
            // Per Phase 3 Task 10 cycle 3 review (User #1) — also
            // recover declarations whose property name is in
            // KnownDroppedProperties (CSS Text L3 properties
            // AngleSharp.Css 1.0.0-beta.144 drops). Per User #2 —
            // emit using the legacy-alias-normalized name (so
            // `word-wrap: break-word` lands at the cascade as
            // `overflow-wrap: break-word`).
            var lowerName = propertyName.ToLowerInvariant();
            var isKnownDropped = KnownDroppedProperties.Contains(lowerName);
            var include = modernOnly
                ? ContainsModernValueFunction(rawValue)
                  || ContainsMultiArgAttr(rawValue)
                  || isKnownDropped
                : !string.IsNullOrEmpty(rawValue);
            if (include)
            {
                var (cleanValue, isImportant) = ImportantParser.Strip(rawValue);
                var normalizedName = NormalizePropertyName(lowerName);

                // Per Phase 3 Task 15 L13 — the `flex` shorthand
                // expands into THREE longhand recovery records per
                // CSS Flexbox L1 §7.4. AngleSharp.Css 1.0.0-beta.144
                // only partially handles the shorthand, so emitting
                // explicit longhand declarations via the recovery
                // path guarantees the cascade sees the correct
                // (grow, shrink, basis) tuple.
                if (normalizedName == "flex"
                    && FlexShorthandExpander.TryExpand(
                        cleanValue,
                        out var fGrow,
                        out var fShrink,
                        out var fBasis))
                {
                    // Per Phase 3 Task 15 L17 post-PR-#77 — emit 3
                    // longhand recovery records, all sharing the
                    // SAME source ordinal (= they expanded from this
                    // one shorthand declaration).
                    output.Add(new CssDeclarationRecovery(
                        "flex-grow", fGrow, isImportant,
                        IsFromShorthandExpansion: true,
                        SourceOrdinal: ordinal));
                    output.Add(new CssDeclarationRecovery(
                        "flex-shrink", fShrink, isImportant,
                        IsFromShorthandExpansion: true,
                        SourceOrdinal: ordinal));
                    output.Add(new CssDeclarationRecovery(
                        "flex-basis", fBasis, isImportant,
                        IsFromShorthandExpansion: true,
                        SourceOrdinal: ordinal));
                }
                else if (normalizedName == "flex-flow"
                    && FlexFlowShorthandExpander.TryExpand(
                        cleanValue,
                        out var ffDir,
                        out var ffWrap))
                {
                    // Per Phase 3 Task 15 L17 post-PR-#77 — emit 2
                    // longhand recovery records sharing the same
                    // source ordinal.
                    output.Add(new CssDeclarationRecovery(
                        "flex-direction", ffDir, isImportant,
                        IsFromShorthandExpansion: true,
                        SourceOrdinal: ordinal));
                    output.Add(new CssDeclarationRecovery(
                        "flex-wrap", ffWrap, isImportant,
                        IsFromShorthandExpansion: true,
                        SourceOrdinal: ordinal));
                }
                // Per Phase 3 Task 17 cycle 0c — grid-row / grid-column
                // shorthands per CSS Grid L1 §8.4. Each expands into a
                // (start, end) pair of longhand recovery records sharing
                // the source ordinal.
                else if (normalizedName == "grid-row")
                {
                    if (GridLineShorthandExpander.TryExpand(
                        cleanValue, out var grStart, out var grEnd))
                    {
                        output.Add(new CssDeclarationRecovery(
                            "grid-row-start", grStart, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-row-end", grEnd, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                    }
                    else
                    {
                        // Per PR-#91 review F1 — atomic invalidation.
                        // AngleSharp.Css may emit per-longhand declarations
                        // natively for grid-row (= per-longhand validation
                        // means valid components survive while invalid ones
                        // drop, partially applying the shorthand). Per CSS
                        // Cascade L4 §4.2, an invalid shorthand contributes
                        // none of its longhands. Override AngleSharp's emits
                        // with longhand recovery records carrying the RAW
                        // shorthand value (= contains '/'); the
                        // GridLineResolver rejects them as Invalid + the
                        // cascade falls back to the property initial value
                        // (auto). NB: IsFromShorthandExpansion=false makes
                        // the override unconditional vs the per-source-ordinal
                        // arbitration (= we WANT to override AngleSharp's
                        // emit unconditionally; that's the whole point).
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-row-start", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-row-end", cleanValue, isImportant, ordinal);
                    }
                }
                else if (normalizedName == "grid-column")
                {
                    if (GridLineShorthandExpander.TryExpand(
                        cleanValue, out var gcStart, out var gcEnd))
                    {
                        output.Add(new CssDeclarationRecovery(
                            "grid-column-start", gcStart, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-column-end", gcEnd, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                    }
                    else
                    {
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-column-start", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-column-end", cleanValue, isImportant, ordinal);
                    }
                }
                // Per Phase 3 Task 17 cycle 0c — grid-area shorthand
                // per §8.4. Expands into all FOUR grid-line longhands.
                else if (normalizedName == "grid-area")
                {
                    if (GridAreaShorthandExpander.TryExpand(
                        cleanValue,
                        out var gaRowStart,
                        out var gaColumnStart,
                        out var gaRowEnd,
                        out var gaColumnEnd))
                    {
                        output.Add(new CssDeclarationRecovery(
                            "grid-row-start", gaRowStart, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-column-start", gaColumnStart, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-row-end", gaRowEnd, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-column-end", gaColumnEnd, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                    }
                    else
                    {
                        // Per PR-#91 review F1 — atomic invalidation for
                        // grid-area (see grid-row branch for rationale).
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-row-start", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-column-start", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-row-end", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-column-end", cleanValue, isImportant, ordinal);
                    }
                }
                // Per Phase 3 Task 18 cycle 8 — the `grid` shorthand
                // per §7.4. Expands into all six grid-template-* +
                // grid-auto-* longhands. The §7.4 reset rule applies
                // (= longhands not set by the matched form reset to
                // their initial values), so even the
                // <c>&lt;rows&gt; / &lt;columns&gt;</c> form emits
                // explicit `auto`/`row`/`none` for the unmentioned
                // auto-* + template-areas longhands.
                else if (normalizedName == "grid")
                {
                    if (GridShorthandExpander.TryExpand(
                        cleanValue,
                        out var gTemplateRows,
                        out var gTemplateColumns,
                        out var gTemplateAreas,
                        out var gAutoRows,
                        out var gAutoColumns,
                        out var gAutoFlow))
                    {
                        output.Add(new CssDeclarationRecovery(
                            "grid-template-rows", gTemplateRows, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-template-columns", gTemplateColumns, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-template-areas", gTemplateAreas, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-auto-rows", gAutoRows, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-auto-columns", gAutoColumns, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                        output.Add(new CssDeclarationRecovery(
                            "grid-auto-flow", gAutoFlow, isImportant,
                            IsFromShorthandExpansion: true,
                            SourceOrdinal: ordinal));
                    }
                    else
                    {
                        // Atomic invalidation per CSS Cascade L4 §4.2 +
                        // PR-#91 review F1 — emit invalid-sentinel
                        // longhands carrying the raw shorthand value
                        // so each longhand's resolver rejects them
                        // and the cascade falls back to initial values.
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-template-rows", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-template-columns", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-template-areas", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-auto-rows", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-auto-columns", cleanValue, isImportant, ordinal);
                        EmitInvalidGridShorthandRecovery(output,
                            "grid-auto-flow", cleanValue, isImportant, ordinal);
                    }
                }
                else
                {
                    // Non-shorthand recovery (modern colors,
                    // align-items compounds, etc.). Carries its own
                    // source ordinal for completeness; the merge
                    // doesn't use the ordinal for these (override is
                    // unconditional).
                    output.Add(new CssDeclarationRecovery(
                        normalizedName,
                        cleanValue,
                        isImportant,
                        SourceOrdinal: ordinal));
                    // Per PR-#91 review F3 — track explicit longhands
                    // UNCONDITIONALLY (not gated on a prior shorthand).
                    // The L17-original `if (sawShorthand)` gate was a
                    // micro-optimization that broke importance precedence
                    // for the `longhand !important; shorthand` ordering
                    // (= the earlier important longhand was unrecorded so
                    // the later normal shorthand could replace it). The
                    // merge logic in CssParserAdapter already gates the
                    // override decision on importance + source ordinal;
                    // the preprocessor's job is to record ALL explicit
                    // longhands so the merge can compare. The minor
                    // allocation cost for shorthand-free rules is a fair
                    // trade for correctness.
                    explicitLonghands ??= new List<ExplicitLonghandRef>();
                    explicitLonghands.Add(new ExplicitLonghandRef(
                        normalizedName, ordinal, isImportant));
                }
            }
            else
            {
                // Per PR-#91 review F3 — even when the declaration is
                // excluded from recovery (= the typical case for explicit
                // longhands AngleSharp handles natively), it still counts
                // as an explicit longhand for source-order comparison
                // against any subsequent OR prior shorthand expansion.
                // Tracking unconditionally (= removing the prior
                // sawShorthand gate) catches the
                // `longhand !important; shorthand` ordering case where
                // the earlier important longhand must beat the later
                // normal shorthand.
                var (excludedClean, excludedImportant) = ImportantParser.Strip(rawValue);
                _ = excludedClean; // value text not needed; merge reads from AngleSharp
                var unconditionalNormalizedName = NormalizePropertyName(lowerName);
                explicitLonghands ??= new List<ExplicitLonghandRef>();
                explicitLonghands.Add(new ExplicitLonghandRef(
                    unconditionalNormalizedName, ordinal, excludedImportant));
            }

            ordinal++;
            if (tok.PeekChar() == ';') tok.ReadChar();
            tok.SkipWhitespaceAndComments();
        }

        return (
            output.Count == 0
                ? ImmutableArray<CssDeclarationRecovery>.Empty
                : output.ToImmutable(),
            explicitLonghands is null
                ? ImmutableArray<ExplicitLonghandRef>.Empty
                : explicitLonghands.ToImmutableArray());
    }

    /// <summary>Per Phase 3 Task 17 cycle 0c post-PR-#91 review F1 —
    /// emit a longhand recovery record for the named property carrying
    /// the RAW (invalid) shorthand value. The GridLineResolver rejects
    /// this value as Invalid (= the raw shorthand contains <c>/</c> which
    /// is not valid in <c>&lt;grid-line&gt;</c> grammar); the cascade
    /// then falls back to the property initial value (auto), which is
    /// the spec-correct behavior for an invalid shorthand per CSS Cascade
    /// L4 §4.2.
    ///
    /// <para><b>Why this overrides AngleSharp:</b>
    /// <c>IsFromShorthandExpansion=false</c> makes the override
    /// unconditional in <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c>
    /// (= we deliberately want to override AngleSharp's per-longhand
    /// emit; that's the whole point — AngleSharp would otherwise apply
    /// the valid components of the shorthand piecewise, violating the
    /// §4.2 atomicity rule).</para>
    ///
    /// <para><b>Known limitation</b>: a prior valid same-rule longhand
    /// declaration (e.g., <c>grid-row-start: 3; grid-row: 2 / 0;</c>)
    /// cannot be preserved because AngleSharp's per-rule property
    /// dedup discards the earlier value before our recovery merges.
    /// Spec says the invalid shorthand should drop + the earlier 3 win;
    /// our cycle-0c implementation drops both to initial value (auto).
    /// Documented in deferrals.md.</para></summary>
    private static void EmitInvalidGridShorthandRecovery(
        ImmutableArray<CssDeclarationRecovery>.Builder output,
        string longhandName,
        string rawShorthandValue,
        bool isImportant,
        int sourceOrdinal)
    {
        output.Add(new CssDeclarationRecovery(
            longhandName,
            rawShorthandValue,
            isImportant,
            IsFromShorthandExpansion: false,
            SourceOrdinal: sourceOrdinal));
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="value"/> contains a call to one of
    /// the modern CSS value functions (case-insensitive, outside strings/comments).
    /// </summary>
    private static bool ContainsModernValueFunction(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var tok = new CssTokenizer(value.AsSpan(), null);
        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == '\'' || c == '"') { tok.SkipString(); continue; }
            if (c == '/' && tok.PeekCharAt(1) == '*') { tok.SkipWhitespaceAndComments(); continue; }
            if (IsIdentifierStart(c))
            {
                var ident = tok.ReadIdentifier();
                if (tok.PeekChar() == '(')
                {
                    if (ModernValueFunctions.Contains(ident.ToString())) return true;
                    // Skip the function args; ReadParenthesizedBlock handles balance.
                    tok.ReadParenthesizedBlock();
                }
                continue;
            }
            tok.ReadChar();
        }
        return false;
    }

    /// <summary>
    /// Per Task 16 review Rec 1 — <see langword="true"/> when
    /// <paramref name="value"/> contains an <c>attr()</c> call with the
    /// modern multi-arg shape (<c>attr(name type)</c>, <c>attr(name, fallback)</c>,
    /// <c>attr(name type, fallback)</c>). The bare <c>attr(name)</c> form
    /// returns <see langword="false"/> — it doesn't need recovery since
    /// AngleSharp passes single-arg <c>attr()</c> through cleanly.
    /// </summary>
    private static bool ContainsMultiArgAttr(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var tok = new CssTokenizer(value.AsSpan(), null);
        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == '\'' || c == '"') { tok.SkipString(); continue; }
            if (c == '/' && tok.PeekCharAt(1) == '*') { tok.SkipWhitespaceAndComments(); continue; }
            if (IsIdentifierStart(c))
            {
                var ident = tok.ReadIdentifier();
                if (tok.PeekChar() == '(')
                {
                    var argBlock = tok.ReadParenthesizedBlock();
                    if (IsAttrIdent(ident) && IsMultiArgAttrBlock(argBlock)) return true;
                }
                continue;
            }
            tok.ReadChar();
        }
        return false;
    }

    /// <summary>Case-insensitive ASCII match against the literal <c>attr</c>
    /// (4 chars). The modern <c>attr()</c> in CSS Values L5 is the only
    /// shape we need to distinguish here — the legacy single-arg form
    /// is a subset that AngleSharp handles cleanly.</summary>
    private static bool IsAttrIdent(System.ReadOnlySpan<char> ident) =>
        ident.Length == 4
        && (ident[0] | 0x20) == 'a'
        && (ident[1] | 0x20) == 't'
        && (ident[2] | 0x20) == 't'
        && (ident[3] | 0x20) == 'r';

    /// <summary>Detect the multi-arg <c>attr()</c> shape from the
    /// already-balanced parenthesized block (which includes the surrounding
    /// parens per <see cref="CssTokenizer.ReadParenthesizedBlock"/>).
    /// Returns <see langword="true"/> when the block contains a comma OR a
    /// whitespace-separated token after the first ident — both indicate the
    /// modern <c>attr(name type)</c> / <c>attr(name, fallback)</c> /
    /// <c>attr(name type, fallback)</c> form.</summary>
    private static bool IsMultiArgAttrBlock(System.ReadOnlySpan<char> block)
    {
        if (block.Length < 2 || block[0] != '(' || block[^1] != ')') return false;
        var body = block[1..^1];

        // Comma → unambiguously multi-arg.
        if (body.IndexOf(',') >= 0) return true;

        // Whitespace-separated tokens → multi-arg. Trim, find first
        // whitespace, check whether non-whitespace content follows.
        body = body.Trim();
        if (body.IsEmpty) return false;
        var firstWs = -1;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] is ' ' or '\t' or '\n' or '\r' or '\f') { firstWs = i; break; }
        }
        if (firstWs < 0) return false;
        for (var i = firstWs + 1; i < body.Length; i++)
        {
            if (body[i] is not (' ' or '\t' or '\n' or '\r' or '\f')) return true;
        }
        return false;
    }

    private static string ReadImportUrl(ref CssTokenizer tok)
    {
        var c = tok.PeekChar();
        if (c == '\'' || c == '"')
        {
            return ReadQuotedString(ref tok);
        }

        if (PeekKeyword(ref tok, "url") && tok.PeekCharAt(3) == '(')
        {
            tok.ReadChar(); tok.ReadChar(); tok.ReadChar();
            var paren = tok.ReadParenthesizedBlock();
            var inner = TrimSurroundingParens(paren).Trim();
            if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[^1] == inner[0])
                inner = inner[1..^1];
            return inner.ToString();
        }

        var fallback = tok.ReadUntilAnyTopLevel(" \t\r\n;").ToString().Trim();
        return fallback;
    }

    private static string ReadQuotedString(ref CssTokenizer tok)
    {
        var quote = tok.PeekChar();
        if (quote != '\'' && quote != '"') return string.Empty;
        tok.ReadChar();
        var contentStart = tok.Position;
        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == '\\')
            {
                tok.ReadChar();
                if (!tok.IsEnd) tok.ReadChar();
                continue;
            }
            if (c == quote)
            {
                var content = tok.GetSubstring(contentStart, tok.Position - contentStart);
                tok.ReadChar();
                return content;
            }
            if (c == '\n')
            {
                return tok.GetSubstring(contentStart, tok.Position - contentStart);
            }
            tok.ReadChar();
        }
        return tok.GetSubstring(contentStart, tok.Position - contentStart);
    }

    private static bool TryConsumeKeyword(ref CssTokenizer tok, string keyword)
    {
        if (!PeekKeyword(ref tok, keyword)) return false;
        for (var i = 0; i < keyword.Length; i++) tok.ReadChar();
        return true;
    }

    private static bool PeekKeyword(ref CssTokenizer tok, string keyword)
    {
        for (var i = 0; i < keyword.Length; i++)
        {
            var c = tok.PeekCharAt(i);
            if (char.ToLowerInvariant(c) != char.ToLowerInvariant(keyword[i])) return false;
        }
        var nextChar = tok.PeekCharAt(keyword.Length);
        return !IsIdentifierContinue(nextChar);
    }

    private static ReadOnlySpan<char> TrimSurroundingParens(ReadOnlySpan<char> input)
    {
        if (input.Length >= 2 && input[0] == '(' && input[^1] == ')')
            return input[1..^1];
        return input;
    }

    private static bool IsIdentifierStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-';

    private static bool IsIdentifierContinue(char c) =>
        IsIdentifierStart(c) || (c >= '0' && c <= '9');
}
