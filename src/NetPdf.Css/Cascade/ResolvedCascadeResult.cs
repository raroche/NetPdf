// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Output of <see cref="VarResolver.Resolve"/> — per-(element, pseudo-element)
/// <see cref="ResolvedRuleSet"/> with cascade winners + custom-property tables ready for
/// Tasks 9–10 typed-value resolution.
/// </summary>
internal sealed class ResolvedCascadeResult
{
    private readonly Dictionary<IElement, ResolvedRuleSet> _elementStyles = new();
    private readonly Dictionary<(IElement Element, string Pseudo), ResolvedRuleSet> _pseudoStyles = new();

    /// <summary>Get-or-create the host element's resolved rule set.</summary>
    public ResolvedRuleSet StylesFor(IElement element, CustomPropertyTable customProperties)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_elementStyles.TryGetValue(element, out var set))
        {
            set = new ResolvedRuleSet(customProperties);
            _elementStyles[element] = set;
        }
        return set;
    }

    /// <summary>Get-or-create the pseudo-element resolved rule set on the given host.</summary>
    public ResolvedRuleSet StylesForPseudo(IElement element, string pseudoElement, CustomPropertyTable customProperties)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(pseudoElement);
        var key = (element, pseudoElement);
        if (!_pseudoStyles.TryGetValue(key, out var set))
        {
            set = new ResolvedRuleSet(customProperties, pseudoElement);
            _pseudoStyles[key] = set;
        }
        return set;
    }

    /// <summary>Read-only lookup — returns <see langword="null"/> when the element has
    /// no resolved set (no rules matched).</summary>
    public ResolvedRuleSet? TryGetStylesFor(IElement element) =>
        _elementStyles.TryGetValue(element, out var set) ? set : null;

    /// <summary>Read-only lookup for a pseudo-element set.</summary>
    public ResolvedRuleSet? TryGetStylesForPseudo(IElement element, string pseudoElement) =>
        _pseudoStyles.TryGetValue((element, pseudoElement), out var set) ? set : null;

    /// <summary>All elements with at least one resolved declaration.</summary>
    public IEnumerable<IElement> StyledElements => _elementStyles.Keys;

    /// <summary>All (element, pseudo-element) pairs with at least one resolved declaration.</summary>
    public IEnumerable<(IElement Element, string Pseudo)> StyledPseudoElements => _pseudoStyles.Keys;

    /// <summary>Total host-element rule sets.</summary>
    public int ElementCount => _elementStyles.Count;

    /// <summary>Total pseudo-element rule sets.</summary>
    public int PseudoElementCount => _pseudoStyles.Count;
}
