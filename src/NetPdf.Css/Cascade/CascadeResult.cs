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

    /// <summary>Per Phase C C-4 — running cumulative count of
    /// <see cref="MatchedDeclaration"/> entries added across all
    /// host-element + pseudo-element rule sets in this render. Compared
    /// against <see cref="CascadeResolver.MaxMatchedDeclarationsPerRender"/>
    /// in <see cref="CascadeResolver.AddMatched"/> + the inline-style walk
    /// to bound the per-render memory pressure when both per-rule + per-
    /// element caps stay individually under threshold but their product
    /// would blow the matched table.</summary>
    public int TotalMatchedDeclarationCount { get; private set; }

    /// <summary>Per Phase C C-4 — once this flag flips, every subsequent
    /// <see cref="CascadeResolver.AddMatched"/> call short-circuits before
    /// emitting more <see cref="MatchedDeclaration"/> entries. The single
    /// <c>CSS-CASCADE-OVERFLOW-001</c> diagnostic is emitted at the
    /// transition; subsequent attempts silently skip.</summary>
    public bool MatchedLimitReached { get; private set; }

    /// <summary>Add <paramref name="count"/> to the cumulative tracker.
    /// Returns <see langword="true"/> when the cap is hit on this call
    /// (caller emits the one-shot diagnostic + any subsequent calls go
    /// through but the matched-rule-sets simply aren't written to).</summary>
    internal bool TryConsumeMatched(int count)
    {
        if (MatchedLimitReached) return false;
        TotalMatchedDeclarationCount += count;
        if (TotalMatchedDeclarationCount > CascadeResolver.MaxMatchedDeclarationsPerRender)
        {
            MatchedLimitReached = true;
            return true; // first overflow — caller should emit + continue
        }
        return true;
    }
}
