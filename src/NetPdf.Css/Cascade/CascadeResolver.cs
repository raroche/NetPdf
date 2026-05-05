// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AngleSharp.Dom;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using NetPdf.Css.Selectors;

namespace NetPdf.Css.Cascade;

/// <summary>
/// The cascade resolver — walks the DOM, evaluates every stylesheet's compiled selectors
/// against each element, and produces a <see cref="CascadeResult"/> mapping each element
/// (and each pseudo-element it touches) to the matched declarations. Tasks 8–10 then
/// resolve <c>var()</c> / <c>calc()</c> / typed values per property; Task 12+ consume the
/// final ComputedStyle. Implements CSS Cascade L4 §6.4 ordering: origin → context →
/// inline → layer → specificity → source order, encoded as a single comparable
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
///   <item><description>Add inline styles (<c>style="…"</c> attributes) as a virtual
///   stylesheet appended after every author stylesheet, with
///   <see cref="CascadeKey.IsInlineStyle"/> set so they win over selector-driven rules
///   regardless of selector specificity per CSS Cascade L4 §6.4.3.</description></item>
///   <item><description>Conditional grouping at-rules — <c>@media</c>, <c>@supports</c>,
///   <c>@container</c> — gate their children by prelude evaluation:
///   <c>@media</c> against the <see cref="CssMediaContext"/>; <c>@supports</c> against the
///   property registry (basic evaluator — recognized properties pass, unknown skip with
///   <c>CSS-AT-RULE-UNKNOWN-001</c>); <c>@container</c> always emits
///   <c>CSS-CONTAINER-QUERY-UNSUPPORTED-001</c> and skips children (real container queries
///   are roadmap v1.4).</description></item>
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
        var hasContainingStylesheets = new HashSet<int>();
        var layers = new LayerRegistry();
        var maxSheetOrder = 0;
        for (var i = 0; i < stylesheets.Length; i++)
        {
            var sheet = stylesheets[i];
            if (sheet.IsDisabled) continue;
            if (!media.Matches(sheet.MediaQuery)) continue;
            if (sheet.Order > maxSheetOrder) maxSheetOrder = sheet.Order;
            CollectRules(sheet, compiledRules, hasContainingStylesheets, media, layers, diagnostics);
        }

        if (diagnostics is not null)
        {
            foreach (var sheetIdx in hasContainingStylesheets)
            {
                // sheetIdx is sheet.Order, not array index — find the sheet to pin location.
                var sheet = FindSheetByOrder(stylesheets, sheetIdx);
                diagnostics.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssHasRenderingNotImplemented001,
                    "A `:has()` selector was encountered. NetPdf does not evaluate `:has()` in v1 — the rule has no effect.",
                    CssDiagnosticSeverity.Warning,
                    sheet?.Location ?? CssSourceLocation.Unknown));
            }
        }

        var result = new CascadeResult();
        if (document.DocumentElement is null) return result;

        WalkElement(document.DocumentElement, compiledRules, result);

        // Inline styles enter as a virtual stylesheet with stylesheet-order pinned ABOVE
        // the maximum real stylesheet order so the source-order tie-break (when two
        // declarations share origin/importance/inline-tier/layer/specificity) places them
        // last. The IsInlineStyle flag is what makes them beat selectors of any
        // specificity per L4 §6.4.3 — independent of the source-order pinning.
        var inlineStylesheetOrder = maxSheetOrder + 1;
        WalkInlineStyles(document.DocumentElement, result, inlineStylesheetOrder);

        return result;
    }

    private static CssStylesheet? FindSheetByOrder(ImmutableArray<CssStylesheet> sheets, int order)
    {
        foreach (var s in sheets)
        {
            if (s.Order == order) return s;
        }
        return null;
    }

    /// <summary>Compile every rule in <paramref name="sheet"/> (recursing through
    /// <c>@media</c>/<c>@supports</c>/<c>@layer</c>/<c>@container</c> grouping rules and
    /// <c>@import</c>'s imported rules) and accumulate them into the output list.
    /// Compilation errors emit <c>CSS-PARSE-WARNING-001</c> and the rule contributes nothing.
    /// </summary>
    private static void CollectRules(
        CssStylesheet sheet,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        CssMediaContext media,
        LayerRegistry layers,
        ICssDiagnosticsSink? diagnostics)
    {
        var ruleOrder = 0;
        CollectFromRules(sheet.Rules, sheet.Origin, sheet.Order, ref ruleOrder,
            output, hasContainingSheets, media, layers,
            currentLayer: LayerRegistry.UnlayeredIndex, diagnostics);
    }

    private static void CollectFromRules(
        ImmutableArray<CssRule> rules,
        CssStylesheetOrigin origin,
        int sheetOrder,
        ref int ruleOrder,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        CssMediaContext media,
        LayerRegistry layers,
        int currentLayer,
        ICssDiagnosticsSink? diagnostics)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case CssStyleRule sr:
                    CollectStyleRule(sr, origin, sheetOrder, ref ruleOrder, currentLayer,
                        output, hasContainingSheets, diagnostics);
                    break;
                case CssAtRule ar:
                    CollectAtRule(ar, origin, sheetOrder, ref ruleOrder,
                        output, hasContainingSheets, media, layers, currentLayer, diagnostics);
                    break;
                case CssImportRule import:
                    CollectImportRule(import, origin, sheetOrder, ref ruleOrder,
                        output, hasContainingSheets, media, layers, currentLayer, diagnostics);
                    break;
                // Other rule kinds (page, font-face, …) don't contribute declarations to
                // the regular element cascade; they have separate consumers.
            }
        }
    }

    private static void CollectStyleRule(
        CssStyleRule sr,
        CssStylesheetOrigin origin,
        int sheetOrder,
        ref int ruleOrder,
        int currentLayer,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        ICssDiagnosticsSink? diagnostics)
    {
        var compiled = CompileSelector(sr.Selector.RawText, diagnostics, sr.Location);
        if (compiled is not null && compiled.ContainsHas)
            hasContainingSheets.Add(sheetOrder);
        output.Add(new CompiledRule(
            Selectors: compiled,
            Declarations: sr.Declarations,
            Origin: origin,
            StylesheetOrder: sheetOrder,
            RuleOrder: ruleOrder++,
            LayerOrder: currentLayer));
    }

    private static void CollectAtRule(
        CssAtRule ar,
        CssStylesheetOrigin origin,
        int sheetOrder,
        ref int ruleOrder,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        CssMediaContext media,
        LayerRegistry layers,
        int currentLayer,
        ICssDiagnosticsSink? diagnostics)
    {
        switch (ar.Name)
        {
            case "media":
                if (!media.Matches(ar.Prelude)) return;
                CollectFromRules(ar.ChildRules, origin, sheetOrder, ref ruleOrder,
                    output, hasContainingSheets, media, layers, currentLayer, diagnostics);
                break;
            case "supports":
                if (!SupportsConditionMatches(ar.Prelude, diagnostics, ar.Location)) return;
                CollectFromRules(ar.ChildRules, origin, sheetOrder, ref ruleOrder,
                    output, hasContainingSheets, media, layers, currentLayer, diagnostics);
                break;
            case "container":
                diagnostics?.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssContainerQueryUnsupported001,
                    "An `@container` rule was encountered. NetPdf does not evaluate container queries in v1 — contained rules are skipped.",
                    CssDiagnosticSeverity.Warning,
                    ar.Location));
                break;
            case "layer":
                CollectLayerAtRule(ar, origin, sheetOrder, ref ruleOrder,
                    output, hasContainingSheets, media, layers, diagnostics);
                break;
            default:
                if (ar.ChildRules.IsEmpty && ar.Declarations.IsEmpty
                    && !string.IsNullOrEmpty(ar.RawBody)
                    && !IsKnownDeclarationBearingAtRule(ar.Name))
                {
                    EmitOpaqueAtRuleDiagnostic(ar, diagnostics);
                }
                break;
        }
    }

    /// <summary>Handle an <c>@layer</c> at-rule per CSS Cascade L4 §6.4.4.
    /// Statement-form (<c>@layer foo, bar;</c> — comma-separated names, no body) registers
    /// the layer names in declaration order so subsequent block-form rules can find them.
    /// Block-form (<c>@layer foo { … }</c> — single name + body) registers the name if not
    /// already known, then collects the body's rules with the layer's index threaded through
    /// to <see cref="CompiledRule.LayerOrder"/>. Anonymous block-form (<c>@layer { … }</c>)
    /// gets a synthetic unique layer name.</summary>
    private static void CollectLayerAtRule(
        CssAtRule ar,
        CssStylesheetOrigin origin,
        int sheetOrder,
        ref int ruleOrder,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        CssMediaContext media,
        LayerRegistry layers,
        ICssDiagnosticsSink? diagnostics)
    {
        var names = LayerRegistry.ParsePrelude(ar.Prelude);
        var hasBody = !ar.ChildRules.IsEmpty || !string.IsNullOrEmpty(ar.RawBody);

        if (!hasBody)
        {
            // Statement-form @layer foo, bar, baz; — register all names in declaration
            // order so future block-form rules find their assigned indices.
            if (names.Length == 0)
            {
                // @layer ; or @layer with empty prelude — odd but tolerated.
                return;
            }
            foreach (var n in names) layers.RegisterIfMissing(n);
            return;
        }

        // Block-form: at most one name per spec (multi-name with body is invalid CSS;
        // tolerated by treating the first as the layer for the body).
        var primary = names.Length > 0 ? names[0] : null;  // null → anonymous
        var layerIdx = layers.RegisterIfMissing(primary);

        if (ar.ChildRules.IsEmpty && !string.IsNullOrEmpty(ar.RawBody))
        {
            // Body wasn't decomposed by AngleSharp.Css. v1 emits a diagnostic; future
            // tasks can plumb a CssParserAdapter+CssPreprocessor reparse here so layered
            // rules with opaque bodies still apply.
            EmitOpaqueAtRuleDiagnostic(ar, diagnostics);
            return;
        }

        CollectFromRules(ar.ChildRules, origin, sheetOrder, ref ruleOrder,
            output, hasContainingSheets, media, layers, layerIdx, diagnostics);
    }

    /// <summary>Walk an <c>@import</c>'s already-resolved <see cref="CssImportRule.ImportedRules"/>
    /// at the import's cascade position. The imported rules' media + supports + layer
    /// metadata is honored: the imported sheet is collected only when its media query
    /// matches the cascade media context, the supports condition resolves true, and the
    /// layer (if any) is registered + threaded through to imported declarations.</summary>
    /// <remarks>
    /// In v1 <see cref="CssImportRule.ImportedRules"/> is always empty because
    /// <c>HtmlPdfOptions.ResourceLoader</c> is disabled at the host level (Task 1) — no
    /// imports are actually fetched. This branch becomes live once Phase 5 wires resource
    /// loading. Architecturally correct now so the wireup doesn't have to revisit the cascade.
    /// </remarks>
    private static void CollectImportRule(
        CssImportRule import,
        CssStylesheetOrigin origin,
        int sheetOrder,
        ref int ruleOrder,
        List<CompiledRule> output,
        HashSet<int> hasContainingSheets,
        CssMediaContext media,
        LayerRegistry layers,
        int currentLayer,
        ICssDiagnosticsSink? diagnostics)
    {
        if (import.ImportedRules.IsEmpty) return;
        if (!media.Matches(import.MediaQuery)) return;
        if (import.SupportsCondition is { } supports
            && !SupportsConditionMatches(NormalizeSupportsCondition(supports), diagnostics, import.Location))
        {
            return;
        }

        // @import url(x) layer(name) — imported rules join the named layer. Use the
        // import's layer name if set; otherwise inherit currentLayer (typically the
        // surrounding @layer block, if any).
        int importLayer = currentLayer;
        if (import.LayerName is not null)
        {
            importLayer = layers.RegisterIfMissing(import.LayerName);
        }

        CollectFromRules(import.ImportedRules, origin, sheetOrder, ref ruleOrder,
            output, hasContainingSheets, media, layers, importLayer, diagnostics);
    }

    /// <summary>Per Rec 2: <c>@import url(x) supports(display: grid)</c> arrives at the
    /// cascade with the inner condition stripped of its surrounding parens (Task 3
    /// preprocessor stores it as <c>"display: grid"</c>). The supports evaluator's
    /// <see cref="SupportsParser"/> requires a leading <c>(</c>; wrap if missing.</summary>
    private static string NormalizeSupportsCondition(string condition)
    {
        var trimmed = condition?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed)) return trimmed;
        if (trimmed[0] == '(') return trimmed;
        return "(" + trimmed + ")";
    }

    /// <summary>
    /// v1 <c>@supports</c> evaluator — checks the condition's <c>(property: value)</c> form
    /// against the property registry. Registered properties resolve true (broad approximation
    /// — we don't validate the value against the property's grammar). Unknown properties
    /// resolve false. Boolean compound conditions (<c>not X</c>, <c>X and Y</c>, <c>X or Y</c>)
    /// recurse. Anything more elaborate (e.g., <c>selector(...)</c>, <c>font-tech(...)</c>,
    /// <c>font-format(...)</c>) is treated as unsupported (returns false) with a diagnostic.
    /// </summary>
    /// <remarks>
    /// Per CSS Conditional L3 §4.1, <c>@supports</c> conditions are a recursive grammar
    /// involving negation, conjunction, disjunction, and a leaf <c>(property: value)</c>
    /// declaration check. This v1 implementation handles the common shapes; full
    /// declaration-grammar evaluation is post-v1 work.
    /// </remarks>
    private static bool SupportsConditionMatches(string condition, ICssDiagnosticsSink? diagnostics, CssSourceLocation location)
    {
        var trimmed = (condition ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // An empty supports condition is malformed; skip the block.
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssAtRuleUnknown001,
                "@supports rule with empty condition — children skipped.",
                CssDiagnosticSeverity.Info, location));
            return false;
        }

        try
        {
            return EvaluateSupports(trimmed);
        }
        catch
        {
            // Conservative: any parse failure → unsupported, skip children.
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssAtRuleUnknown001,
                $"@supports condition \"{trimmed}\" could not be evaluated — children skipped.",
                CssDiagnosticSeverity.Info, location));
            return false;
        }
    }

    private static bool EvaluateSupports(string s)
    {
        var p = new SupportsParser(s);
        var result = p.ParseExpression();
        p.SkipWhitespace();
        if (!p.IsEnd) throw new FormatException("trailing content after @supports condition");
        return result;
    }

    /// <summary>Tiny recursive-descent evaluator over the @supports condition grammar.
    /// Implements <c>not</c>, <c>and</c>, <c>or</c>, parens, and the leaf
    /// <c>(property: value)</c> form. Other forms (<c>selector()</c>, <c>font-tech()</c>)
    /// throw, which the caller treats as "unsupported".</summary>
    private ref struct SupportsParser
    {
        private readonly string _text;
        private int _pos;

        public SupportsParser(string text) { _text = text; _pos = 0; }
        public readonly bool IsEnd => _pos >= _text.Length;
        public void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }
        private char Peek() => _text[_pos];

        public bool ParseExpression()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (TryReadKeyword("and"))
                {
                    SkipWhitespace();
                    var right = ParseUnary();
                    left = left && right;
                }
                else if (TryReadKeyword("or"))
                {
                    SkipWhitespace();
                    var right = ParseUnary();
                    left = left || right;
                }
                else break;
            }
            return left;
        }

        private bool ParseUnary()
        {
            SkipWhitespace();
            if (TryReadKeyword("not"))
            {
                SkipWhitespace();
                return !ParseUnary();
            }
            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            SkipWhitespace();
            if (IsEnd || Peek() != '(') throw new FormatException("expected '('");
            _pos++; // consume (
            // The inner is either another expression or a `property: value` declaration.
            // Try the declaration form first by scanning for ':' before the matching ')'.
            var inner = ReadBalancedToCloseParen();
            // If inner has the shape "<ident>:<value>", treat as a declaration check.
            // Otherwise, recurse as a sub-expression (for grouping / boolean compound).
            var colon = inner.IndexOf(':');
            if (colon > 0 && IsIdentifierLike(inner.AsSpan(0, colon)))
            {
                var prop = inner[..colon].Trim();
                // Value is checked by spec but our v1 simplification: presence of a
                // registered property name is enough.
                return PropertyMetadata.NameToId.ContainsKey(prop);
            }
            // Sub-expression — re-evaluate.
            var sub = new SupportsParser(inner);
            var r = sub.ParseExpression();
            sub.SkipWhitespace();
            if (!sub.IsEnd) throw new FormatException("trailing content in @supports sub-expression");
            return r;
        }

        private string ReadBalancedToCloseParen()
        {
            var start = _pos;
            int depth = 1;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var inner = _text[start.._pos];
                        _pos++; // consume )
                        return inner;
                    }
                }
                _pos++;
            }
            throw new FormatException("unterminated @supports parenthesis");
        }

        private bool TryReadKeyword(string keyword)
        {
            if (_pos + keyword.Length > _text.Length) return false;
            for (var i = 0; i < keyword.Length; i++)
            {
                if (char.ToLowerInvariant(_text[_pos + i]) != keyword[i]) return false;
            }
            // Followed by whitespace, paren, or end.
            var next = _pos + keyword.Length;
            if (next < _text.Length)
            {
                var c = _text[next];
                if (!char.IsWhiteSpace(c) && c != '(' && c != ')') return false;
            }
            _pos += keyword.Length;
            return true;
        }

        private static bool IsIdentifierLike(ReadOnlySpan<char> s)
        {
            if (s.IsEmpty) return false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c) || c == '(' || c == ')') return false;
            }
            return true;
        }
    }

    private static void EmitOpaqueAtRuleDiagnostic(CssAtRule ar, ICssDiagnosticsSink? diagnostics)
    {
        diagnostics?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssAtRuleUnknown001,
            $"@{ar.Name} rule body could not be decomposed; nested rules were not applied to the cascade.",
            CssDiagnosticSeverity.Info,
            ar.Location));
    }

    /// <summary>At-rule names whose body holds declarations (not nested style rules) — these
    /// are intentionally skipped at the cascade level, not flagged as opaque.</summary>
    private static bool IsKnownDeclarationBearingAtRule(string name) =>
        name is "page" or "font-face" or "counter-style" or "property" or "color-profile";

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
        var bloom = BuildElementBloom(element);
        foreach (var rule in rules)
        {
            if (rule.Selectors is null) continue;
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
                layerOrder: rule.LayerOrder,
                specificity: alt.Specificity,
                stylesheetOrder: rule.StylesheetOrder,
                ruleOrder: rule.RuleOrder,
                declarationOrder: i,
                isInlineStyle: false);
            target.Add(new MatchedDeclaration(decl, key));
        }
    }

    /// <summary>Per CSS Cascade L4 §6.4.4 — kept here for documentation; inline styles
    /// no longer rely on a specificity sentinel because <see cref="CascadeKey.IsInlineStyle"/>
    /// places them in their own cascade tier above all selector-driven rules in the same
    /// origin/importance bucket per L4 §6.4.3.</summary>
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
                            declarationOrder: i,
                            isInlineStyle: true);
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
