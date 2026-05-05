// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Resolves <c>var(--name, fallback)</c> references in cascade winners against each
/// element's effective custom-property table per CSS Custom Properties L1 §3 + §3.5.
/// Walks the DOM top-down so the inheritance chain (parent's resolved custom properties
/// flow into each child's table per L1 §2.1) is built incrementally as the walk
/// progresses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline.</b>
/// </para>
/// <list type="number">
///   <item><description>Walk the document in document order. For each element:
///   collect its winning custom-property declarations from the
///   <see cref="MatchedRuleSet"/>; layer them on top of the parent's effective
///   <see cref="CustomPropertyTable"/>.</description></item>
///   <item><description>Resolve each own-layer custom-property value through
///   <see cref="VarSubstitution.Substitute"/> (custom-property values can themselves
///   contain <c>var()</c> references — resolved with the element's full effective
///   table; circular chains emit <c>CSS-VAR-CIRCULAR-001</c>).</description></item>
///   <item><description>For every non-custom property winner, substitute <c>var()</c>
///   in the value text. Pack into a <see cref="ResolvedDeclaration"/> with the
///   pre-substitution declaration carried for diagnostics + the original
///   <see cref="CascadeKey"/> so downstream stages still see the cascade ordering.</description></item>
///   <item><description>Recurse into child elements with the updated effective table.</description></item>
/// </list>
/// <para>
/// Pseudo-element matched-rule-sets reuse the host element's custom-property table since
/// pseudo-elements don't introduce their own scope — <c>::before</c> sees its host's
/// <c>--*</c> values per L1 §2.
/// </para>
/// </remarks>
internal static class VarResolver
{
    /// <summary>Resolve every <c>var()</c> reference in <paramref name="cascade"/>'s
    /// matched declarations and return a per-element resolved view.</summary>
    public static ResolvedCascadeResult Resolve(
        CascadeResult cascade,
        IDocument document,
        ICssDiagnosticsSink? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(cascade);
        ArgumentNullException.ThrowIfNull(document);

        var result = new ResolvedCascadeResult();
        if (document.DocumentElement is null) return result;

        WalkElement(document.DocumentElement, cascade, CustomPropertyTable.Empty,
            result, diagnostics);
        return result;
    }

    private static void WalkElement(
        IElement element,
        CascadeResult cascade,
        CustomPropertyTable parentCustomProperties,
        ResolvedCascadeResult result,
        ICssDiagnosticsSink? diagnostics)
    {
        var matched = cascade.TryGetStylesFor(element);
        // Build this element's effective custom-property table on top of the parent's
        // chain. Even when the element has no matched declarations, we still need a
        // per-element table so children inherit through us correctly.
        var ownTable = new CustomPropertyTable(parentCustomProperties);
        var hadCustomDecls = false;
        if (matched is not null)
        {
            hadCustomDecls = CollectOwnCustomProperties(matched, ownTable);
            // Pre-detect cycles so cycle members become invalid BEFORE substitution.
            // Per CSS Custom Properties L1 §3.5: every member of a dependency cycle
            // is "invalid at computed value time"; the earlier substitution-time guard
            // only invalidated the chain that hit the cycle first.
            CustomPropertyCycleDetector.DetectAndMarkInvalid(ownTable, diagnostics);
            ResolveOwnCustomPropertyValues(ownTable, diagnostics);
            EmitNonCustomDeclarations(matched, ownTable, element, result, diagnostics, isPseudo: false);
            // Rec 6: ensure elements that have ONLY custom-property declarations still
            // surface a ResolvedRuleSet so callers can read the resolved table via
            // result.TryGetStylesFor(element).CustomProperties. Without this, an element
            // like `<div style="--brand: red">` (no other styles) would inherit correctly
            // through the walk but be invisible to the post-resolve query API.
            if (hadCustomDecls && result.TryGetStylesFor(element) is null)
            {
                result.StylesFor(element, ownTable);
            }
        }

        // Pseudo-element resolution. Per CSS L1 §2: pseudo-elements have their own
        // styles and CAN declare their own custom properties — those should layer ON
        // TOP of the host's effective table for substitution within the pseudo's
        // declarations. So `p::before { --primary: blue; content: var(--primary) }`
        // sees ::before's --primary, not the host's.
        foreach (var pair in cascade.StyledPseudoElements)
        {
            if (!ReferenceEquals(pair.Element, element)) continue;
            var pseudoMatched = cascade.TryGetStylesForPseudo(pair.Element, pair.Pseudo);
            if (pseudoMatched is null) continue;
            var pseudoTable = new CustomPropertyTable(ownTable);
            CollectOwnCustomProperties(pseudoMatched, pseudoTable);
            CustomPropertyCycleDetector.DetectAndMarkInvalid(pseudoTable, diagnostics);
            ResolveOwnCustomPropertyValues(pseudoTable, diagnostics);
            EmitNonCustomDeclarations(pseudoMatched, pseudoTable, element, result, diagnostics,
                isPseudo: true, pseudoName: pair.Pseudo);
        }

        foreach (var child in element.Children)
        {
            WalkElement(child, cascade, ownTable, result, diagnostics);
        }
    }

    /// <summary>Walk <paramref name="matched"/>, find every winning custom-property
    /// declaration (one per name) and write its raw value into <paramref name="ownTable"/>
    /// at the OWN layer. Returns <see langword="true"/> when at least one custom-property
    /// declaration was written so callers can decide whether to register an exposed
    /// <see cref="ResolvedRuleSet"/>. Var() in the value isn't resolved here — that's
    /// <see cref="ResolveOwnCustomPropertyValues"/>'s job, which runs after every name is
    /// known so cross-references resolve correctly.</summary>
    private static bool CollectOwnCustomProperties(MatchedRuleSet matched, CustomPropertyTable ownTable)
    {
        var any = false;
        foreach (var name in matched.Properties)
        {
            if (!IsCustomPropertyName(name)) continue;
            var winner = matched.GetWinner(name);
            if (winner is null) continue;
            ownTable.Set(name, winner.Declaration.Value.RawText ?? string.Empty);
            any = true;
        }
        return any;
    }

    /// <summary>Now that every own-layer custom-property value is in place, resolve any
    /// <c>var()</c> references inside those values. The substitution sees the FULL
    /// effective table (own + inherited) — so <c>--child: var(--parent)</c> resolves
    /// against the inherited <c>--parent</c>. Names already marked invalid by the
    /// cycle pre-pass are SKIPPED so their invalid state survives this pass.
    /// Names whose substitution produces an "invalid at computed value time" result
    /// (missing reference with no fallback, depth/output limit, etc.) are marked invalid
    /// in the table per CSS Custom Properties L1 §3.5 — external <c>var(--name, fallback)</c>
    /// references then fall through to their fallback rather than picking up the
    /// <c>"unset"</c> sentinel string as if it were a valid value.</summary>
    private static void ResolveOwnCustomPropertyValues(
        CustomPropertyTable ownTable,
        ICssDiagnosticsSink? diagnostics)
    {
        // Snapshot the names to avoid mutating-while-enumerating.
        var names = new List<string>(ownTable.OwnNames);
        foreach (var name in names)
        {
            // Invalid names: skip — TryGetValue returns false for them so their stored
            // value is unreachable anyway. Re-Setting would clear the invalid flag,
            // re-promoting cycle members to "valid (with garbage value)".
            if (!ownTable.TryGetValue(name, out var raw)) continue;
            var resolved = VarSubstitution.Substitute(raw, ownTable, diagnostics);
            if (resolved.IsInvalid)
            {
                // Mark invalid instead of storing "unset" as a literal value. This is
                // the structured-substitution-result fix: external var(--name, fallback)
                // references will TryGetValue → false → use the var()'s fallback.
                ownTable.MarkInvalid(name);
            }
            else
            {
                ownTable.Set(name, resolved.Value);
            }
        }
    }

    private static void EmitNonCustomDeclarations(
        MatchedRuleSet matched,
        CustomPropertyTable customProperties,
        IElement element,
        ResolvedCascadeResult result,
        ICssDiagnosticsSink? diagnostics,
        bool isPseudo,
        string? pseudoName = null)
    {
        ResolvedRuleSet? targetSet = null;
        foreach (var name in matched.Properties)
        {
            if (IsCustomPropertyName(name)) continue;
            var winner = matched.GetWinner(name);
            if (winner is null) continue;
            var raw = winner.Declaration.Value.RawText ?? string.Empty;
            var substituted = VarSubstitution.Substitute(raw, customProperties, diagnostics,
                location: winner.Declaration.Location);
            // After var() substitution, run the math-function resolver (Task 9) so
            // calc() / min() / max() / clamp() / abs() / sign() expressions reduce to
            // single concrete values when possible. Mixed-unit cases (50% + 16px) are
            // preserved as text for layout to finalize. Diagnostic codes
            // CSS-CALC-INVALID-001 / CSS-CALC-DIV-BY-ZERO-001 emitted by the resolver.
            var calcReduced = CalcResolver.Resolve(substituted.Value, diagnostics,
                winner.Declaration.Location);
            var resolved = new SubstitutionResult(calcReduced, substituted.IsInvalid);
            // For non-custom declarations the structured "invalid" flag is informative
            // only — the value field already holds the right sentinel ("unset") that
            // Tasks 9–10 typed-value parsers will resolve to the property's initial
            // value per CSS Custom Properties L1 §3.5 (invalid-at-computed-value-time
            // semantics for non-custom properties).
            // Lazy-allocate the target set so elements with only custom-property
            // declarations (which are stored in CustomPropertyTable, not
            // ResolvedRuleSet) don't pollute StyledElements with an empty entry.
            targetSet ??= isPseudo
                ? result.StylesForPseudo(element, pseudoName!, customProperties)
                : result.StylesFor(element, customProperties);
            targetSet.Add(new ResolvedDeclaration(
                Property: name,
                ResolvedValue: resolved.Value,
                OriginalDeclaration: winner.Declaration,
                Key: winner.Key));
        }
    }

    private static bool IsCustomPropertyName(string name) =>
        name.Length >= 2 && name[0] == '-' && name[1] == '-';
}
