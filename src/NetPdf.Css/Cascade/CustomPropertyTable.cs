// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Per-element resolved custom-property map. Lookup is case-sensitive per CSS Custom
/// Properties L1 §2 (custom-property names use the dashed-ident grammar with case-
/// preserving identity). The table is layered: each instance carries its own writes
/// PLUS a chain to its parent table, so child elements naturally inherit unmodified
/// custom properties from their ancestors per L1 §2.1 ("Custom properties are inherited
/// like any other CSS property.").
/// </summary>
/// <remarks>
/// <para>
/// <b>Inheritance shape.</b> Custom properties cascade. The root element collects the
/// winning own-element <c>--*</c> declarations from <see cref="MatchedRuleSet"/>; each
/// descendant adds a layer above the parent's table. Lookup (<see cref="TryGetValue"/>)
/// walks own writes first, then the parent chain. This avoids copying the parent map
/// at each depth — typical documents have ~5 custom properties at most, so the lookup
/// cost stays proportional to depth rather than depth × N-properties.
/// </para>
/// <para>
/// <b>Empty / root case.</b> <see cref="Empty"/> represents the "no custom properties"
/// table at the document root. Constructing with a parent of <see cref="Empty"/> behaves
/// exactly like having no parent.
/// </para>
/// </remarks>
internal sealed class CustomPropertyTable
{
    /// <summary>Sentinel root table — immutable so callers can use it as a no-op parent
    /// without risk of accidentally polluting global state via <see cref="Set"/>. The
    /// constructor below pins <see cref="_isImmutable"/> true ONLY for this instance.</summary>
    public static CustomPropertyTable Empty { get; } = new(parent: null, isImmutable: true);

    private readonly CustomPropertyTable? _parent;
    private readonly Dictionary<string, string> _own = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalid = new(StringComparer.Ordinal);
    private readonly bool _isImmutable;

    public CustomPropertyTable(CustomPropertyTable? parent) : this(parent, isImmutable: false) { }

    private CustomPropertyTable(CustomPropertyTable? parent, bool isImmutable)
    {
        _parent = parent;
        _isImmutable = isImmutable;
    }

    /// <summary>Set a custom-property value on this layer. Overrides any inherited value
    /// for the same name. Names must start with <c>--</c>; this is enforced by the
    /// caller (cascade resolver) since it sources names from already-validated
    /// <see cref="Parser.CssDeclaration.Property"/> strings.</summary>
    /// <exception cref="InvalidOperationException">When invoked on
    /// <see cref="Empty"/> — the singleton is immutable so callers can use it as a no-op
    /// parent without globally polluting future resolutions.</exception>
    public void Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (_isImmutable)
            throw new InvalidOperationException(
                "CustomPropertyTable.Empty is immutable. Construct a new table with Empty as parent if you need a fresh layer.");
        _own[name] = value;
        _invalid.Remove(name);
    }

    /// <summary>Mark a name as "invalid at computed value time" per CSS Custom
    /// Properties L1 §3.5 — used by the cycle-detection pass to flag every member of a
    /// dependency cycle. Subsequent <see cref="TryGetValue"/> calls return
    /// <see langword="false"/> for invalid names so the consuming
    /// <see cref="VarSubstitution"/> falls back to the referencing var()'s fallback.</summary>
    public void MarkInvalid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_isImmutable)
            throw new InvalidOperationException("CustomPropertyTable.Empty is immutable.");
        _invalid.Add(name);
    }

    /// <summary>Look up a custom-property value, walking own writes then the parent
    /// chain. Returns <see langword="false"/> when the name isn't defined anywhere
    /// on this element or its ancestors, OR when the name has been marked invalid via
    /// <see cref="MarkInvalid"/>.</summary>
    public bool TryGetValue(string name, out string value)
    {
        if (_invalid.Contains(name))
        {
            value = string.Empty;
            return false;
        }
        if (_own.TryGetValue(name, out var v))
        {
            value = v;
            return true;
        }
        if (_parent is not null && _parent.TryGetValue(name, out value!))
            return true;
        value = string.Empty;
        return false;
    }

    /// <summary>Number of custom properties set on THIS layer only (excludes inherited).
    /// Tests + diagnostics use this; the cascade itself uses <see cref="TryGetValue"/>.</summary>
    public int OwnCount => _own.Count;

    /// <summary>Names set on this layer only — useful for tests + the var-resolver's
    /// "resolve own-layer writes first" pass.</summary>
    public IEnumerable<string> OwnNames => _own.Keys;

    /// <summary>Walks the chain (own + every ancestor) yielding each layer's
    /// <see cref="OwnNames"/> in turn. Used by the cycle-detection pre-pass to build
    /// a per-element dependency graph over every reachable custom property.</summary>
    public IEnumerable<string> AllReachableNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cursor = this; cursor is not null; cursor = cursor._parent)
        {
            foreach (var name in cursor._own.Keys)
            {
                if (seen.Add(name)) yield return name;
            }
        }
    }
}
