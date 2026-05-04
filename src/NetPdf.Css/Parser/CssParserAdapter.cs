// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// <b>Fidelity notes.</b> AngleSharp.Css normalizes during parsing. The visible effects:
/// </para>
/// <list type="bullet">
///   <item><description>Named colors expand to <c>rgba(r, g, b, a)</c> form.</description></item>
///   <item><description>Shorthand declarations expand to longhands —
///   <c>background: url('x') no-repeat</c> becomes 12 separate <c>background-*</c> declarations,
///   some with <c>initial</c> values that did not appear in the source.</description></item>
///   <item><description>Whitespace around combinators is canonicalized
///   (<c>.a &gt; .b</c> → <c>.a&gt;.b</c>).</description></item>
/// </list>
/// <para>
/// Cascade and computed-value stages already operate on longhands and normalized colors, so
/// these normalizations are beneficial downstream. Diagnostics that need the original source
/// text would have to read it from <c>&lt;style&gt;</c>'s <c>TextContent</c> — out of scope
/// for Task 2.
/// </para>
/// <para>
/// <b>Modern syntax.</b> Constructs that AngleSharp.Css does not recognize natively
/// (<c>@layer</c>, <c>@container</c>, <c>oklch()</c>, <c>color-mix()</c>, etc.) are handled
/// by Task 3's pre-pass tokenizer, which rewrites them or substitutes opaque sentinel tokens
/// before AngleSharp.Css sees the input. The adapter does not need a special case for them —
/// either AngleSharp.Css handles a rule and we adapt it, or the pre-pass already extracted
/// the rule into an alternative path before reaching this adapter.
/// </para>
/// </remarks>
internal static class CssParserAdapter
{
    /// <summary>
    /// Adapts a parsed AngleSharp.Css stylesheet into NetPdf's internal AST.
    /// </summary>
    public static CssStylesheet Adapt(ICssStyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return new CssStylesheet(AdaptRules(sheet.Rules));
    }

    private static IReadOnlyList<CssRule> AdaptRules(ICssRuleList rules)
    {
        if (rules.Length == 0) return Array.Empty<CssRule>();
        var output = new List<CssRule>(rules.Length);
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            output.Add(AdaptRule(rule));
        }
        return output;
    }

    /// <summary>
    /// Dispatches by AngleSharp rule type. The order of cases is important: more-specific
    /// interfaces (e.g., <see cref="ICssMediaRule"/>) must come before their bases
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

    private static CssStyleRule AdaptStyleRule(ICssStyleRule rule) =>
        new(new CssSelector(rule.SelectorText ?? string.Empty), AdaptDeclarations(rule.Style));

    private static CssAtRule AdaptMediaRule(ICssMediaRule rule) => new(
        Name: "media",
        Prelude: rule.Media?.MediaText ?? string.Empty,
        Declarations: Array.Empty<CssDeclaration>(),
        ChildRules: AdaptRules(rule.Rules));

    private static CssAtRule AdaptSupportsRule(ICssSupportsRule rule) => new(
        Name: "supports",
        // AngleSharp.Css's IConditionFunction renders to its CSS text via ToCss(),
        // surfaced through the rule's CssText with the @supports prefix. We slice it
        // to keep just the condition between "@supports" and the opening "{".
        Prelude: ExtractPrelude(rule.CssText, "@supports"),
        Declarations: Array.Empty<CssDeclaration>(),
        ChildRules: AdaptRules(rule.Rules));

    private static CssAtRule AdaptKeyframesRule(ICssKeyframesRule rule) => new(
        Name: "keyframes",
        Prelude: rule.Name ?? string.Empty,
        Declarations: Array.Empty<CssDeclaration>(),
        ChildRules: AdaptRules(rule.Rules));

    private static CssStyleRule AdaptKeyframeRule(ICssKeyframeRule rule) =>
        // A keyframe entry (e.g. "0%" or "from") behaves structurally like a style rule:
        // a "selector" (the key text) plus a declaration block. Treating it as CssStyleRule
        // means downstream consumers don't need a separate keyframe shape.
        new(new CssSelector(rule.KeyText ?? string.Empty), AdaptDeclarations(rule.Style));

    private static CssAtRule AdaptFontFaceRule(ICssFontFaceRule rule)
    {
        // ICssFontFaceRule implements IEnumerable<ICssProperty> directly (not through
        // ICssStyleDeclaration). Iterate to recover every authored descriptor — Family,
        // Source, Style, Weight, etc. all come through this enumeration plus any vendor
        // descriptors AngleSharp doesn't surface as typed properties.
        var declarations = rule is IEnumerable<ICssProperty> descriptors
            ? AdaptProperties(descriptors)
            : Array.Empty<CssDeclaration>();
        return new CssAtRule(
            Name: "font-face",
            Prelude: string.Empty,
            Declarations: declarations,
            ChildRules: Array.Empty<CssRule>());
    }

    private static CssAtRule AdaptPageRule(ICssPageRule rule) => new(
        Name: "page",
        Prelude: rule.SelectorText ?? string.Empty,
        Declarations: AdaptDeclarations(rule.Style),
        ChildRules: Array.Empty<CssRule>());

    private static CssAtRule AdaptMarginRule(ICssMarginRule rule) => new(
        // Margin-box at-rules inside @page (e.g. @top-center) — AngleSharp.Css surfaces them
        // as separate ICssMarginRule entries. The Name property on the rule is the
        // margin-box identifier ("top-center", "bottom-right-corner", …) without the leading @.
        Name: rule.Name ?? string.Empty,
        Prelude: string.Empty,
        Declarations: AdaptDeclarations(rule.Style),
        ChildRules: Array.Empty<CssRule>());

    private static CssAtRule AdaptImportRule(ICssImportRule rule)
    {
        var media = rule.Media?.MediaText;
        var prelude = string.IsNullOrEmpty(media)
            ? $"url(\"{rule.Href}\")"
            : $"url(\"{rule.Href}\") {media}";
        return new CssAtRule(
            Name: "import",
            Prelude: prelude,
            Declarations: Array.Empty<CssDeclaration>(),
            ChildRules: Array.Empty<CssRule>());
    }

    private static CssAtRule AdaptCharsetRule(ICssCharsetRule rule) => new(
        Name: "charset",
        Prelude: $"\"{rule.CharacterSet}\"",
        Declarations: Array.Empty<CssDeclaration>(),
        ChildRules: Array.Empty<CssRule>());

    private static CssAtRule AdaptNamespaceRule(ICssNamespaceRule rule)
    {
        var prelude = string.IsNullOrEmpty(rule.Prefix)
            ? $"url(\"{rule.NamespaceUri}\")"
            : $"{rule.Prefix} url(\"{rule.NamespaceUri}\")";
        return new CssAtRule(
            Name: "namespace",
            Prelude: prelude,
            Declarations: Array.Empty<CssDeclaration>(),
            ChildRules: Array.Empty<CssRule>());
    }

    private static CssAtRule AdaptUnknownGroupingRule(ICssGroupingRule rule) => new(
        // A grouping at-rule that we don't have an explicit case for — preserve structure
        // so downstream stages can still walk children. The name is derived from the rule
        // type so the diagnostic-emission stage in Task 16 has a stable identifier.
        Name: NameFromType(rule),
        Prelude: ExtractPrelude(rule.CssText, "@" + NameFromType(rule)),
        Declarations: Array.Empty<CssDeclaration>(),
        ChildRules: AdaptRules(rule.Rules));

    private static CssAtRule AdaptUnknownRule(ICssRule rule) => new(
        // A rule type we have no explicit case for — fall back to a structural placeholder.
        // The CssText is preserved as the prelude for round-trip reporting; downstream
        // diagnostic-emission can flag it for compatibility-matrix gaps.
        Name: NameFromType(rule),
        Prelude: rule.CssText ?? string.Empty,
        Declarations: Array.Empty<CssDeclaration>(),
        ChildRules: Array.Empty<CssRule>());

    private static IReadOnlyList<CssDeclaration> AdaptDeclarations(ICssStyleDeclaration style) =>
        // ICssStyleDeclaration : IEnumerable<ICssProperty> — enumerate via the shared path.
        AdaptProperties(style);

    private static IReadOnlyList<CssDeclaration> AdaptProperties(IEnumerable<ICssProperty> properties)
    {
        var output = new List<CssDeclaration>();
        foreach (var property in properties)
        {
            if (property is null || string.IsNullOrEmpty(property.Name)) continue;
            output.Add(new CssDeclaration(
                Property: property.Name,
                Value: new CssValue(property.Value ?? string.Empty),
                IsImportant: property.IsImportant));
        }
        return output.Count == 0 ? Array.Empty<CssDeclaration>() : output;
    }

    private static string NameFromType(ICssRule rule)
    {
        // CssRuleType is an enum like CssMediaRule, CssStyleRule, CssDocumentRule, ...
        // Strip the "Css" prefix and "Rule" suffix, lowercase the rest, hyphenate camel
        // boundaries ("FontFace" → "font-face").
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
