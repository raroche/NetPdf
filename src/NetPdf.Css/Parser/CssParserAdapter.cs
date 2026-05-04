// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

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
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return new CssStylesheet(
            Rules: AdaptRules(sheet.Rules),
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

    private static ImmutableArray<CssRule> AdaptRules(ICssRuleList rules)
    {
        if (rules.Length == 0) return ImmutableArray<CssRule>.Empty;
        var output = ImmutableArray.CreateBuilder<CssRule>(rules.Length);
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            output.Add(AdaptRule(rule));
        }
        return output.ToImmutable();
    }

    /// <summary>
    /// Dispatches by AngleSharp rule type. Order matters: more-specific interfaces
    /// (e.g., <see cref="ICssMediaRule"/>) come before their bases
    /// (<see cref="ICssGroupingRule"/>) so the right adaptation runs.
    /// </summary>
    private static CssRule AdaptRule(ICssRule rule) => rule switch
    {
        ICssStyleRule s => AdaptStyleRule(s),
        ICssMediaRule m => AdaptMediaRule(m),
        ICssSupportsRule s => AdaptSupportsRule(s),
        ICssKeyframesRule k => AdaptKeyframesRule(k),
        ICssKeyframeRule kf => AdaptKeyframeRule(kf),
        ICssFontFaceRule f => AdaptFontFaceRule(f),
        ICssPageRule p => AdaptPageRule(p),
        ICssMarginRule mr => AdaptMarginRule(mr),
        ICssImportRule i => AdaptImportRule(i),
        ICssCharsetRule c => AdaptCharsetRule(c),
        ICssNamespaceRule n => AdaptNamespaceRule(n),
        ICssGroupingRule g => AdaptUnknownGroupingRule(g),
        _ => AdaptUnknownRule(rule),
    };

    private static CssStyleRule AdaptStyleRule(ICssStyleRule rule) => new(
        new CssSelector(rule.SelectorText ?? string.Empty),
        AdaptDeclarations(rule.Style),
        CssSourceLocation.Unknown);

    private static CssAtRule AdaptMediaRule(ICssMediaRule rule) => new(
        Name: "media",
        Prelude: rule.Media?.MediaText ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules),
        Location: CssSourceLocation.Unknown);

    private static CssAtRule AdaptSupportsRule(ICssSupportsRule rule) => new(
        Name: "supports",
        Prelude: ExtractPrelude(rule.CssText, "@supports"),
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules),
        Location: CssSourceLocation.Unknown);

    private static CssAtRule AdaptKeyframesRule(ICssKeyframesRule rule) => new(
        Name: "keyframes",
        Prelude: rule.Name ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules),
        Location: CssSourceLocation.Unknown);

    private static CssStyleRule AdaptKeyframeRule(ICssKeyframeRule rule) => new(
        // A keyframe entry (e.g. "0%" or "from") behaves structurally like a style rule:
        // a "selector" (the key text) plus a declaration block.
        new CssSelector(rule.KeyText ?? string.Empty),
        AdaptDeclarations(rule.Style),
        CssSourceLocation.Unknown);

    private static CssAtRule AdaptFontFaceRule(ICssFontFaceRule rule)
    {
        // ICssFontFaceRule implements IEnumerable<ICssProperty> directly (not through
        // ICssStyleDeclaration). Iterate to recover every authored descriptor.
        var declarations = rule is IEnumerable<ICssProperty> descriptors
            ? AdaptProperties(descriptors)
            : ImmutableArray<CssDeclaration>.Empty;
        return new CssAtRule(
            Name: "font-face",
            Prelude: string.Empty,
            Declarations: declarations,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: CssSourceLocation.Unknown);
    }

    private static CssAtRule AdaptPageRule(ICssPageRule rule) => new(
        // Note: AngleSharp.Css 1.0.0-beta.144 silently drops the @page selector
        // (`:first`/`:left`/`:right`/named pages all surface with empty SelectorText) and
        // every margin-box at-rule inside @page. Task 3's pre-pass tokenizer recovers both.
        Name: "page",
        Prelude: rule.SelectorText ?? string.Empty,
        Declarations: AdaptDeclarations(rule.Style),
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: CssSourceLocation.Unknown);

    private static CssAtRule AdaptMarginRule(ICssMarginRule rule) => new(
        // Currently dead in the dispatch — the AngleSharp.Css parser does not emit
        // ICssMarginRule instances (margin-boxes are dropped before the CSSOM). Kept for
        // forward compatibility: when the upstream parser starts emitting them or when
        // Task 3's pre-pass synthesizes them, this case is what catches them.
        Name: rule.Name ?? string.Empty,
        Prelude: string.Empty,
        Declarations: AdaptDeclarations(rule.Style),
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: CssSourceLocation.Unknown);

    private static CssImportRule AdaptImportRule(ICssImportRule rule) => new(
        Url: rule.Href ?? string.Empty,
        // Caveat: AngleSharp.Css folds `layer(name)` and `supports(...)` clauses into the
        // media field as a malformed "not all" query. Until Task 3's pre-pass recovers
        // them, MediaQuery may carry "not all" for an import that originally had a layer
        // or supports clause. LayerName + SupportsCondition stay null until Task 3.
        MediaQuery: rule.Media?.MediaText ?? string.Empty,
        LayerName: null,
        SupportsCondition: null,
        ImportedRules: ImmutableArray<CssRule>.Empty,
        Location: CssSourceLocation.Unknown);

    private static CssAtRule AdaptCharsetRule(ICssCharsetRule rule) => new(
        Name: "charset",
        Prelude: $"\"{rule.CharacterSet}\"",
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: CssSourceLocation.Unknown);

    private static CssAtRule AdaptNamespaceRule(ICssNamespaceRule rule)
    {
        var prelude = string.IsNullOrEmpty(rule.Prefix)
            ? $"url(\"{rule.NamespaceUri}\")"
            : $"{rule.Prefix} url(\"{rule.NamespaceUri}\")";
        return new CssAtRule(
            Name: "namespace",
            Prelude: prelude,
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: CssSourceLocation.Unknown);
    }

    private static CssAtRule AdaptUnknownGroupingRule(ICssGroupingRule rule) => new(
        Name: NameFromType(rule),
        Prelude: ExtractPrelude(rule.CssText, "@" + NameFromType(rule)),
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules),
        Location: CssSourceLocation.Unknown);

    private static CssAtRule AdaptUnknownRule(ICssRule rule) => new(
        Name: NameFromType(rule),
        Prelude: rule.CssText ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: ImmutableArray<CssRule>.Empty,
        Location: CssSourceLocation.Unknown);

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
