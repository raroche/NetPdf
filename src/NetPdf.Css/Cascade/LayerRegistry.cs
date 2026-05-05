// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Per-cascade dictionary mapping <c>@layer</c> name → assigned index. The cascade resolver
/// builds one of these while walking stylesheets and uses it to populate the
/// <see cref="CascadeKey.LayerOrder"/> field per matched declaration.
/// </summary>
/// <remarks>
/// <para>
/// Per CSS Cascade L4 §6.4.4 layer ordering: layers are declared in source order, and
/// earlier-declared = lower precedence for normal declarations (the comparison is reversed
/// for <c>!important</c>). Indices are 1-based; index 0 is reserved for "unlayered"
/// declarations and means the implicit final layer for normal / first layer for
/// <c>!important</c>.
/// </para>
/// <para>
/// Layer name collisions are no-ops: re-declaring an existing layer name keeps the original
/// index, matching the spec's "layers are merged across declarations" semantics.
/// Anonymous layers (<c>@layer { ... }</c>) get a synthetic unique name so each gets its
/// own index.
/// </para>
/// <para>
/// Nested layer names (<c>@layer foo.bar</c>) are treated as a single flat layer name in
/// v1 — the spec's hierarchical layer semantics is post-v1 work. So <c>@layer foo</c> and
/// <c>@layer foo.bar</c> get distinct top-level layer indices in v1; the implicit "bar
/// inherits foo's position" rule isn't honored. Documented as a known gap in
/// <c>docs/compatibility-matrix.md</c>.
/// </para>
/// </remarks>
internal sealed class LayerRegistry
{
    /// <summary>Reserved index for unlayered declarations.</summary>
    public const int UnlayeredIndex = 0;

    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private int _nextIndex = 1;
    private int _anonymousCounter = 0;

    /// <summary>Look up the index for <paramref name="name"/>, assigning the next available
    /// one if not already registered. Empty / null names get a unique synthetic anonymous
    /// name. Returns the layer's index.</summary>
    public int RegisterIfMissing(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            // Anonymous layer — synth a unique name so each gets its own slot.
            name = $"__anonymous_{++_anonymousCounter}";
        }
        if (!_indices.TryGetValue(name, out var idx))
        {
            idx = _nextIndex++;
            _indices[name] = idx;
        }
        return idx;
    }

    /// <summary>Number of layers registered so far. Useful for tests.</summary>
    public int Count => _indices.Count;

    /// <summary>Try to get the index for an already-registered name without registering a
    /// new one. Returns <c>0</c> (the unlayered sentinel) when not found.</summary>
    public int TryGetIndex(string? name)
    {
        if (string.IsNullOrEmpty(name)) return UnlayeredIndex;
        return _indices.TryGetValue(name, out var idx) ? idx : UnlayeredIndex;
    }

    /// <summary>Parse the prelude of an <c>@layer</c> at-rule into individual layer names.
    /// Statement-form preludes are comma-separated (<c>@layer foo, bar, baz</c>);
    /// block-form is typically a single name. Whitespace around names is trimmed.</summary>
    public static string[] ParsePrelude(string prelude)
    {
        if (string.IsNullOrWhiteSpace(prelude)) return Array.Empty<string>();
        var parts = prelude.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result.ToArray();
    }
}
