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
        // per-element table so children inherit through us correctly — using a fresh
        // layer keeps the chain shape uniform without carrying empty sentinels.
        var ownTable = new CustomPropertyTable(parentCustomProperties);
        if (matched is not null)
        {
            CollectOwnCustomProperties(matched, ownTable);
            ResolveOwnCustomPropertyValues(ownTable, diagnostics);
            EmitNonCustomDeclarations(matched, ownTable, element, result, diagnostics, isPseudo: false);
        }
        else
        {
            // Register an empty resolved set so the lookup contract is consistent
            // (StyledElements only enumerates elements with actual resolved styles).
            // Skipped intentionally — keeping behavior symmetric with CascadeResult.
        }

        // Pseudo-element resolution — each (element, pseudo) bucket reuses the host's
        // custom-property table.
        foreach (var pair in cascade.StyledPseudoElements)
        {
            if (!ReferenceEquals(pair.Element, element)) continue;
            var pseudoMatched = cascade.TryGetStylesForPseudo(pair.Element, pair.Pseudo);
            if (pseudoMatched is null) continue;
            EmitNonCustomDeclarations(pseudoMatched, ownTable, element, result, diagnostics,
                isPseudo: true, pseudoName: pair.Pseudo);
        }

        foreach (var child in element.Children)
        {
            WalkElement(child, cascade, ownTable, result, diagnostics);
        }
    }

    /// <summary>Walk <paramref name="matched"/>, find every winning custom-property
    /// declaration (one per name) and write its raw value into <paramref name="ownTable"/>
    /// at the OWN layer. Var() in the value isn't resolved here — that's
    /// <see cref="ResolveOwnCustomPropertyValues"/>'s job, which runs after every name is
    /// known so cross-references resolve correctly.</summary>
    private static void CollectOwnCustomProperties(MatchedRuleSet matched, CustomPropertyTable ownTable)
    {
        foreach (var name in matched.Properties)
        {
            if (!IsCustomPropertyName(name)) continue;
            var winner = matched.GetWinner(name);
            if (winner is null) continue;
            ownTable.Set(name, winner.Declaration.Value.RawText ?? string.Empty);
        }
    }

    /// <summary>Now that every own-layer custom-property value is in place, resolve any
    /// <c>var()</c> references inside those values. The substitution sees the FULL
    /// effective table (own + inherited) — so <c>--child: var(--parent)</c> resolves
    /// against the inherited <c>--parent</c>.</summary>
    private static void ResolveOwnCustomPropertyValues(
        CustomPropertyTable ownTable,
        ICssDiagnosticsSink? diagnostics)
    {
        // Snapshot the names to avoid mutating-while-enumerating.
        var names = new List<string>(ownTable.OwnNames);
        foreach (var name in names)
        {
            ownTable.TryGetValue(name, out var raw);
            var resolved = VarSubstitution.Substitute(raw, ownTable, diagnostics);
            ownTable.Set(name, resolved);
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
            var resolved = VarSubstitution.Substitute(raw, customProperties, diagnostics,
                location: winner.Declaration.Location);
            // Lazy-allocate the target set so elements with only custom-property
            // declarations (which are stored in CustomPropertyTable, not
            // ResolvedRuleSet) don't pollute StyledElements with an empty entry.
            targetSet ??= isPseudo
                ? result.StylesForPseudo(element, pseudoName!, customProperties)
                : result.StylesFor(element, customProperties);
            targetSet.Add(new ResolvedDeclaration(
                Property: name,
                ResolvedValue: resolved,
                OriginalDeclaration: winner.Declaration,
                Key: winner.Key));
        }
    }

    private static bool IsCustomPropertyName(string name) =>
        name.Length >= 2 && name[0] == '-' && name[1] == '-';
}
