// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Output of <see cref="CascadeResolver.Resolve"/> — per-element matched rule sets, plus
/// per-(element, pseudo-element) sets for selectors targeting <c>::before</c> /
/// <c>::after</c> / etc. AngleSharp <see cref="IElement"/> reference identity is the
/// dictionary key so callers can look up styles directly from a DOM node.
/// </summary>
internal sealed class CascadeResult
{
    private readonly Dictionary<IElement, MatchedRuleSet> _elementStyles = new();
    private readonly Dictionary<(IElement Element, string Pseudo), MatchedRuleSet> _pseudoStyles = new();

    /// <summary>Returns the host element's matched rule set, allocating + registering one
    /// if this is the first matching declaration for the element.</summary>
    public MatchedRuleSet StylesFor(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_elementStyles.TryGetValue(element, out var set))
        {
            set = new MatchedRuleSet();
            _elementStyles[element] = set;
        }
        return set;
    }

    /// <summary>Returns the matched rule set for a pseudo-element on the given host.
    /// Allocates on first call.</summary>
    public MatchedRuleSet StylesForPseudo(IElement element, string pseudoElement)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(pseudoElement);
        var key = (element, pseudoElement);
        if (!_pseudoStyles.TryGetValue(key, out var set))
        {
            set = new MatchedRuleSet(pseudoElement);
            _pseudoStyles[key] = set;
        }
        return set;
    }

    /// <summary>Returns the host-element matched rule set if one was registered, else
    /// <see langword="null"/>. Use this in read-only callers to avoid allocating empty sets.</summary>
    public MatchedRuleSet? TryGetStylesFor(IElement element) =>
        _elementStyles.TryGetValue(element, out var set) ? set : null;

    /// <summary>Returns the pseudo-element matched rule set if one was registered.</summary>
    public MatchedRuleSet? TryGetStylesForPseudo(IElement element, string pseudoElement) =>
        _pseudoStyles.TryGetValue((element, pseudoElement), out var set) ? set : null;

    /// <summary>All elements that picked up at least one matched declaration.</summary>
    public IEnumerable<IElement> StyledElements => _elementStyles.Keys;

    /// <summary>All (element, pseudo-element) pairs that picked up at least one matched
    /// declaration. Pseudo-element materialization (Task 14) iterates this.</summary>
    public IEnumerable<(IElement Element, string Pseudo)> StyledPseudoElements => _pseudoStyles.Keys;

    /// <summary>Total host-element rule sets (used by tests + diagnostics).</summary>
    public int ElementCount => _elementStyles.Count;

    /// <summary>Total pseudo-element rule sets.</summary>
    public int PseudoElementCount => _pseudoStyles.Count;
}
