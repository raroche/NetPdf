// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Selectors;

namespace NetPdf.Css.Cascade;

/// <summary>
/// The cascade resolver — walks the DOM, evaluates every stylesheet's compiled selectors
/// against each element, and produces a <see cref="CascadeResult"/> mapping each element
/// (and each pseudo-element it touches) to the matched declarations. Tasks 8–10 then
/// resolve <c>var()</c> / <c>calc()</c> / typed values per property; Task 12+ consume the
/// final ComputedStyle. Implements CSS Cascade L4 §6.4 ordering: origin → importance →
/// layer → specificity → source order, encoded as a single comparable
/// <see cref="CascadeKey"/> per matched declaration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline.</b>
/// </para>
/// <list type="number">
///   <item><description>Compile every <see cref="CssStyleRule"/>'s selector text once via
///   <see cref="SelectorCompiler"/>. Compile errors emit <c>CSS-PARSE-WARNING-001</c> and
///   the rule contributes nothing — the rest of the stylesheet still loads.</description></item>
///   <item><description>Walk the DOM in document order. For each element, build an
///   ancestor + self <see cref="SelectorBloomFilter"/> over the (lowercased tag, classes,
///   id) tokens. Use it to skip selectors whose <see cref="SelectorBytecode.RequiredTags"/>
///   /<c>RequiredClasses</c>/<c>RequiredIds</c> aren't all present — saves the matcher call
///   on the typical 90%+ of (element, selector) pairs that can't possibly match.</description></item>
///   <item><description>For each surviving selector, invoke <see cref="SelectorMatcher.Match"/>.
///   On a successful match, build a <see cref="CascadeKey"/> (origin from the stylesheet,
///   importance from the declaration, specificity from the matched alternative, source
///   order from stylesheet/rule/declaration indices) and add a <see cref="MatchedDeclaration"/>
///   to either the host element's <see cref="MatchedRuleSet"/> or the pseudo-element's
///   set (when <see cref="SelectorBytecode.PseudoElement"/> is non-null).</description></item>
///   <item><description>Add inline styles (<c>style="…"</c> attributes) as virtual
///   stylesheet entries appended after every author stylesheet, with specificity per
///   CSS Cascade L4 §6.4.4 ("the inline style attribute's specificity is treated as if
///   it were a single id selector").</description></item>
///   <item><description>Emit <c>CSS-HAS-RENDERING-NOT-IMPLEMENTED-001</c> once per
///   stylesheet whose any rule's compiled selectors set <see cref="SelectorList.ContainsHas"/>.
///   The matcher already returns false for such selectors (Task 6 review cycle 1's
///   <c>ContainsHas</c> guard), so the diagnostic is the only user-visible signal.</description></item>
/// </list>
/// </remarks>
internal static class CascadeResolver
{
    /// <summary>Resolve the cascade for the given <paramref name="document"/> against the
    /// provided <paramref name="stylesheets"/> in source order.</summary>
    public static CascadeResult Resolve(
        IDocument document,
        ImmutableArray<CssStylesheet> stylesheets,
        CssMediaContext media,
        ICssDiagnosticsSink? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(media);

        var compiledRules = new List<CompiledRule>();
        var hasContainingStylesheets = new HashSet<int>(); // stylesheet orders that touched :has()
        for (var sheetIdx = 0; sheetIdx < stylesheets.Length; sheetIdx++)
        {
            var sheet = stylesheets[sheetIdx];
            if (sheet.IsDisabled) continue;
            if (!media.Matches(sheet.MediaQuery)) continue;
            CollectRules(sheet, sheetIdx, compiledRules, hasContainingStylesheets, diagnostics);
        }

        // Emit :has() unsupported-rendering diagnostic once per stylesheet that uses it.
        // The matcher returns false for ContainsHas selectors (Task 6 cycle 1) — the
        // diagnostic is what tells the user why their `article:has(h1)` rule didn't apply.
        if (diagnostics is not null)
        {
            foreach (var sheetIdx in hasContainingStylesheets)
            {
                var sheet = stylesheets[sheetIdx];
                diagnostics.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssHasRenderingNotImplemented001,
                    "A `:has()` selector was encountered. NetPdf does not evaluate `:has()` in v1 — the rule has no effect.",
                    CssDiagnosticSeverity.Warning,
                    sheet.Location));
            }
        }

        var result = new CascadeResult();
        if (document.DocumentElement is null) return result;

        WalkElement(document.DocumentElement, compiledRules, result);

        // Inline styles: appended after every selector-driven match so they win on equal
        // specificity per CSS Cascade L4 §6.4.4. Stylesheet-order index is set above the
        // last real stylesheet so the source-order tie-breaker pulls them above
        // identically-specific declarations.
        var inlineStylesheetOrder = stylesheets.Length;
        WalkInlineStyles(document.DocumentElement, result, inlineStylesheetOrder);

        return result;
    }

    /// <summary>Compile all style rules in <paramref name="sheet"/> (recursing through
    /// <c>@media</c>/<c>@supports</c>/etc. grouping rules) and accumulate them into
    /// <paramref name="output"/>. Compilation errors emit <c>CSS-PARSE-WARNING-001</c> and
    /// the rule contributes nothing.</summary>
    private static void CollectRules(
        CssStylesheet sheet,
        int sheetOrder,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        ICssDiagnosticsSink? diagnostics)
    {
        var ruleOrder = 0;
        CollectFromRules(sheet.Rules, sheet.Origin, sheetOrder, ref ruleOrder,
            output, hasContainingSheets, diagnostics);
    }

    private static void CollectFromRules(
        ImmutableArray<CssRule> rules,
        CssStylesheetOrigin origin,
        int sheetOrder,
        ref int ruleOrder,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        ICssDiagnosticsSink? diagnostics)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case CssStyleRule sr:
                    var compiled = CompileSelector(sr.Selector.RawText, diagnostics, sr.Location);
                    if (compiled is not null && compiled.ContainsHas)
                        hasContainingSheets.Add(sheetOrder);
                    output.Add(new CompiledRule(
                        Selectors: compiled,
                        Declarations: sr.Declarations,
                        Origin: origin,
                        StylesheetOrder: sheetOrder,
                        RuleOrder: ruleOrder++));
                    break;
                case CssAtRule ar when IsGroupingAtRule(ar.Name):
                    CollectFromRules(ar.ChildRules, origin, sheetOrder, ref ruleOrder,
                        output, hasContainingSheets, diagnostics);
                    break;
                // Other at-rules (page, font-face, keyframes, import, …) don't contribute
                // declarations to the regular element cascade; they have separate consumers
                // (page-box layout for @page, font loader for @font-face, etc.). They're
                // intentionally ignored here.
            }
        }
    }

    /// <summary>At-rule names whose body contains nested style rules that participate in
    /// the cascade for elements.</summary>
    private static bool IsGroupingAtRule(string name) =>
        name is "media" or "supports" or "layer" or "container";

    private static SelectorList? CompileSelector(string raw, ICssDiagnosticsSink? diagnostics, CssSourceLocation loc)
    {
        try
        {
            return SelectorCompiler.Compile(raw);
        }
        catch (SelectorParseException ex)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssParseWarning001,
                $"Invalid selector \"{raw}\" — {ex.Reason}. Rule skipped.",
                CssDiagnosticSeverity.Warning,
                loc));
            return null;
        }
    }

    /// <summary>Recursive DOM walk in document order.</summary>
    private static void WalkElement(IElement element, List<CompiledRule> rules, CascadeResult result)
    {
        ApplyRulesToElement(element, rules, result);
        foreach (var child in element.Children)
        {
            WalkElement(child, rules, result);
        }
    }

    private static void ApplyRulesToElement(IElement element, List<CompiledRule> rules, CascadeResult result)
    {
        // Build the per-element ancestor + self bloom. A typical selector chain has at most
        // 4-5 compounds, so for an element at depth N this is N tokens × ~3 (tag + classes +
        // id) = O(small constant). The bloom is reused across every selector pre-filter
        // for this element.
        var bloom = BuildElementBloom(element);

        foreach (var rule in rules)
        {
            if (rule.Selectors is null) continue; // failed-compile rule
            for (var altIdx = 0; altIdx < rule.Selectors.Alternatives.Length; altIdx++)
            {
                var alt = rule.Selectors.Alternatives[altIdx];
                if (!BloomAllows(alt, bloom)) continue;
                if (!SelectorMatcher.Match(alt, element)) continue;
                AddMatched(rule, alt, element, result);
            }
        }
    }

    private static SelectorBloomFilter BuildElementBloom(IElement element)
    {
        var bloom = default(SelectorBloomFilter);
        // The candidate itself + every ancestor contributes its tokens — the candidate is
        // the key compound's anchor; ancestors satisfy descendant/child combinators.
        var cursor = element;
        while (cursor is not null)
        {
            bloom.Add(cursor.LocalName.ToLowerInvariant());
            if (!string.IsNullOrEmpty(cursor.Id)) bloom.Add(cursor.Id);
            if (cursor.ClassList is not null)
            {
                foreach (var c in cursor.ClassList) bloom.Add(c);
            }
            cursor = cursor.ParentElement;
        }
        return bloom;
    }

    private static bool BloomAllows(SelectorBytecode alt, in SelectorBloomFilter bloom)
    {
        foreach (var tag in alt.RequiredTags)
        {
            if (!bloom.MightContain(tag)) return false;
        }
        foreach (var cls in alt.RequiredClasses)
        {
            if (!bloom.MightContain(cls)) return false;
        }
        foreach (var id in alt.RequiredIds)
        {
            if (!bloom.MightContain(id)) return false;
        }
        return true;
    }

    private static void AddMatched(
        CompiledRule rule,
        SelectorBytecode alt,
        IElement element,
        CascadeResult result)
    {
        var target = alt.PseudoElement is null
            ? result.StylesFor(element)
            : result.StylesForPseudo(element, alt.PseudoElement);

        for (var i = 0; i < rule.Declarations.Length; i++)
        {
            var decl = rule.Declarations[i];
            var key = new CascadeKey(
                origin: rule.Origin,
                isImportant: decl.IsImportant,
                layerOrder: 0, // v1: layers wired but unused; @layer parsing is opaque today
                specificity: alt.Specificity,
                stylesheetOrder: rule.StylesheetOrder,
                ruleOrder: rule.RuleOrder,
                declarationOrder: i);
            target.Add(new MatchedDeclaration(decl, key));
        }
    }

    /// <summary>Inline-style specificity per CSS Cascade L4 §6.4.4: treated as if the
    /// element had a single id selector — <c>(1, 0, 0)</c>. Their stylesheet-order is
    /// pinned just above all real stylesheets so the source-order tie-breaker resolves
    /// them above identically-specific selector matches.</summary>
    private static readonly Specificity InlineStyleSpecificity = new(1, 0, 0);

    private static void WalkInlineStyles(IElement element, CascadeResult result, int inlineStylesheetOrder)
    {
        WalkInlineStylesRecursive(element, result, inlineStylesheetOrder, ruleOrder: 0);
    }

    private static int WalkInlineStylesRecursive(IElement element, CascadeResult result, int inlineStylesheetOrder, int ruleOrder)
    {
        var styleAttr = element.GetAttribute("style");
        if (!string.IsNullOrEmpty(styleAttr))
        {
            // Use AngleSharp.Css's ElementCssInlineStyleExtensions.GetStyle() — preserves
            // the same shorthand expansion as <style> blocks. Available when WithCss() is
            // wired into the BrowsingContext (which our HtmlParsingHost does).
            var anglesharpStyle = AngleSharp.Css.Dom.ElementCssInlineStyleExtensions.GetStyle(element);
            if (anglesharpStyle is not null)
            {
                var declarations = CssParserAdapter.AdaptInlineStyle(anglesharpStyle);
                if (!declarations.IsEmpty)
                {
                    var target = result.StylesFor(element);
                    for (var i = 0; i < declarations.Length; i++)
                    {
                        var decl = declarations[i];
                        var key = new CascadeKey(
                            origin: CssStylesheetOrigin.Author,
                            isImportant: decl.IsImportant,
                            layerOrder: 0,
                            specificity: InlineStyleSpecificity,
                            stylesheetOrder: inlineStylesheetOrder,
                            ruleOrder: ruleOrder,
                            declarationOrder: i);
                        target.Add(new MatchedDeclaration(decl, key));
                    }
                    ruleOrder++;
                }
            }
        }
        foreach (var child in element.Children)
        {
            ruleOrder = WalkInlineStylesRecursive(child, result, inlineStylesheetOrder, ruleOrder);
        }
        return ruleOrder;
    }
}
