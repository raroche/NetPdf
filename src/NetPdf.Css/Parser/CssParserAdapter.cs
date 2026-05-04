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

        var sheetLocation = preprocess.RulePositions.IsEmpty
            ? CssSourceLocation.Unknown
            : preprocess.RulePositions[0].Location;

        return new CssStylesheet(
            Rules: AdaptRules(sheet.Rules, preprocess),
            Href: href,
            Origin: origin,
            OwnerKind: ownerKind,
            MediaQuery: mediaQuery,
            IsDisabled: isDisabled,
            Order: order,
            Location: sheetLocation);
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

    private static ImmutableArray<CssRule> AdaptRules(ICssRuleList rules, CssPreprocessResult preprocess)
    {
        if (rules.Length == 0) return ImmutableArray<CssRule>.Empty;
        var output = ImmutableArray.CreateBuilder<CssRule>(rules.Length);
        var pageOrdinal = 0;
        var importOrdinal = 0;
        var ruleOrdinal = 0;
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            var location = LookupRulePosition(preprocess, ruleOrdinal++);
            output.Add(AdaptRule(rule, preprocess, ref pageOrdinal, ref importOrdinal, location));
        }
        return output.ToImmutable();
    }

    /// <summary>
    /// Dispatches by AngleSharp rule type. Order matters: more-specific interfaces
    /// (e.g., <see cref="ICssMediaRule"/>) come before their bases
    /// (<see cref="ICssGroupingRule"/>) so the right adaptation runs.
    /// </summary>
    private static CssRule AdaptRule(
        ICssRule rule,
        CssPreprocessResult preprocess,
        ref int pageOrdinal,
        ref int importOrdinal,
        CssSourceLocation location)
        => rule switch
        {
            ICssStyleRule s => AdaptStyleRule(s, location),
            ICssMediaRule m => AdaptMediaRule(m, preprocess, location),
            ICssSupportsRule s => AdaptSupportsRule(s, preprocess, location),
            ICssKeyframesRule k => AdaptKeyframesRule(k, preprocess, location),
            ICssKeyframeRule kf => AdaptKeyframeRule(kf, location),
            ICssFontFaceRule f => AdaptFontFaceRule(f, location),
            ICssPageRule p => AdaptPageRule(p, LookupPageRecovery(preprocess, pageOrdinal++), location),
            ICssMarginRule mr => AdaptMarginRule(mr, location),
            ICssImportRule i => AdaptImportRule(i, LookupImportRecovery(preprocess, importOrdinal++), location),
            ICssCharsetRule c => AdaptCharsetRule(c, location),
            ICssNamespaceRule n => AdaptNamespaceRule(n, location),
            ICssGroupingRule g => AdaptUnknownGroupingRule(g, preprocess, location),
            _ => AdaptUnknownRule(rule, location),
        };

    private static CssSourceLocation LookupRulePosition(CssPreprocessResult preprocess, int ordinal)
    {
        if (ordinal < 0 || ordinal >= preprocess.RulePositions.Length)
            return CssSourceLocation.Unknown;
        return preprocess.RulePositions[ordinal].Location;
    }

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

    private static CssStyleRule AdaptStyleRule(ICssStyleRule rule, CssSourceLocation location) => new(
        new CssSelector(rule.SelectorText ?? string.Empty),
        AdaptDeclarations(rule.Style),
        location);

    private static CssAtRule AdaptMediaRule(ICssMediaRule rule, CssPreprocessResult preprocess, CssSourceLocation location) => new(
        Name: "media",
        Prelude: rule.Media?.MediaText ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules, preprocess),
        Location: location);

    private static CssAtRule AdaptSupportsRule(ICssSupportsRule rule, CssPreprocessResult preprocess, CssSourceLocation location) => new(
        Name: "supports",
        Prelude: ExtractPrelude(rule.CssText, "@supports"),
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules, preprocess),
        Location: location);

    private static CssAtRule AdaptKeyframesRule(ICssKeyframesRule rule, CssPreprocessResult preprocess, CssSourceLocation location) => new(
        Name: "keyframes",
        Prelude: rule.Name ?? string.Empty,
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules, preprocess),
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

        var ruleLocation = recovery?.Location ?? location;
        return new CssAtRule(
            Name: "page",
            Prelude: prelude,
            Declarations: AdaptDeclarations(rule.Style),
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

    private static CssAtRule AdaptUnknownGroupingRule(ICssGroupingRule rule, CssPreprocessResult preprocess, CssSourceLocation location) => new(
        Name: NameFromType(rule),
        Prelude: ExtractPrelude(rule.CssText, "@" + NameFromType(rule)),
        Declarations: ImmutableArray<CssDeclaration>.Empty,
        ChildRules: AdaptRules(rule.Rules, preprocess),
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
    /// <c>!important</c>; respects strings and parens for value tokenization.
    /// </summary>
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
                // Skip stray characters defensively.
                tok.ReadChar();
                continue;
            }

            tok.SkipWhitespaceAndComments();
            if (tok.PeekChar() != ':')
            {
                // Malformed; advance to next ';' and continue.
                tok.ReadUntilAnyTopLevel(";");
                if (tok.PeekChar() == ';') tok.ReadChar();
                tok.SkipWhitespaceAndComments();
                continue;
            }
            tok.ReadChar(); // consume ':'
            tok.SkipWhitespaceAndComments();

            var valueSpan = tok.ReadUntilAnyTopLevel(";");
            var valueText = valueSpan.ToString().Trim();
            var isImportant = false;
            const string importantMarker = "!important";
            if (valueText.EndsWith(importantMarker, StringComparison.OrdinalIgnoreCase))
            {
                isImportant = true;
                valueText = valueText[..^importantMarker.Length].TrimEnd();
            }

            output.Add(new CssDeclaration(
                Property: name.ToString(),
                Value: new CssValue(valueText),
                IsImportant: isImportant,
                Location: parentLocation));

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
