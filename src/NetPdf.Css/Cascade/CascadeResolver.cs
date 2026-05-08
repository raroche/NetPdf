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
        ICssDiagnosticsSink? diagnostics = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(media);

        var compiledRules = new List<CompiledRule>();
        var hasContainingStylesheets = new HashSet<int>();
        var layers = new LayerRegistry();
        var maxSheetOrder = 0;
        for (var i = 0; i < stylesheets.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = stylesheets[i];
            if (sheet.IsDisabled) continue;
            if (!media.Matches(sheet.MediaQuery)) continue;
            if (sheet.Order > maxSheetOrder) maxSheetOrder = sheet.Order;
            CollectRules(sheet, compiledRules, hasContainingStylesheets, media, layers, diagnostics, cancellationToken);
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

        var rootBloom = default(SelectorBloomFilter);
        WalkElement(document.DocumentElement, compiledRules, in rootBloom, result, cancellationToken);

        // Inline styles enter as a virtual stylesheet with stylesheet-order pinned ABOVE
        // the maximum real stylesheet order so the source-order tie-break (when two
        // declarations share origin/importance/inline-tier/layer/specificity) places them
        // last. The IsInlineStyle flag is what makes them beat selectors of any
        // specificity per L4 §6.4.3 — independent of the source-order pinning.
        var inlineStylesheetOrder = maxSheetOrder + 1;
        WalkInlineStyles(document.DocumentElement, result, inlineStylesheetOrder, cancellationToken);

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
        ICssDiagnosticsSink? diagnostics,
        System.Threading.CancellationToken cancellationToken)
    {
        // Per Phase A security hardening A-8 — wrap the diagnostic sink in a
        // per-stylesheet budget that caps CSS-PARSE-WARNING-001 emissions at
        // 100. A hostile CSS with thousands of broken selectors would
        // otherwise flood the consumer's sink + downstream log infrastructure;
        // the cap keeps the signal intact (the first ~100 broken rules are
        // still surfaced) while silencing the spam tail. Other diagnostic
        // codes pass through unbudgeted — the cap is specifically about
        // selector-parse warnings since they're the easiest amplifier.
        var budgetedSink = diagnostics is null
            ? null
            : new SelectorParseWarningBudgetSink(diagnostics);

        var ruleOrder = 0;
        // Per PR #16 review user-recommendation #4 — capture the starting
        // output count so the rule-cap check is per-stylesheet, not global.
        // Without this, a multi-sheet document where the first sheet
        // approaches MaxRulesPerStylesheet would silently truncate
        // subsequent independent sheets.
        var sheetStartCount = output.Count;
        CollectFromRules(sheet.Rules, sheet.Origin, sheet.Order, ref ruleOrder,
            output, hasContainingSheets, media, layers,
            currentLayer: LayerRegistry.UnlayeredIndex, budgetedSink,
            sheetStartCount, cancellationToken);
    }

    /// <summary>Per Phase A A-8 — sink decorator that throttles
    /// CSS-PARSE-WARNING-001 to <see cref="MaxSelectorParseWarningsPerSheet"/>
    /// per stylesheet. After the budget is exceeded, further selector-parse
    /// warnings are silently dropped + a single summary diagnostic is emitted
    /// noting how many additional warnings would have followed. All other
    /// codes pass through unconditionally.</summary>
    private sealed class SelectorParseWarningBudgetSink : ICssDiagnosticsSink
    {
        public const int MaxSelectorParseWarningsPerSheet = 100;

        private readonly ICssDiagnosticsSink _inner;
        private int _emitted;
        private int _suppressed;
        private bool _summaryEmitted;

        public SelectorParseWarningBudgetSink(ICssDiagnosticsSink inner)
        {
            _inner = inner;
        }

        public void Emit(CssDiagnostic diagnostic)
        {
            if (diagnostic.Code != CssDiagnosticCodes.CssParseWarning001)
            {
                _inner.Emit(diagnostic);
                return;
            }
            if (_emitted < MaxSelectorParseWarningsPerSheet)
            {
                _inner.Emit(diagnostic);
                _emitted++;
                return;
            }
            _suppressed++;
            if (!_summaryEmitted)
            {
                _summaryEmitted = true;
                _inner.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssParseWarning001,
                    $"Selector-parse warnings exceeded the per-stylesheet budget ({MaxSelectorParseWarningsPerSheet}); further selector failures are suppressed for this stylesheet.",
                    CssDiagnosticSeverity.Warning,
                    diagnostic.Location));
            }
        }
    }

    /// <summary>Per Phase A security hardening A-4 — bound the rule-walker
    /// recursion depth so an attacker stylesheet with thousands of nested
    /// <c>@media</c> / <c>@supports</c> / <c>@layer</c> / <c>@import</c>
    /// rules can't escalate into <c>StackOverflowException</c> (uncatchable).
    /// 32 is generous for any hand-authored CSS (typical nesting is 2–3 levels)
    /// while bounding adversarial inputs to well under the .NET default 1 MiB
    /// stack budget.</summary>
    private const int MaxAtRuleNestingDepth = 32;

    /// <summary>Per Phase B B-2 — maximum number of compiled rules per
    /// stylesheet. A hostile sheet with millions of rules would otherwise
    /// pour CPU into selector compilation + per-element matching even with
    /// the bloom-filter pre-filter active. 50k is wide enough for any
    /// real-world author CSS (Tailwind's full build is ~30k) while bounding
    /// the per-stylesheet upper limit.</summary>
    internal const int MaxRulesPerStylesheet = 50_000;

    /// <summary>Per Phase B B-2 — maximum number of declarations on a single
    /// rule. Real rules have &lt; 30; an attacker could synthesize a rule
    /// with thousands of <c>data-x: y</c> declarations to bloat the cascade
    /// table per element. Excess declarations are dropped from the tail.</summary>
    internal const int MaxDeclarationsPerRule = 256;

    /// <summary>Per Phase B B-2 — maximum number of selector alternatives in
    /// one rule's selector list (i.e., comma-separated selector groups).
    /// Real rules have &lt; 5 (occasionally &lt; 100 for normalize.css-style
    /// shorthand); 1024 is the wide cap that catches doc-bombing without
    /// rejecting legitimate megalists.</summary>
    internal const int MaxSelectorAlternatives = 1024;

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
        ICssDiagnosticsSink? diagnostics,
        int sheetStartCount,
        System.Threading.CancellationToken cancellationToken,
        int depth = 0)
    {
        // Per Phase A A-4 — early-return on excessive nesting; emit one
        // diagnostic so the author sees why their guarded rules were skipped.
        if (depth > MaxAtRuleNestingDepth)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssAtRuleUnknown001,
                $"@-rule nesting exceeded the depth limit ({MaxAtRuleNestingDepth}); contained rules skipped.",
                CssDiagnosticSeverity.Warning,
                CssSourceLocation.Unknown));
            return;
        }

        foreach (var rule in rules)
        {
            // Per Phase A A-7 — check cancellation per-rule (not just at the stage
            // boundary). A hostile stylesheet with millions of rules or an
            // adversarially-deep nested @media tree could otherwise spend tens of
            // seconds in selector compilation before any post-loop check runs.
            cancellationToken.ThrowIfCancellationRequested();

            // Per Phase B B-2 — cap the running count of compiled rules per
            // stylesheet so a hostile sheet with millions of rules doesn't
            // pull the cascade into multi-second CPU paths. Emit one
            // diagnostic at the moment of overflow + drop every subsequent
            // rule (style + at-rule child rules included — the cap is
            // sheet-wide, not per-grouping).
            // Per PR #16 review user-recommendation #4 — the count is
            // PER-STYLESHEET (output.Count - sheetStartCount), not the
            // global output.Count, so a sheet that previously hit the cap
            // doesn't poison every subsequent independent sheet.
            if (output.Count - sheetStartCount >= MaxRulesPerStylesheet)
            {
                diagnostics?.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssRuleLimitExceeded001,
                    $"Stylesheet exceeded the {MaxRulesPerStylesheet}-rule cap; remaining rules skipped.",
                    CssDiagnosticSeverity.Warning,
                    CssSourceLocation.Unknown));
                return;
            }

            switch (rule)
            {
                case CssStyleRule sr:
                    CollectStyleRule(sr, origin, sheetOrder, ref ruleOrder, currentLayer,
                        output, hasContainingSheets, diagnostics);
                    break;
                case CssAtRule ar:
                    CollectAtRule(ar, origin, sheetOrder, ref ruleOrder,
                        output, hasContainingSheets, media, layers, currentLayer, diagnostics,
                        sheetStartCount, cancellationToken, depth);
                    break;
                case CssImportRule import:
                    CollectImportRule(import, origin, sheetOrder, ref ruleOrder,
                        output, hasContainingSheets, media, layers, currentLayer, diagnostics,
                        sheetStartCount, cancellationToken, depth);
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

        // Per PR #16 review user-recommendation #3 + Copilot review #6 —
        // enforce MaxSelectorAlternatives. The cap was declared in Phase B
        // but never wired up; a single rule with millions of comma-separated
        // alternatives (each compiled to its own SelectorBytecode) would
        // amplify the per-element matching loop past the per-rule budget.
        // Truncate post-compile by rebuilding the SelectorList with only
        // the first N alternatives — the matching loop iterates the
        // truncated list directly.
        if (compiled is not null && compiled.Alternatives.Length > MaxSelectorAlternatives)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssRuleLimitExceeded001,
                $"Rule exceeded the {MaxSelectorAlternatives}-selector-alternative cap; excess alternatives dropped.",
                CssDiagnosticSeverity.Warning,
                sr.Location));
            compiled = new NetPdf.Css.Selectors.SelectorList(
                ImmutableArray.CreateRange(compiled.Alternatives.Take(MaxSelectorAlternatives)),
                compiled.SourceText);
        }

        // Per Phase B B-2 — cap declarations per rule. A hostile rule with
        // thousands of `data-*: value` declarations would inflate every
        // matched-rule-set per element; tail-truncate so the cascade still
        // sees the head (most rules pile their important props at the top).
        var declarations = sr.Declarations;
        if (declarations.Length > MaxDeclarationsPerRule)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssRuleLimitExceeded001,
                $"Rule exceeded the {MaxDeclarationsPerRule}-declaration cap; tail declarations dropped.",
                CssDiagnosticSeverity.Warning,
                sr.Location));
            declarations = ImmutableArray.CreateRange(declarations.Take(MaxDeclarationsPerRule));
        }

        output.Add(new CompiledRule(
            Selectors: compiled,
            Declarations: declarations,
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
        ICssDiagnosticsSink? diagnostics,
        int sheetStartCount,
        System.Threading.CancellationToken cancellationToken,
        int depth)
    {
        switch (ar.Name)
        {
            case "media":
                if (!media.Matches(ar.Prelude)) return;
                CollectFromRules(ar.ChildRules, origin, sheetOrder, ref ruleOrder,
                    output, hasContainingSheets, media, layers, currentLayer, diagnostics,
                    sheetStartCount, cancellationToken, depth + 1);
                break;
            case "supports":
                if (!SupportsConditionMatches(ar.Prelude, diagnostics, ar.Location)) return;
                CollectFromRules(ar.ChildRules, origin, sheetOrder, ref ruleOrder,
                    output, hasContainingSheets, media, layers, currentLayer, diagnostics,
                    sheetStartCount, cancellationToken, depth + 1);
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
                    output, hasContainingSheets, media, layers, diagnostics,
                    sheetStartCount, cancellationToken, depth);
                break;
            default:
                // Unknown at-rule. Three shapes to handle:
                //   (a) Cascade-silent at-rules with separate consumers (declaration-bearing
                //       like @page / @font-face / @counter-style / @property / @color-profile,
                //       OR child-rule-bearing like @keyframes / @font-feature-values /
                //       @font-palette-values / @scroll-timeline / @view-transition). The
                //       cascade intentionally produces no diagnostic — these are well-known
                //       at-rules that will be consumed by future Phase tasks (animations,
                //       fonts, etc.).
                //   (b) Empty + RawBody set — opaque body the parser couldn't decompose.
                //       Emit CSS-AT-RULE-UNKNOWN-001 so users see preserved-not-applied.
                //   (c) Non-empty ChildRules — an at-rule AngleSharp.Css adapted as a
                //       generic ICssGroupingRule (e.g., a future @scope). v1 doesn't know
                //       what condition (if any) gates these children, so the conservative
                //       choice is to skip them + emit the opaque diagnostic. Without this,
                //       nested rules in unknown groupings would silently apply with no
                //       signal to the user.
                if (IsKnownCascadeSilentAtRule(ar.Name)) break;
                if (!ar.ChildRules.IsEmpty || !string.IsNullOrEmpty(ar.RawBody))
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
        ICssDiagnosticsSink? diagnostics,
        int sheetStartCount,
        System.Threading.CancellationToken cancellationToken,
        int depth)
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
            output, hasContainingSheets, media, layers, layerIdx, diagnostics,
            sheetStartCount, cancellationToken, depth + 1);
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
        ICssDiagnosticsSink? diagnostics,
        int sheetStartCount,
        System.Threading.CancellationToken cancellationToken,
        int depth)
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
            output, hasContainingSheets, media, layers, importLayer, diagnostics,
            sheetStartCount, cancellationToken, depth + 1);
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
            // Per Phase A A-6 — sanitize the author-supplied condition before
            // interpolation; the prelude can carry control chars or be extreme
            // length.
            var safeTrimmed = DiagnosticTextSanitizer.Sanitize(trimmed, maxLength: 120);
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssAtRuleUnknown001,
                $"@supports condition \"{safeTrimmed}\" could not be evaluated — children skipped.",
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

        /// <summary>Parse a chain of <c>and</c>-connected or <c>or</c>-connected sub-expressions.
        /// Per CSS Conditional L3 §4.1.1: mixing <c>and</c> and <c>or</c> at the same
        /// (unparenthesized) level is malformed — <c>(A) and (B) or (C)</c> must be
        /// rejected; the author needs explicit grouping (<c>((A) and (B)) or (C)</c>).
        /// Throws <see cref="FormatException"/> on mixed connectors so the caller drops
        /// the @supports block with a diagnostic.</summary>
        public bool ParseExpression()
        {
            var left = ParseUnary();
            // Track which connector we've seen at this level. Once locked in, the other
            // connector is a parse error.
            char seen = '\0';
            while (true)
            {
                SkipWhitespace();
                bool isAnd = TryReadKeyword("and");
                bool isOr = !isAnd && TryReadKeyword("or");
                if (!isAnd && !isOr) break;

                var connector = isAnd ? 'a' : 'o';
                if (seen == '\0') seen = connector;
                else if (seen != connector)
                    throw new FormatException("@supports mixes 'and' and 'or' at the same level — explicit parens required");

                SkipWhitespace();
                var right = ParseUnary();
                left = isAnd ? (left && right) : (left || right);
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
                if (!PropertyMetadata.NameToId.TryGetValue(prop, out var propertyId)) return false;

                // Per Phase 2 deep review Rec 4 — also validate the VALUE.
                // Cycle 1 returned true on bare property-name presence, so
                // `@supports (color: not-a-color)` would apply the guarded
                // block. Per CSS Conditional L3 §4.1.3, the test must succeed
                // ONLY if both the property is supported AND the value would
                // be accepted. Probe the typed pipeline with a null diagnostics
                // sink and WHITELIST Resolved + Deferred outcomes (per PR #13
                // review feedback): Resolved = typed value produced; Deferred
                // = well-formed but needs downstream context (var()/calc()).
                // UnsupportedUnvalidated (the cycle-2 backlog state where the
                // resolver isn't wired yet) must NOT count as supported —
                // returning true on it would say "yes, NetPdf supports this"
                // when in fact NetPdf hasn't validated the value at all.
                // Invalid is the parse-error case + obviously unsupported.
                var rawValue = inner[(colon + 1)..].Trim();
                if (rawValue.Length == 0) return false;
                var probe = ComputedValues.PropertyResolvers.PropertyResolverDispatch.Resolve(
                    propertyId, rawValue, diagnostics: null, location: CssSourceLocation.Unknown);
                return probe.State == ComputedValues.PropertyResolvers.ResolutionState.Resolved
                    || probe.State == ComputedValues.PropertyResolvers.ResolutionState.Deferred;
            }
            // Sub-expression — re-evaluate.
            var sub = new SupportsParser(inner);
            var r = sub.ParseExpression();
            sub.SkipWhitespace();
            if (!sub.IsEnd) throw new FormatException("trailing content in @supports sub-expression");
            return r;
        }

        /// <summary>Read up to the matching close-paren, tracking nested parens AND skipping
        /// the contents of single- / double-quoted strings so values like
        /// <c>(content: "(") </c> or <c>(font-family: "a)b")</c> aren't truncated at a
        /// paren inside the string.</summary>
        private string ReadBalancedToCloseParen()
        {
            var start = _pos;
            int depth = 1;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == '"' || c == '\'')
                {
                    SkipString(c);
                    continue;
                }
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

        /// <summary>Advance past a string literal starting at the current quote char.
        /// Handles backslash escapes (treats <c>\&lt;any&gt;</c> as a single escaped char so
        /// <c>"a\"b"</c> doesn't terminate early). Stops at the closing quote — caller is
        /// past it on return.</summary>
        private void SkipString(char quote)
        {
            _pos++; // consume opening quote
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == '\\' && _pos + 1 < _text.Length)
                {
                    _pos += 2;
                    continue;
                }
                _pos++;
                if (c == quote) return;
            }
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
        // Per Phase A A-6 — sanitize the at-rule name. Although AngleSharp normalizes
        // these to ASCII identifiers in well-formed input, malformed CSS can carry an
        // arbitrary token here; defense-in-depth before interpolation.
        var safeName = DiagnosticTextSanitizer.Sanitize(ar.Name, maxLength: 60);
        diagnostics?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssAtRuleUnknown001,
            $"@{safeName} rule body could not be decomposed; nested rules were not applied to the cascade.",
            CssDiagnosticSeverity.Info,
            ar.Location));
    }

    /// <summary>At-rules with separate consumers that should NOT trigger
    /// <c>CSS-AT-RULE-UNKNOWN-001</c> when the cascade encounters them. Includes:
    /// declaration-bearing rules (<c>@page</c>, <c>@font-face</c>, <c>@counter-style</c>,
    /// <c>@property</c>, <c>@color-profile</c>) and child-rule-bearing rules
    /// (<c>@keyframes</c>, <c>@font-feature-values</c>, <c>@font-palette-values</c>,
    /// <c>@scroll-timeline</c>, <c>@view-transition</c>) — all of which have their own
    /// downstream consumers in later phases (page-box layout, font loader, animations,
    /// transitions). Without this allowlist, every stylesheet using <c>@keyframes</c>
    /// would surface noisy <c>CSS-AT-RULE-UNKNOWN-001</c> diagnostics for a known at-rule.
    /// Statement-form rules (<c>@charset</c>, <c>@namespace</c>) don't show up here
    /// because they have empty <c>ChildRules</c> + empty <c>RawBody</c> and naturally
    /// fall through the diagnostic-emit branch.</summary>
    private static bool IsKnownCascadeSilentAtRule(string name) =>
        name is "page" or "font-face" or "counter-style" or "property" or "color-profile"
             or "keyframes" or "font-feature-values" or "font-palette-values"
             or "scroll-timeline" or "view-transition"
             or "charset" or "namespace";

    private static SelectorList? CompileSelector(string raw, ICssDiagnosticsSink? diagnostics, CssSourceLocation loc)
    {
        try
        {
            return SelectorCompiler.Compile(raw);
        }
        catch (SelectorParseException ex)
        {
            // Per Phase 2 deep review C-2 — sanitize BOTH the selector text and
            // the exception reason before formatting them into the diagnostic
            // message. Untrusted CSS can carry ANSI escape sequences, NUL /
            // control chars, or extreme length; emitting verbatim could let a
            // hostile stylesheet inject control sequences into a downstream
            // sink (terminal log, JSON encoder, etc.) and would also bloat
            // diagnostic output for an attacker-supplied multi-megabyte
            // selector. The reason string can also embed the raw selector
            // fragment, so it gets the same treatment.
            var safeRaw = DiagnosticTextSanitizer.Sanitize(raw, maxLength: 80);
            var safeReason = DiagnosticTextSanitizer.Sanitize(ex.Reason, maxLength: 200);
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssParseWarning001,
                $"Invalid selector \"{safeRaw}\" — {safeReason}. Rule skipped.",
                CssDiagnosticSeverity.Warning,
                loc));
            return null;
        }
    }

    // The sanitizer was factored out into Diagnostics/DiagnosticTextSanitizer
    // per Phase A A-6 so all emission sites can share one implementation.

    /// <summary>Recursive DOM walk in document order. Carries the ancestor-self bloom as
    /// a value-copied parameter — each frame copies parent's bloom, adds this element's
    /// (tag, classes, id) tokens, and passes the new bloom to children. Avoids the prior
    /// O(depth) per-element rebuild that re-hashed the same ancestor chain repeatedly.
    /// The 512-byte struct copy per frame is cheap relative to the matcher work that
    /// dominates the per-element cost.</summary>
    private static void WalkElement(IElement element, List<CompiledRule> rules,
        in SelectorBloomFilter ancestorBloom, CascadeResult result,
        System.Threading.CancellationToken cancellationToken)
    {
        // Per Phase 2 deep review Rec 6 — check at every element so a hostile
        // 100k-element document stops promptly instead of running the full
        // selector match pass before noticing the stage boundary in
        // Phase2Pipeline.
        cancellationToken.ThrowIfCancellationRequested();
        var bloom = ancestorBloom; // value copy — independent from caller's
        AddElementTokens(element, ref bloom);
        ApplyRulesToElement(element, rules, in bloom, result);
        foreach (var child in element.Children)
        {
            WalkElement(child, rules, in bloom, result, cancellationToken);
        }
    }

    private static void AddElementTokens(IElement element, ref SelectorBloomFilter bloom)
    {
        bloom.Add(element.LocalName.ToLowerInvariant());
        if (!string.IsNullOrEmpty(element.Id)) bloom.Add(element.Id);
        if (element.ClassList is not null)
        {
            foreach (var c in element.ClassList) bloom.Add(c);
        }
    }

    private static void ApplyRulesToElement(IElement element, List<CompiledRule> rules,
        in SelectorBloomFilter bloom, CascadeResult result)
    {
        foreach (var rule in rules)
        {
            if (rule.Selectors is null) continue;
            ApplyRuleToElement(rule, element, in bloom, result);
        }
    }

    /// <summary>Apply one rule to one element, ensuring each (rule, element, pseudo-element)
    /// triple contributes its declarations AT MOST ONCE to the cascade — picking the
    /// max-specificity matching alternative within the rule's selector list, per CSS Cascade
    /// L4 §6.4.5 ("the specificity is the most specific selector for which it matches").</summary>
    /// <remarks>
    /// <para>
    /// The earlier "iterate alternatives + AddMatched per match" loop double-inserted the
    /// same authored declaration when a selector list had multiple matching alternatives —
    /// e.g., <c>p, .x { color: red }</c> on <c>&lt;p class="x"&gt;</c> added <c>color: red</c>
    /// twice with specificities <c>(0,0,1)</c> and <c>(0,1,0)</c>, inflating
    /// <see cref="MatchedRuleSet.Count"/> and breaking <c>revert</c>/<c>revert-layer</c>
    /// semantics that expect one cascade entry per authored declaration.
    /// </para>
    /// <para>
    /// Pseudo-element separation: alternatives that target different pseudo-elements
    /// (<c>p::before, p::after { … }</c>) intentionally produce distinct entries in their
    /// own <see cref="MatchedRuleSet"/>s — the deduplication is per (target,
    /// pseudo-element) bucket.
    /// </para>
    /// </remarks>
    /// <summary>Sentinel string used as the dictionary key for the host-element bucket
    /// (since <see cref="Dictionary{TKey, TValue}"/> requires a non-null key). Pseudo-element
    /// alternatives use their actual <see cref="SelectorBytecode.PseudoElement"/> name as
    /// the key — pseudo-element names are lowercased CSS identifiers and never collide
    /// with this sentinel.</summary>
    private const string HostBucketKey = "\0host";

    private static void ApplyRuleToElement(
        CompiledRule rule,
        IElement element,
        in SelectorBloomFilter bloom,
        CascadeResult result)
    {
        // Best matching alternative per pseudo-element bucket.
        Dictionary<string, SelectorBytecode>? bestPerBucket = null;
        var alts = rule.Selectors!.Alternatives;
        for (var altIdx = 0; altIdx < alts.Length; altIdx++)
        {
            var alt = alts[altIdx];
            if (!BloomAllows(alt, bloom)) continue;
            if (!SelectorMatcher.Match(alt, element)) continue;
            bestPerBucket ??= new Dictionary<string, SelectorBytecode>();
            var bucketKey = alt.PseudoElement ?? HostBucketKey;
            if (!bestPerBucket.TryGetValue(bucketKey, out var existing)
                || alt.Specificity > existing.Specificity)
            {
                bestPerBucket[bucketKey] = alt;
            }
        }
        if (bestPerBucket is null) return;
        foreach (var winning in bestPerBucket.Values)
        {
            AddMatched(rule, winning, element, result);
        }
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

    private static void WalkInlineStyles(IElement element, CascadeResult result, int inlineStylesheetOrder, System.Threading.CancellationToken cancellationToken)
    {
        WalkInlineStylesRecursive(element, result, inlineStylesheetOrder, ruleOrder: 0, cancellationToken);
    }

    private static int WalkInlineStylesRecursive(IElement element, CascadeResult result, int inlineStylesheetOrder, int ruleOrder, System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var styleAttr = element.GetAttribute("style");
        if (!string.IsNullOrEmpty(styleAttr))
        {
            var anglesharpStyle = AngleSharp.Css.Dom.ElementCssInlineStyleExtensions.GetStyle(element);
            if (anglesharpStyle is not null)
            {
                // Per Phase 2 deep review Rec 3 — feed the raw style attribute
                // text through CssPreprocessor recovery so modern colors +
                // multi-arg attr() in inline styles reach the typed pipeline,
                // matching the behavior of <style> blocks.
                var declarations = CssParserAdapter.AdaptInlineStyleWithRecovery(
                    anglesharpStyle, styleAttr, CssSourceLocation.Unknown);
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
            ruleOrder = WalkInlineStylesRecursive(child, result, inlineStylesheetOrder, ruleOrder, cancellationToken);
        }
        return ruleOrder;
    }
}
