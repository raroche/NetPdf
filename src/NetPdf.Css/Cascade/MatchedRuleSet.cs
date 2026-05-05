// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace NetPdf.Css.Cascade;

/// <summary>
/// All <see cref="MatchedDeclaration"/>s that resolved to a single (element, pseudo-element)
/// pair, organized for fast per-property winner lookup. Built by <see cref="CascadeResolver"/>
/// during the DOM walk; consumed by Tasks 8–10 (var-substitution + computed-value resolution)
/// and Task 12 (BoxBuilder).
/// </summary>
/// <remarks>
/// <para>
/// Internally, declarations are bucketed by lowercased property name so the per-property
/// winner can be looked up in O(matches-for-this-property). The buckets are NOT pre-sorted —
/// callers iterate via <see cref="GetWinner(string)"/> / <see cref="GetAllForProperty(string)"/>
/// and pick the largest <see cref="MatchedDeclaration.Key"/>. Sorting at insert time would
/// be more wasteful: most properties end up with one or two matches, so the constant-factor
/// of a linear scan beats heap-sort overhead.
/// </para>
/// <para>
/// <b>Pseudo-element separation.</b> A given element has one <see cref="MatchedRuleSet"/>
/// for its own styles (PseudoElement = <see langword="null"/>), plus optional additional
/// sets for each pseudo-element targeted by selectors (<c>::before</c>, <c>::after</c>,
/// <c>::marker</c>, …). Task 14's pseudo-element materializer reads the pseudo-element
/// sets to build the synthetic boxes.
/// </para>
/// </remarks>
internal sealed class MatchedRuleSet
{
    private readonly Dictionary<string, List<MatchedDeclaration>> _byProperty
        = new(StringComparer.Ordinal);
    private int _count;

    /// <summary>The pseudo-element this set targets (<c>"before"</c>, <c>"marker"</c>, etc.),
    /// or <see langword="null"/> when this is the host element's own style set.</summary>
    public string? PseudoElement { get; }

    public MatchedRuleSet(string? pseudoElement = null)
    {
        PseudoElement = pseudoElement;
    }

    /// <summary>Total declarations in the set across every property.</summary>
    public int Count => _count;

    /// <summary>Properties this set has at least one declaration for. Returned in
    /// insertion order — useful for deterministic iteration.</summary>
    public IEnumerable<string> Properties => _byProperty.Keys;

    /// <summary>Add a declaration to the set. Property name is matched case-sensitively;
    /// callers must lowercase before calling (the cascade resolver does so).</summary>
    public void Add(MatchedDeclaration matched)
    {
        ArgumentNullException.ThrowIfNull(matched);
        var name = matched.Declaration.Property;
        if (!_byProperty.TryGetValue(name, out var list))
        {
            list = new List<MatchedDeclaration>(capacity: 2);
            _byProperty[name] = list;
        }
        list.Add(matched);
        _count++;
    }

    /// <summary>The cascade winner for <paramref name="property"/> — the declaration with
    /// the largest <see cref="MatchedDeclaration.Key"/>. Returns <see langword="null"/>
    /// when the property has no matched declarations on this element.</summary>
    public MatchedDeclaration? GetWinner(string property)
    {
        if (!_byProperty.TryGetValue(property, out var list)) return null;
        var winner = list[0];
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Key > winner.Key) winner = list[i];
        }
        return winner;
    }

    /// <summary>Every matched declaration for <paramref name="property"/>, in insertion
    /// order. Empty when none. Useful for Tasks 8–10 to resolve <c>revert</c> /
    /// <c>revert-layer</c> by walking lower-precedence declarations.</summary>
    public IReadOnlyList<MatchedDeclaration> GetAllForProperty(string property) =>
        _byProperty.TryGetValue(property, out var list)
            ? (IReadOnlyList<MatchedDeclaration>)list
            : ImmutableArray<MatchedDeclaration>.Empty;

    /// <summary>Snapshot of every matched declaration in the set, sorted by
    /// <see cref="MatchedDeclaration.Key"/> ascending. Convenience for tests + diagnostics —
    /// production code should use <see cref="GetWinner"/>.</summary>
    public ImmutableArray<MatchedDeclaration> ToSortedArray()
    {
        var result = ImmutableArray.CreateBuilder<MatchedDeclaration>(_count);
        foreach (var list in _byProperty.Values)
        {
            foreach (var d in list) result.Add(d);
        }
        result.Sort((a, b) => a.Key.CompareTo(b.Key));
        return result.ToImmutable();
    }
}
