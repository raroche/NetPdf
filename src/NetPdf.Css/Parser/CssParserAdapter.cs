// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf.Css.Parser.Preprocessing;

namespace NetPdf.Css.Parser;

/// <summary>
/// Translates AngleSharp.Css's CSSOM tree (<see cref="ICssStyleSheet"/>) into NetPdf's
/// internal AST (<see cref="CssStylesheet"/> / <see cref="CssRule"/> hierarchy). This is the
/// firewall between AngleSharp.Css types and the rest of NetPdf — every downstream stage
/// (cascade, computed values, box generation) consumes the NetPdf types only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inputs.</b> The <c>HtmlParsingHost</c> in the <c>NetPdf</c> facade configures
/// AngleSharp with <c>WithCss()</c>, which populates <c>document.StyleSheets</c> with one
/// <see cref="ICssStyleSheet"/> per <c>&lt;style&gt;</c> element and per resolved
/// <c>&lt;link rel="stylesheet"&gt;</c>. The host disables resource loading, so external
/// stylesheets are present in the CSSOM but their content is empty until later phase tasks
/// wire <c>HtmlPdfOptions.ResourceLoader</c>.
/// </para>
/// <para>
/// <b>Inline styles.</b> Element <c>style="..."</c> attributes are not part of any sheet —
/// each is its own cascade input. Use <see cref="AdaptInlineStyle"/> to translate the
/// declarations from an <see cref="ICssStyleDeclaration"/> obtained via
/// <c>IElement.GetStyle()</c> on a parsed DOM element. The cascade resolver (Task 7) walks
/// the DOM and pulls inline styles at iteration time so the AngleSharp DOM boundary stays
/// inside <c>NetPdf</c> + <c>NetPdf.Css.Parser</c>.
/// </para>
/// <para>
/// <b>Fidelity notes.</b> AngleSharp.Css normalizes during parsing. The visible effects:
/// </para>
/// <list type="bullet">
///   <item><description>Named colors expand to <c>rgba(r, g, b, a)</c> form.</description></item>
///   <item><description>Shorthand declarations expand to longhands —
///   <c>background: url('x') no-repeat</c> becomes 12 separate <c>background-*</c> declarations,
///   some with <c>initial</c> values that did not appear in the source. Beneficial downstream
///   (cascade operates on longhands).</description></item>
///   <item><description>Whitespace around combinators is canonicalized
///   (<c>.a &gt; .b</c> → <c>.a&gt;.b</c>).</description></item>
/// </list>
/// <para>
/// <b>Known AngleSharp.Css 1.0.0-beta.144 gaps</b> that Phase 2 Task 3's pre-pass tokenizer
/// is responsible for closing:
/// </para>
/// <list type="bullet">
///   <item><description><b>@page selector loss.</b> <c>@page :first { … }</c> /
///   <c>@page :left</c> / <c>@page :right</c> / <c>@page name</c> all surface with empty
///   <c>SelectorText</c>. The selector vanishes before the CSSOM. Task 3 must extract the
///   selector before AngleSharp parses the rule.</description></item>
///   <item><description><b>@page margin-box loss.</b> Margin-box at-rules
///   (<c>@top-center</c>, <c>@bottom-right-corner</c>, …) inside an <c>@page</c> are silently
///   dropped — they reach neither the parent <c>ICssPageRule</c> nor the top level.
///   <see cref="ICssMarginRule"/> is defined in AngleSharp but never instantiated by the
///   current parser. The dispatch case below stays for forward compatibility.</description></item>
///   <item><description><b>@import modern syntax loss.</b> <c>@import url(...) layer(name)</c>
///   and <c>@import url(...) supports(...)</c> survive the parser only as a malformed media
///   query (<c>"not all"</c>). The layer name and supports condition cannot be recovered
///   from the CSSOM. <see cref="CssImportRule.LayerName"/> and
///   <see cref="CssImportRule.SupportsCondition"/> stay <see langword="null"/> in Task 2.</description></item>
/// </list>
/// </remarks>
internal static class CssParserAdapter
{
    /// <summary>
    /// Adapts a parsed AngleSharp.Css stylesheet into NetPdf's internal AST with default
    /// metadata (no href, author origin, unknown owner kind, no media filter, enabled,
    /// order 0). Most callers that have richer context should use
    /// <see cref="Adapt(ICssStyleSheet, string?, CssStylesheetOrigin, CssStylesheetOwnerKind, string?, bool, int)"/>.
    /// </summary>
    public static CssStylesheet Adapt(ICssStyleSheet sheet) =>
        Adapt(sheet, href: null, origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.Unknown, mediaQuery: null,
            isDisabled: false, order: 0);

    /// <summary>
    /// Adapts a parsed AngleSharp.Css stylesheet, attaching cascade-relevant metadata so
    /// downstream stages can filter by media, sort by source order, and resolve relative
    /// URLs against the sheet's <paramref name="href"/>.
    /// </summary>
    public static CssStylesheet Adapt(
        ICssStyleSheet sheet,
        string? href,
        CssStylesheetOrigin origin,
        CssStylesheetOwnerKind ownerKind,
        string? mediaQuery,
        bool isDisabled,
        int order)
        => Adapt(sheet, CssPreprocessResult.Empty, href, origin, ownerKind, mediaQuery, isDisabled, order);

    /// <summary>
    /// Adapts a parsed AngleSharp.Css stylesheet with Phase 2 Task 3 pre-pass recoveries
    /// merged in: <c>@page</c> selectors and margin-boxes that AngleSharp drops, modern
    /// <c>@import</c> clauses (<c>layer</c> / <c>supports</c>), and rule source positions.
    /// Caller obtains <paramref name="preprocess"/> by running
    /// <see cref="Preprocessing.CssPreprocessor.Process"/> over the raw CSS text BEFORE
    /// AngleSharp parses it (e.g., from <c>&lt;style&gt;.TextContent</c>); the AngleSharp
    /// pass and the pre-pass run independently and are aligned by ordinal index here.
    /// </summary>
    public static CssStylesheet Adapt(
        ICssStyleSheet sheet,
        CssPreprocessResult preprocess,
        string? href,
        CssStylesheetOrigin origin,
        CssStylesheetOwnerKind ownerKind,
        string? mediaQuery,
        bool isDisabled,
        int order)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(preprocess);

        // CssStylesheet.Location is the source position of the OWNING <style>/<link>
        // element in the host HTML document — that's an HTML-level position, not a CSS
        // one. The pre-pass only sees the CSS text, not the HTML around it, so we leave
        // this as Unknown until the host plumbs the HTML position through (Phase 2 Task 12+
        // wiring). The preprocess.RuleSlots[0].Location is the first rule's CSS-internal
        // position — different concept entirely.
        return new CssStylesheet(
            Rules: AdaptTopLevelRules(sheet.Rules, preprocess),
            Href: href,
            Origin: origin,
            OwnerKind: ownerKind,
            MediaQuery: mediaQuery,
            IsDisabled: isDisabled,
            Order: order,
            Location: CssSourceLocation.Unknown);
    }

    /// <summary>
    /// Adapts an inline style declaration block (<c>style="..."</c> attribute on a DOM
    /// element) into a flat list of <see cref="CssDeclaration"/>. Inline styles are a
    /// distinct cascade input from stylesheets — they have no selector (they target the
    /// owning element directly) and follow CSS Cascade L4 §6.4.4 specificity rules. The
    /// caller (the cascade resolver in Task 7) supplies the owning element from the DOM
    /// walk; this method only handles the declarations.
    /// </summary>
    public static ImmutableArray<CssDeclaration> AdaptInlineStyle(ICssStyleDeclaration style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return AdaptProperties(style);
    }

    /// <summary>
    /// Adapts an inline style block where the raw <c>style="..."</c> text is also
    /// available, running it through the same recovery layer as <c>&lt;style&gt;</c>
    /// blocks. Per Phase 2 deep review Rec 3 — without this, inline modern colors
    /// (<c>style="color: oklch(...)"</c>) and multi-arg <c>attr()</c> forms were lost
    /// or misdiagnosed because the typed pipeline only ever saw AngleSharp.Css
    /// 1.0.0-beta.144's normalized output. The recovery scan finds modern
    /// declarations in <paramref name="rawStyleText"/> and merges them over the
    /// AngleSharp-parsed declarations using the same priority rules as
    /// <see cref="AdaptDeclarationsWithRecovery"/>.
    /// </summary>
    public static ImmutableArray<CssDeclaration> AdaptInlineStyleWithRecovery(
        ICssStyleDeclaration style,
        string? rawStyleText,
        CssSourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (string.IsNullOrWhiteSpace(rawStyleText)) return AdaptProperties(style);

        // Per Phase 3 Task 15 L17 post-PR-#77 review #1 (P1) — inline
        // styles MUST use the order-aware scan so the cascade-correct
        // merge sees the explicit-longhand source positions for
        // shorthand-vs-longhand conflicts. Pre-fix the inline path
        // dropped the ordinal info → recovery's shorthand expansion
        // silently overrode a later explicit longhand in
        // `style="flex-flow: row wrap; flex-wrap: nowrap"`.
        var (recoveredDeclarations, explicitLonghands) =
            CssPreprocessor.ScanForModernDeclarationsWithOrder(rawStyleText);
        if (recoveredDeclarations.IsEmpty) return AdaptProperties(style);

        var recovery = new CssStyleRuleRecovery(
            OrdinalIndex: 0,
            Declarations: recoveredDeclarations,
            ExplicitLonghandOrdinals: explicitLonghands);
        return AdaptDeclarationsWithRecovery(style, recovery, location);
    }

    /// <summary>
    /// Walks the top-level rules. The slot list from the preprocessor is the source of
    /// truth for source order. The merge strategy is robust against drift: each slot is
    /// tentatively paired with the next AngleSharp rule, but if the rule kinds don't match
    /// (because AngleSharp dropped that rule type), the slot is demoted to opaque and the
    /// AngleSharp cursor doesn't advance. This means future AngleSharp regressions that
    /// drop new rule types won't reintroduce ordinal drift.
    /// </summary>
    private static ImmutableArray<CssRule> AdaptTopLevelRules(ICssRuleList rules, CssPreprocessResult preprocess)
    {
        if (preprocess.RuleSlots.IsEmpty) return AdaptRulesWithoutPreprocess(rules);

        var output = ImmutableArray.CreateBuilder<CssRule>(Math.Max(rules.Length, preprocess.RuleSlots.Length));
        var ctx = new MergeContext { AngleIdx = 0 };

        foreach (var slot in preprocess.RuleSlots)
        {
            output.Add(AdaptSlot(slot, rules, preprocess, ref ctx));
        }

        // Defensive: AngleSharp emitted more rules than the slot list predicted. Append
        // them with Unknown positions rather than dropping them.
        while (ctx.AngleIdx < rules.Length)
        {
            var rule = rules[ctx.AngleIdx++];
            if (rule is null) continue;
            output.Add(AdaptAngleSharpRule(rule, preprocess, ref ctx, CssSourceLocation.Unknown));
        }

        return output.ToImmutable();
    }

    /// <summary>
    /// Pairs a single slot with AngleSharp's next rule when the kinds align; otherwise
    /// emits an opaque <see cref="CssAtRule"/> using the slot's metadata. For grouping
    /// at-rules whose slot has nested slots, recurses into the body using the same merge
    /// strategy on the AngleSharp grouping rule's children.
    /// </summary>
    private static CssRule AdaptSlot(
        CssRuleSlot slot,
        ICssRuleList rules,
        CssPreprocessResult preprocess,
        ref MergeContext ctx)
    {
        if (ctx.AngleIdx < rules.Length)
        {
            var ang = rules[ctx.AngleIdx];
            if (ang is not null && SlotMatchesAngleSharpRule(slot, ang))
            {
                ctx.AngleIdx++;
                return AdaptAngleSharpRuleWithSlot(ang, slot, preprocess, ref ctx);
            }
        }
        // Mismatch (or no AngleSharp rule left): emit opaque from the slot's metadata.
        return EmitOpaqueFromSlot(slot);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the AngleSharp rule's interface matches the slot's
    /// expected kind AND (for at-rules) at-keyword. Mismatches return <see langword="false"/>
    /// so the caller can demote the slot to opaque without advancing the AngleSharp cursor —
    /// this is what prevents drift when AngleSharp drops a rule type the preprocessor
    /// expected it to emit. Unknown-on-both-sides at-keywords pair only when neither side
    /// recognizes the keyword (i.e., AngleSharp emitted a generic at-rule kind we didn't
    /// special-case). That keeps novel at-rules pairable while still rejecting the case
    /// where AngleSharp dropped the slot's rule and gave us something different instead.
    /// </summary>
    private static bool SlotMatchesAngleSharpRule(CssRuleSlot slot, ICssRule ang)
    {
        if (slot.Kind == CssRuleSlotKind.StyleRule)
        {
            if (ang is not ICssStyleRule styleRule) return false;
            // Per Phase 2 deep review Rec 1 — kind-only matching let a
            // dropped style rule (e.g., `li::marker { ... }` in AngleSharp.Css
            // 1.0.0-beta.144) consume the NEXT style rule in source order,
            // corrupting source locations + recovery ordinals downstream.
            // Two-tier match:
            //   (1) AngleSharp emitted an empty selector — its parser failed
            //       on at least one selector token (e.g., `::marker`) but
            //       kept the declaration body. Pair with slot so the slot's
            //       authored selector + AngleSharp's parsed declarations
            //       merge — preserves both the source location AND the body.
            //   (2) AngleSharp's selector matches the slot's normalized
            //       prelude — happy path.
            //   (3) Otherwise the slot's selector + AngleSharp's selector
            //       describe DIFFERENT rules (real drift); demote slot to
            //       opaque without advancing AngleSharp.
            if (string.IsNullOrEmpty(styleRule.SelectorText)) return true;
            return SelectorTextEquivalent(slot.Prelude, styleRule.SelectorText);
        }

        if (ang is ICssStyleRule) return false; // slot expects at-rule but AngleSharp gave us a style rule

        var expectedKind = slot.AtKeyword;
        if (string.IsNullOrEmpty(expectedKind)) return true;

        // Strict at-keyword match: derive AngleSharp's rule keyword and compare.
        var actualKind = AngleSharpAtKeyword(ang);
        if (string.IsNullOrEmpty(actualKind))
            // Both sides consider this rule a generic at-rule — pair them. Novel AngleSharp
            // at-rule support (e.g., a future @scope) flows through this path.
            return true;
        return string.Equals(expectedKind, actualKind, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps an AngleSharp <see cref="ICssRule"/> to its at-keyword (without leading <c>@</c>),
    /// or <see cref="string.Empty"/> if it's a generic at-rule the dispatch doesn't recognize.
    /// </summary>
    private static string AngleSharpAtKeyword(ICssRule rule) => rule switch
    {
        ICssMediaRule => "media",
        ICssSupportsRule => "supports",
        ICssKeyframesRule => "keyframes",
        ICssKeyframeRule => string.Empty, // not a top-level at-rule — only appears inside @keyframes
        ICssFontFaceRule => "font-face",
        ICssPageRule => "page",
        ICssMarginRule => string.Empty, // not a top-level at-rule
        ICssImportRule => "import",
        ICssCharsetRule => "charset",
        ICssNamespaceRule => "namespace",
        ICssStyleRule => string.Empty,
        _ => string.Empty,
    };

    private static CssRule EmitOpaqueFromSlot(CssRuleSlot slot)
    {
        if (slot.Kind == CssRuleSlotKind.StyleRule)
        {
            // Per Phase 2 deep review Rec 2 — recover the dropped rule's
            // declarations from RawBody so downstream stages (CascadeResolver +
            // CssContentList + ColorResolver + the diagnostic dispatcher) can
            // still process them. Without this, a dropped rule like
            // `li::marker { content: counter(items); color: red }` lost its
            // body entirely + the spec-required CSS-CONTENT-FUNCTION-UNSUPPORTED-001
            // emission was unreachable through the production path. The scanner
            // is forgiving (skips malformed declarations) and lower-cases
            // property names per CSS Syntax §2.
            var recovery = CssPreprocessor.ScanAllDeclarations(slot.RawBody);
            var declarations = recovery.IsEmpty
                ? ImmutableArray<CssDeclaration>.Empty
                : ToCssDeclarations(recovery, slot.Location);
            return new CssStyleRule(
                Selector: new CssSelector(slot.Prelude),
                Declarations: declarations,
                Location: slot.Location);
        }
        return new CssAtRule(
            Name: slot.AtKeyword,
            Prelude: slot.Prelude,
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: slot.Location,
            RawBody: slot.RawBody);
    }

    private static ImmutableArray<CssDeclaration> ToCssDeclarations(
        ImmutableArray<CssDeclarationRecovery> recovery,
        CssSourceLocation location)
    {
        var output = ImmutableArray.CreateBuilder<CssDeclaration>(recovery.Length);
        foreach (var rec in recovery)
        {
            output.Add(new CssDeclaration(
                Property: rec.Property,
                Value: new CssValue(rec.RawValueText),
                IsImportant: rec.IsImportant,
                Location: location));
        }
        return output.ToImmutable();
    }

    /// <summary>Returns <see langword="true"/> if two selector strings represent the
    /// same selector after collapsing whitespace runs to a single space + trimming
    /// ends. CSS selectors are case-sensitive (per CSS Selectors L4 §2 — element
    /// names are case-insensitive in HTML but case-sensitive in XML; class / id /
    /// pseudo-* tokens are always case-sensitive); we compare ordinal so the
    /// strictest of the cases applies. Per Phase 2 deep review Rec 1.</summary>
    private static bool SelectorTextEquivalent(string slotPrelude, string? angleSharpSelector)
    {
        if (string.IsNullOrEmpty(slotPrelude)) return string.IsNullOrEmpty(angleSharpSelector);
        if (string.IsNullOrEmpty(angleSharpSelector)) return false;
        return string.Equals(
            NormalizeSelector(slotPrelude),
            NormalizeSelector(angleSharpSelector),
            StringComparison.Ordinal);
    }

    private static string NormalizeSelector(string s)
    {
        // Collapse internal whitespace runs to a single space; trim ends.
        // Keeps `* > p` and `*>p` identical without altering token semantics.
        var sb = new System.Text.StringBuilder(s.Length);
        var lastWasWhitespace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && !lastWasWhitespace) sb.Append(' ');
                lastWasWhitespace = true;
            }
            else if (ch == '>' || ch == '+' || ch == '~' || ch == ',')
            {
                // Combinator / comma: drop trailing space, append, mark last-not-whitespace.
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                sb.Append(ch);
                lastWasWhitespace = true; // suppress space immediately after.
            }
            else
            {
                sb.Append(ch);
                lastWasWhitespace = false;
            }
        }
        // Trim trailing space if any.
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Adapts nested rules without preprocess data — used for the empty-preprocess fall-back
    /// path and for grouping rules whose slot has no nested slots (typically because the
    /// caller passed a pre-existing <see cref="ICssStyleSheet"/> without preprocessing it).
    /// All locations are <see cref="CssSourceLocation.Unknown"/>.
    /// </summary>
    private static ImmutableArray<CssRule> AdaptRulesWithoutPreprocess(ICssRuleList rules)
    {
        if (rules.Length == 0) return ImmutableArray<CssRule>.Empty;
        var output = ImmutableArray.CreateBuilder<CssRule>(rules.Length);
        var ctx = new MergeContext();
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            output.Add(AdaptAngleSharpRule(rule, CssPreprocessResult.Empty, ref ctx, CssSourceLocation.Unknown));
        }
        return output.ToImmutable();
    }

    /// <summary>
    /// Adapts grouping rule children using a nested slot list when the slot supplied one.
    /// Threads the parent walk's <paramref name="parentCtx"/> through so the global
    /// style-rule / page / import ordinals stay aligned with the preprocessor's globally-
    /// numbered recoveries. Only the local <c>AngleIdx</c> resets — that's the cursor
    /// over <i>this</i> grouping's child <see cref="ICssRuleList"/>, which is independent
    /// of any outer grouping's children.
    /// </summary>
    private static ImmutableArray<CssRule> AdaptChildRules(
        ICssRuleList rules,
        ImmutableArray<CssRuleSlot> nestedSlots,
        CssPreprocessResult preprocess,
        ref MergeContext parentCtx)
    {
        if (nestedSlots.IsEmpty) return AdaptRulesWithoutPreprocess(rules);

        var output = ImmutableArray.CreateBuilder<CssRule>(Math.Max(rules.Length, nestedSlots.Length));
        // Save + restore the parent's AngleIdx so nested AngleIdx counting doesn't leak.
        var savedAngleIdx = parentCtx.AngleIdx;
        parentCtx.AngleIdx = 0;

        foreach (var slot in nestedSlots)
        {
            output.Add(AdaptSlot(slot, rules, preprocess, ref parentCtx));
        }
        while (parentCtx.AngleIdx < rules.Length)
        {
            var rule = rules[parentCtx.AngleIdx++];
            if (rule is null) continue;
            output.Add(AdaptAngleSharpRule(rule, preprocess, ref parentCtx, CssSourceLocation.Unknown));
        }

        parentCtx.AngleIdx = savedAngleIdx;
        return output.ToImmutable();
    }

    /// <summary>
    /// Mutable context carrying the cursors used while walking AngleSharp's emitted rules
    /// and the per-kind recovery counters. Passed by <c>ref</c> through the dispatch so
    /// each branch can advance the appropriate counter without indirection.
    /// </summary>
    private struct MergeContext
    {
        public int AngleIdx;
        public int PageOrdinal;
        public int ImportOrdinal;
        public int StyleRuleOrdinal;
    }

    private static CssRule AdaptAngleSharpRuleWithSlot(
        ICssRule rule,
        CssRuleSlot slot,
        CssPreprocessResult preprocess,
        ref MergeContext ctx)
    {
        switch (rule)
        {
            case ICssStyleRule s:
                return AdaptStyleRule(s, preprocess, slot, ref ctx);
            case ICssMediaRule m:
                return AdaptGroupingRule(m, "media", m.Media?.MediaText ?? string.Empty, m.Rules, slot.NestedSlots, preprocess, ref ctx, slot.Location);
            case ICssSupportsRule s:
                return AdaptGroupingRule(s, "supports", ExtractPrelude(s.CssText, "@supports"), s.Rules, slot.NestedSlots, preprocess, ref ctx, slot.Location);
            case ICssKeyframesRule k:
                return AdaptGroupingRule(k, "keyframes", k.Name ?? string.Empty, k.Rules, slot.NestedSlots, preprocess, ref ctx, slot.Location);
            case ICssKeyframeRule kf:
                return AdaptKeyframeRule(kf, slot.Location);
            case ICssFontFaceRule f:
                return AdaptFontFaceRule(f, slot.Location);
            case ICssPageRule p:
                return AdaptPageRule(p, LookupPageRecovery(preprocess, ctx.PageOrdinal++), slot.Location);
            case ICssMarginRule mr:
                return AdaptMarginRule(mr, slot.Location);
            case ICssImportRule i:
                return AdaptImportRule(i, LookupImportRecovery(preprocess, ctx.ImportOrdinal++), slot.Location);
            case ICssCharsetRule c:
                return AdaptCharsetRule(c, slot.Location);
            case ICssNamespaceRule n:
                return AdaptNamespaceRule(n, slot.Location);
            case ICssGroupingRule g:
                return AdaptGroupingRule(g, NameFromType(g), ExtractPrelude(g.CssText, "@" + NameFromType(g)), g.Rules, slot.NestedSlots, preprocess, ref ctx, slot.Location);
            default:
                return AdaptUnknownRule(rule, slot.Location);
        }
    }

    private static CssRule AdaptAngleSharpRule(
        ICssRule rule,
        CssPreprocessResult preprocess,
        ref MergeContext ctx,
        CssSourceLocation location)
    {
        switch (rule)
        {
            case ICssStyleRule s:
                return AdaptStyleRuleWithoutSlot(s, preprocess, location, ref ctx);
            case ICssMediaRule m:
                return AdaptMediaRule(m, location);
            case ICssSupportsRule s:
                return AdaptSupportsRule(s, location);
            case ICssKeyframesRule k:
                return AdaptKeyframesRule(k, location);
            case ICssKeyframeRule kf:
                return AdaptKeyframeRule(kf, location);
            case ICssFontFaceRule f:
                return AdaptFontFaceRule(f, location);
            case ICssPageRule p:
                return AdaptPageRule(p, LookupPageRecovery(preprocess, ctx.PageOrdinal++), location);
            case ICssMarginRule mr:
                return AdaptMarginRule(mr, location);
            case ICssImportRule i:
                return AdaptImportRule(i, LookupImportRecovery(preprocess, ctx.ImportOrdinal++), location);
            case ICssCharsetRule c:
                return AdaptCharsetRule(c, location);
            case ICssNamespaceRule n:
                return AdaptNamespaceRule(n, location);
            case ICssGroupingRule g:
                return AdaptUnknownGroupingRule(g, location);
            default:
                return AdaptUnknownRule(rule, location);
        }
    }

    private static CssAtRule AdaptGroupingRule(
        ICssRule _rule,
        string name,
        string prelude,
        ICssRuleList children,
        ImmutableArray<CssRuleSlot> nestedSlots,
        CssPreprocessResult preprocess,
        ref MergeContext ctx,
        CssSourceLocation location)
        => new(
            Name: name,
            Prelude: prelude,
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: AdaptChildRules(children, nestedSlots, preprocess, ref ctx),
            Location: location);

    private static CssPageRuleRecovery? LookupPageRecovery(CssPreprocessResult preprocess, int ordinal)
    {
        if (ordinal < 0 || ordinal >= preprocess.PageRecoveries.Length) return null;
        return preprocess.PageRecoveries[ordinal];
    }

    private static CssImportRuleRecovery? LookupImportRecovery(CssPreprocessResult preprocess, int ordinal)
    {
        if (ordinal < 0 || ordinal >= preprocess.ImportRecoveries.Length) return null;
        return preprocess.ImportRecoveries[ordinal];
    }

    private static CssStyleRule AdaptStyleRule(ICssStyleRule rule, CssSourceLocation location)
    {
        // Called from no-preprocess path: ordinal -1 means "no recovery to look up".
        return new CssStyleRule(
            new CssSelector(rule.SelectorText ?? string.Empty),
            AdaptDeclarationsWithRecovery(rule.Style, recovery: null, location),
            location);
    }

    /// <summary>Adapter path for AngleSharp rules that arrived AFTER the slot
    /// list was exhausted (defensive case where AngleSharp emitted more rules
    /// than the preprocessor predicted) — no slot prelude available, so use
    /// AngleSharp's selector text directly.</summary>
    private static CssStyleRule AdaptStyleRuleWithoutSlot(ICssStyleRule rule, CssPreprocessResult preprocess, CssSourceLocation location, ref MergeContext ctx)
    {
        var ordinal = ctx.StyleRuleOrdinal++;
        var recovery = LookupStyleRuleRecovery(preprocess, ordinal);
        return new CssStyleRule(
            new CssSelector(rule.SelectorText ?? string.Empty),
            AdaptDeclarationsWithRecovery(rule.Style, recovery, location),
            location);
    }

    private static CssStyleRule AdaptStyleRule(ICssStyleRule rule, CssPreprocessResult preprocess, CssRuleSlot slot, ref MergeContext ctx)
    {
        var ordinal = ctx.StyleRuleOrdinal++;
        var recovery = LookupStyleRuleRecovery(preprocess, ordinal);
        // Per Phase 2 deep review Rec 1 — when AngleSharp's selector is empty
        // (it failed to parse the selector but kept the body), prefer the
        // slot's authored prelude. This restores the original selector text
        // for `li::marker` etc. which AngleSharp.Css 1.0.0-beta.144 emits as
        // an empty-selector rule.
        var selectorText = !string.IsNullOrEmpty(rule.SelectorText)
            ? rule.SelectorText
            : slot.Prelude;
        return new CssStyleRule(
            new CssSelector(selectorText ?? string.Empty),
            AdaptDeclarationsWithRecovery(rule.Style, recovery, slot.Location),
            slot.Location);
    }

    private static CssStyleRuleRecovery? LookupStyleRuleRecovery(CssPreprocessResult preprocess, int ordinal)
    {
        if (ordinal < 0 || preprocess.StyleRuleRecoveries.IsEmpty) return null;
        // Recoveries are sparse — linear scan is fine for typical document sizes.
        foreach (var rec in preprocess.StyleRuleRecoveries)
        {
            if (rec.OrdinalIndex == ordinal) return rec;
            if (rec.OrdinalIndex > ordinal) break;
        }
        return null;
    }

    /// <summary>
    /// Builds the declaration list for a style rule, merging the AngleSharp output with
    /// any recovered declarations whose authored values use modern CSS functions.
    /// Recovery overrides AngleSharp on duplicate property names; recovery-only properties
    /// (the case where AngleSharp dropped the declaration entirely because of
    /// <c>color-mix</c> / <c>light-dark</c>) are appended.
    /// </summary>
    private static ImmutableArray<CssDeclaration> AdaptDeclarationsWithRecovery(
        ICssStyleDeclaration style,
        CssStyleRuleRecovery? recovery,
        CssSourceLocation location)
    {
        var fromAngleSharp = AdaptProperties(style);
        if (recovery is null || recovery.Declarations.IsEmpty) return fromAngleSharp;

        var output = ImmutableArray.CreateBuilder<CssDeclaration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First pass: emit AngleSharp's declarations, overriding values for any property
        // that has a recovery entry. Per Phase 3 Task 15 L17 post-PR-#77 review #2 (P1)
        // — the merge applies CSS Cascade §5 importance + §7.4 source-order rules
        // properly when a shorthand-expansion recovery conflicts with an explicit
        // longhand. Pre-fix used a per-property set which broke:
        //   - multi-shorthand cases (`flex-flow ...; flex-wrap ...; flex-flow ...`)
        //   - !important interactions (`flex-flow ... !important; flex-wrap ...`)
        // The new fix uses each recovery's `SourceOrdinal` + `IsImportant` and
        // compares against the rule's `ExplicitLonghandOrdinals` list using the
        // <see cref="ShouldShorthandWinAgainstExplicitLonghands"/> cascade helper.
        // For non-shorthand recoveries (modern colors, align-items compounds) the
        // override stays unconditional — those recoveries fix what AngleSharp
        // corrupted entirely.
        var explicitLonghands = recovery.ExplicitLonghandOrdinals.IsDefault
            ? ImmutableArray<ExplicitLonghandRef>.Empty
            : recovery.ExplicitLonghandOrdinals;
        foreach (var decl in fromAngleSharp)
        {
            seen.Add(decl.Property);
            var match = FindRecovery(recovery.Declarations, decl.Property);
            var shouldOverride =
                match is not null
                && (!match.IsFromShorthandExpansion
                    || ShouldShorthandWinAgainstExplicitLonghands(
                        match, explicitLonghands));
            if (shouldOverride)
            {
                output.Add(new CssDeclaration(
                    Property: decl.Property,
                    Value: new CssValue(match!.RawValueText),
                    IsImportant: match.IsImportant,
                    Location: location));
            }
            else
            {
                output.Add(decl);
            }
        }

        // Second pass: append recovery declarations whose property AngleSharp dropped.
        foreach (var rec in recovery.Declarations)
        {
            if (seen.Contains(rec.Property)) continue;
            output.Add(new CssDeclaration(
                Property: rec.Property,
                Value: new CssValue(rec.RawValueText),
                IsImportant: rec.IsImportant,
                Location: location));
        }

        return output.ToImmutable();
    }

    /// <summary>Per Phase 3 Task 10 cycle 3b review (User #1) —
    /// returns the LAST recovery matching <paramref name="property"/>
    /// in source order. Earlier "first match wins" semantics broke
    /// CSS last-declaration-wins for repeated declarations like
    /// <c>white-space: normal; white-space: break-spaces</c> — author
    /// intent is the LAST one wins, not the first. Cascade-resolver
    /// last-decl-wins still applies at rule-merge time; this fix
    /// ensures the LAST recovery's value is what makes it through
    /// the recovery-override pass (avoiding stale earlier values).</summary>
    private static CssDeclarationRecovery? FindRecovery(ImmutableArray<CssDeclarationRecovery> list, string property)
    {
        CssDeclarationRecovery? last = null;
        foreach (var item in list)
        {
            if (string.Equals(item.Property, property, StringComparison.OrdinalIgnoreCase))
                last = item;
        }
        return last;
    }

    /// <summary>Per Phase 3 Task 15 L17 post-PR-#77 review #2 (P1) —
    /// determine whether a shorthand-expansion <paramref name="recovery"/>
    /// wins against the explicit longhand declarations in the same rule
    /// per CSS Cascade §5 (importance) + §7.4 (source-order
    /// last-decl-wins).
    ///
    /// <para><b>Cascade rules applied:</b>
    /// <list type="number">
    ///   <item>An <c>!important</c> declaration beats a normal
    ///   declaration regardless of source order.</item>
    ///   <item>Within the same importance level, the LATER source
    ///   position wins.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Examples:</b>
    /// <list type="bullet">
    ///   <item>Recovery <c>flex-wrap: wrap</c> at ordinal 0 (normal)
    ///   vs. explicit <c>flex-wrap: nowrap</c> at ordinal 1 (normal):
    ///   explicit at ordinal 1 &gt; 0 + same importance →
    ///   <b>explicit wins</b> (= return false).</item>
    ///   <item>Recovery <c>flex-wrap: wrap</c> at ordinal 0
    ///   (!important) vs. explicit <c>flex-wrap: nowrap</c> at ordinal
    ///   1 (normal): recovery is important, explicit is normal →
    ///   <b>recovery wins</b> (= return true).</item>
    ///   <item>Recovery <c>flex-wrap: wrap-reverse</c> at ordinal 2
    ///   (normal) — i.e., a LATER shorthand — vs. explicit
    ///   <c>flex-wrap: nowrap</c> at ordinal 1 (normal): recovery's
    ///   ordinal 2 &gt; 1 + same importance → <b>recovery wins</b>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>The check iterates over every explicit longhand
    /// declaration for the same property; finding one that beats the
    /// recovery is sufficient to return <see langword="false"/>. When
    /// no explicit longhand beats the recovery, returns
    /// <see langword="true"/>.</para></summary>
    private static bool ShouldShorthandWinAgainstExplicitLonghands(
        CssDeclarationRecovery recovery,
        ImmutableArray<ExplicitLonghandRef> explicitLonghands)
    {
        if (explicitLonghands.IsDefaultOrEmpty) return true;
        foreach (var exp in explicitLonghands)
        {
            if (!string.Equals(
                    exp.Property, recovery.Property,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (BeatsRecoveryPerCascade(exp, recovery))
            {
                return false; // explicit longhand wins
            }
        }
        return true; // recovery wins (no explicit longhand beat it)
    }

    /// <summary>Per CSS Cascade §5 + §7.4 — does the explicit longhand
    /// declaration <paramref name="exp"/> beat the shorthand-expansion
    /// recovery <paramref name="recovery"/>?</summary>
    private static bool BeatsRecoveryPerCascade(
        ExplicitLonghandRef exp,
        CssDeclarationRecovery recovery)
    {
        if (exp.IsImportant && !recovery.IsImportant)
            return true; // !important beats normal regardless of order
        if (!exp.IsImportant && recovery.IsImportant)
            return false; // normal cannot beat !important
        // Same importance level — later source-order wins.
        return exp.Ordinal > recovery.SourceOrdinal;
    }

    private static CssAtRule AdaptMediaRule(ICssMediaRule rule, CssSourceLocation location) => new(
        Name: "media",
        Prelude: rule.Media?.MediaText ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRulesWithoutPreprocess(rule.Rules),
        Location: location);

    private static CssAtRule AdaptSupportsRule(ICssSupportsRule rule, CssSourceLocation location) => new(
        Name: "supports",
        Prelude: ExtractPrelude(rule.CssText, "@supports"),
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRulesWithoutPreprocess(rule.Rules),
        Location: location);

    private static CssAtRule AdaptKeyframesRule(ICssKeyframesRule rule, CssSourceLocation location) => new(
        Name: "keyframes",
        Prelude: rule.Name ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRulesWithoutPreprocess(rule.Rules),
        Location: location);

    private static CssStyleRule AdaptKeyframeRule(ICssKeyframeRule rule, CssSourceLocation location) => new(
        new CssSelector(rule.KeyText ?? string.Empty),
        AdaptDeclarations(rule.Style),
        location);

    private static CssAtRule AdaptFontFaceRule(ICssFontFaceRule rule, CssSourceLocation location)
    {
        var declarations = rule is IEnumerable<ICssProperty> descriptors
            ? AdaptProperties(descriptors)
            : ImmutableArray<CssDeclaration>.Empty;
        return new CssAtRule(
            Name: "font-face",
            Prelude: string.Empty,
            Declarations: declarations,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: location);
    }

    private static CssAtRule AdaptPageRule(ICssPageRule rule, CssPageRuleRecovery? recovery, CssSourceLocation location)
    {
        // The pre-pass recovers what AngleSharp drops: the page selector and any margin-box
        // at-rules. When recovery is null, fall back to AngleSharp's (lossy) data.
        var prelude = !string.IsNullOrEmpty(recovery?.SelectorText)
            ? recovery!.SelectorText
            : (rule.SelectorText ?? string.Empty);

        var marginBoxes = recovery is null || recovery.MarginBoxes.IsEmpty
            ? ImmutableArray<CssRule>.Empty
            : BuildMarginBoxRules(recovery.MarginBoxes);

        var declarations = AdaptDeclarations(rule.Style);
        if (!string.IsNullOrEmpty(recovery?.SizeText))
        {
            // AngleSharp.Css drops the `size` descriptor (it's a page descriptor, not a regular
            // property), so re-attach the pre-pass-recovered value as a synthetic declaration —
            // downstream @page resolution then reads it like any other declaration. The pre-pass
            // already stripped `!important` from the value text and recorded it, so stamp the
            // importance here for AtPageSizeResolver's cross-rule cascade.
            declarations = declarations.Insert(0, new CssDeclaration(
                Property: "size",
                Value: new CssValue(recovery!.SizeText!),
                IsImportant: recovery.SizeIsImportant,
                Location: CssSourceLocation.Unknown));
        }

        var ruleLocation = recovery?.Location ?? location;
        return new CssAtRule(
            Name: "page",
            Prelude: prelude,
            Declarations: declarations,
            ChildRules: marginBoxes,
            Location: ruleLocation);
    }

    private static CssAtRule AdaptMarginRule(ICssMarginRule rule, CssSourceLocation location) => new(
        // Forward-compat case — AngleSharp.Css doesn't emit ICssMarginRule today. The
        // pre-pass synthesizes margin-boxes via BuildMarginBoxRules below, not here.
        Name: rule.Name ?? string.Empty,
        Prelude: string.Empty,
        Declarations: AdaptDeclarations(rule.Style),
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: location);

    private static CssImportRule AdaptImportRule(
        ICssImportRule rule,
        CssImportRuleRecovery? recovery,
        CssSourceLocation location)
    {
        // Recovery wins for layer + supports + media (AngleSharp would have mangled these
        // when modern syntax is present). Fall back to AngleSharp data when no recovery —
        // either the input was simple or pre-pass ran on stale text.
        var url = recovery is not null ? recovery.Url : (rule.Href ?? string.Empty);
        var media = recovery is not null
            ? recovery.MediaQuery
            : (rule.Media?.MediaText ?? string.Empty);
        var ruleLocation = recovery?.Location ?? location;

        return new CssImportRule(
            Url: url,
            MediaQuery: media,
            LayerName: recovery?.LayerName,
            SupportsCondition: recovery?.SupportsCondition,
            ImportedRules: ImmutableArray<CssRule>.Empty,
            Location: ruleLocation);
    }

    private static CssAtRule AdaptCharsetRule(ICssCharsetRule rule, CssSourceLocation location) => new(
        Name: "charset",
        Prelude: $"\"{rule.CharacterSet}\"",
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: location);

    private static CssAtRule AdaptNamespaceRule(ICssNamespaceRule rule, CssSourceLocation location)
    {
        var prelude = string.IsNullOrEmpty(rule.Prefix)
            ? $"url(\"{rule.NamespaceUri}\")"
            : $"{rule.Prefix} url(\"{rule.NamespaceUri}\")";
        return new CssAtRule(
            Name: "namespace",
            Prelude: prelude,
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: location);
    }

    private static CssAtRule AdaptUnknownGroupingRule(ICssGroupingRule rule, CssSourceLocation location) => new(
        Name: NameFromType(rule),
        Prelude: ExtractPrelude(rule.CssText, "@" + NameFromType(rule)),
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRulesWithoutPreprocess(rule.Rules),
        Location: location);

    private static CssAtRule AdaptUnknownRule(ICssRule rule, CssSourceLocation location) => new(
        Name: NameFromType(rule),
        Prelude: rule.CssText ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: location);

    /// <summary>
    /// Constructs <see cref="CssAtRule"/> entries for the recovered margin-boxes inside an
    /// <c>@page</c> body. Each margin-box becomes an at-rule with its name, no prelude, and
    /// declarations parsed from the recovered raw text via a minimal <c>property: value</c>
    /// splitter (the cascade resolver in Task 7 will be the canonical declaration parser
    /// once typed values land in Task 9–10).
    /// </summary>
    private static ImmutableArray<CssRule> BuildMarginBoxRules(ImmutableArray<CssMarginBoxRecovery> boxes)
    {
        var output = ImmutableArray.CreateBuilder<CssRule>(boxes.Length);
        foreach (var box in boxes)
        {
            output.Add(new CssAtRule(
                Name: box.Name,
                Prelude: string.Empty,
                Declarations: ParseRawDeclarations(box.DeclarationsRawText, box.Location),
                ChildRules: ImmutableArray<CssRule>.Empty,
                Location: box.Location));
        }
        return output.ToImmutable();
    }

    /// <summary>
    /// Splits raw text inside a margin-box's <c>{ }</c> body into <see cref="CssDeclaration"/>
    /// entries. Handles the simple <c>property: value</c> shape with optional
    /// <c>!important</c>. The <c>!important</c> recognizer is token-aware — it only matches
    /// <c>!important</c> outside strings, comments, and parens, so values like
    /// <c>content: "!important"</c> survive intact.
    /// </summary>
    /// <remarks>
    /// All emitted declarations share <paramref name="parentLocation"/> as their location.
    /// Per-property positions inside the margin-box body are a Phase 2 Task 3 follow-up:
    /// the tokenizer tracks line/column relative to the body start, but converting those
    /// into absolute source positions requires the original body's offset within the source
    /// stylesheet, which the recovery records don't carry today.
    /// </remarks>
    private static ImmutableArray<CssDeclaration> ParseRawDeclarations(string body, CssSourceLocation parentLocation)
    {
        if (string.IsNullOrWhiteSpace(body)) return ImmutableArray<CssDeclaration>.Empty;

        var output = ImmutableArray.CreateBuilder<CssDeclaration>();
        var tok = new Preprocessing.CssTokenizer(body.AsSpan(), parentLocation.Source);
        tok.SkipWhitespaceAndComments();

        while (!tok.IsEnd)
        {
            var name = tok.ReadIdentifier();
            if (name.IsEmpty)
            {
                tok.ReadChar();
                continue;
            }

            tok.SkipWhitespaceAndComments();
            if (tok.PeekChar() != ':')
            {
                tok.ReadUntilAnyTopLevel(";");
                if (tok.PeekChar() == ';') tok.ReadChar();
                tok.SkipWhitespaceAndComments();
                continue;
            }
            tok.ReadChar();
            tok.SkipWhitespaceAndComments();

            var valueSpan = tok.ReadUntilAnyTopLevel(";");
            var (valueText, isImportant) = ImportantParser.Strip(valueSpan.ToString());

            // Normalize property name to lowercase to match the rest of the AST:
            // AngleSharp emits property names lowercased, and CssDeclarationRecovery
            // also normalizes via ToLowerInvariant. Preserving authored casing here
            // would cause `@top-center { Color: red }` to emit `Property = "Color"`
            // while a normal style rule's `Color: red` becomes `"color"` — breaking
            // case-insensitive lookup in the cascade.
            var propertyName = name.ToString().ToLowerInvariant();

            // Expand the `font` shorthand into longhands (Task 21 cycle 6). AngleSharp.Css never
            // sees margin-box bodies, so without this a `font: …` declaration would be dropped (no
            // `font` longhand resolver exists). Expansion is ATOMIC — on success the four longhands
            // are emitted and the `font` shorthand is consumed; on failure (a system-font keyword or
            // malformed/invalid value) the raw `font` declaration is KEPT as a marker so
            // MarginBoxStyle — which has a diagnostics sink — can surface it (review #3) rather than
            // silently dropping it. (A `font` that survives here is, by construction, a rejected one.)
            // Expand the `border` / `border-<side>` + `border-width`/`-style`/`-color` + `padding`
            // shorthands into longhands (Task 21) — same rationale as `font`: AngleSharp doesn't see
            // margin-box bodies. Atomic; on failure the raw declaration is KEPT as a marker so
            // MarginBoxStyle surfaces it as a diagnostic.
            if (BorderShorthandExpander.IsBorderShorthand(propertyName)
                && BorderShorthandExpander.TryExpand(propertyName, valueText, out var borderLonghands))
            {
                foreach (var (prop, val) in borderLonghands)
                    output.Add(new CssDeclaration(prop, new CssValue(val), isImportant, parentLocation));
            }
            else if (BorderBoxShorthandExpander.IsBorderBoxShorthand(propertyName)
                && BorderBoxShorthandExpander.TryExpand(propertyName, valueText, out var borderBoxLonghands))
            {
                foreach (var (prop, val) in borderBoxLonghands)
                    output.Add(new CssDeclaration(prop, new CssValue(val), isImportant, parentLocation));
            }
            else if (PaddingShorthandExpander.IsPaddingShorthand(propertyName)
                && PaddingShorthandExpander.TryExpand(propertyName, valueText, out var paddingLonghands))
            {
                foreach (var (prop, val) in paddingLonghands)
                    output.Add(new CssDeclaration(prop, new CssValue(val), isImportant, parentLocation));
            }
            else if (propertyName == "font"
                && FontShorthandExpander.TryExpand(
                    valueText, out var fStyle, out var fWeight, out var fSize, out var fFamily))
            {
                output.Add(new CssDeclaration("font-style", new CssValue(fStyle), isImportant, parentLocation));
                output.Add(new CssDeclaration("font-weight", new CssValue(fWeight), isImportant, parentLocation));
                output.Add(new CssDeclaration("font-size", new CssValue(fSize), isImportant, parentLocation));
                output.Add(new CssDeclaration("font-family", new CssValue(fFamily), isImportant, parentLocation));
            }
            else
            {
                output.Add(new CssDeclaration(propertyName, new CssValue(valueText), isImportant, parentLocation));
            }

            if (tok.PeekChar() == ';') tok.ReadChar();
            tok.SkipWhitespaceAndComments();
        }

        return output.Count == 0 ? ImmutableArray<CssDeclaration>.Empty : output.ToImmutable();
    }


    private static ImmutableArray<CssDeclaration> AdaptDeclarations(ICssStyleDeclaration style) =>
        AdaptProperties(style);

    private static ImmutableArray<CssDeclaration> AdaptProperties(IEnumerable<ICssProperty> properties)
    {
        var output = ImmutableArray.CreateBuilder<CssDeclaration>();
        foreach (var property in properties)
        {
            if (property is null || string.IsNullOrEmpty(property.Name)) continue;
            output.Add(new CssDeclaration(
                Property: property.Name,
                Value: new CssValue(property.Value ?? string.Empty),
                IsImportant: property.IsImportant,
                Location: CssSourceLocation.Unknown));
        }
        return output.Count == 0
            ? ImmutableArray<CssDeclaration>.Empty
            : output.ToImmutable();
    }

    private static string NameFromType(ICssRule rule)
    {
        var name = rule.Type.ToString();
        if (name.StartsWith("Css", StringComparison.Ordinal)) name = name[3..];
        if (name.EndsWith("Rule", StringComparison.Ordinal)) name = name[..^4];
        return CamelToKebab(name);
    }

    private static string CamelToKebab(string camel)
    {
        if (string.IsNullOrEmpty(camel)) return string.Empty;
        var output = new System.Text.StringBuilder(camel.Length + 4);
        for (var i = 0; i < camel.Length; i++)
        {
            var c = camel[i];
            if (i > 0 && char.IsUpper(c)) output.Append('-');
            output.Append(char.ToLowerInvariant(c));
        }
        return output.ToString();
    }

    private static string ExtractPrelude(string? cssText, string atKeyword)
    {
        if (string.IsNullOrEmpty(cssText)) return string.Empty;
        var i = cssText.IndexOf(atKeyword, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var start = i + atKeyword.Length;
        var brace = cssText.IndexOf('{', start);
        var end = brace < 0 ? cssText.Length : brace;
        return cssText[start..end].Trim();
    }
}
