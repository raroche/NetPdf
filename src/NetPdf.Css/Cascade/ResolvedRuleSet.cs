// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Per-(element, pseudo-element) collection of <see cref="ResolvedDeclaration"/> winners
/// — one entry per longhand property, with <c>var()</c> already substituted. Produced
/// by <see cref="VarResolver"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="MatchedRuleSet"/> (which can hold multiple entries per property —
/// the lower-precedence ones serve as candidates for <c>revert</c> / <c>revert-layer</c>),
/// this set holds only the cascade WINNER per property. Tasks 9–10 convert each winner
/// into a typed <see cref="ComputedValues.ComputedSlot"/>.
/// </remarks>
internal sealed class ResolvedRuleSet
{
    private readonly Dictionary<string, ResolvedDeclaration> _byProperty
        = new(System.StringComparer.Ordinal);

    /// <summary>The pseudo-element this set targets, or <see langword="null"/> when this
    /// is the host element's own resolved set.</summary>
    public string? PseudoElement { get; }

    /// <summary>The element's resolved custom-property table — <c>--*</c> values that
    /// child elements inherit and that the typed-value resolvers (Tasks 9–10) can
    /// reference. Computed once per element by <see cref="VarResolver"/>; pseudo-element
    /// sets reuse the host's table since pseudo-elements are styled via the host's
    /// <c>::before</c> / <c>::after</c> selectors.</summary>
    public CustomPropertyTable CustomProperties { get; }

    public ResolvedRuleSet(CustomPropertyTable customProperties, string? pseudoElement = null)
    {
        CustomProperties = customProperties;
        PseudoElement = pseudoElement;
    }

    /// <summary>Total winning declarations in the set.</summary>
    public int Count => _byProperty.Count;

    /// <summary>Property names the set has winners for, in insertion order.</summary>
    public IEnumerable<string> Properties => _byProperty.Keys;

    /// <summary>Add (or overwrite) the winner for the property the
    /// <paramref name="declaration"/> targets.</summary>
    public void Add(ResolvedDeclaration declaration)
    {
        _byProperty[declaration.Property] = declaration;
    }

    /// <summary>Get the winning declaration for <paramref name="property"/>, or
    /// <see langword="null"/> if none.</summary>
    public ResolvedDeclaration? GetWinner(string property) =>
        _byProperty.TryGetValue(property, out var d) ? d : null;

    /// <summary>Snapshot of every winning declaration. Useful for tests + diagnostics.</summary>
    public ImmutableArray<ResolvedDeclaration> Winners
    {
        get
        {
            var b = ImmutableArray.CreateBuilder<ResolvedDeclaration>(_byProperty.Count);
            foreach (var d in _byProperty.Values) b.Add(d);
            return b.ToImmutable();
        }
    }
}
