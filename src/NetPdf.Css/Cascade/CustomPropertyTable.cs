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
    public static CustomPropertyTable Empty { get; } = new(parent: null);

    private readonly CustomPropertyTable? _parent;
    private readonly Dictionary<string, string> _own = new(StringComparer.Ordinal);

    public CustomPropertyTable(CustomPropertyTable? parent)
    {
        _parent = parent;
    }

    /// <summary>Set a custom-property value on this layer. Overrides any inherited value
    /// for the same name. Names must start with <c>--</c>; this is enforced by the
    /// caller (cascade resolver) since it sources names from already-validated
    /// <see cref="Parser.CssDeclaration.Property"/> strings.</summary>
    public void Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _own[name] = value;
    }

    /// <summary>Look up a custom-property value, walking own writes then the parent
    /// chain. Returns <see langword="false"/> when the name isn't defined anywhere
    /// on this element or its ancestors.</summary>
    public bool TryGetValue(string name, out string value)
    {
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
}
