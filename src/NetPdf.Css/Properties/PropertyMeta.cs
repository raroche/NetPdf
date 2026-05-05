// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Properties;

/// <summary>
/// Per-property metadata indexed by <c>PropertyId</c>. The data table itself
/// (<c>PropertyMetadata.Table</c> as <c>ImmutableArray&lt;PropertyMeta&gt;</c>) and the
/// case-insensitive lookup dictionary (<c>PropertyMetadata.NameToId</c>) are emitted by
/// <c>NetPdf.SourceGen.CssPropertyGenerator</c> from <c>properties.json</c>; this record
/// is the typed shape downstream stages consume.
/// </summary>
/// <param name="Id">Stable identifier for the property; doubles as an index into
/// the metadata table.</param>
/// <param name="Name">The CSS property name as authored (lowercase, with any vendor
/// prefix). Lookup via <c>PropertyMetadata.NameToId</c> uses this.</param>
/// <param name="Type">Value-type taxonomy used to dispatch per-property parsing.</param>
/// <param name="DefaultValue">Initial value text per the CSS spec. The cascade
/// substitutes this when no declaration applies and the property doesn't inherit.</param>
/// <param name="Inherits"><see langword="true"/> when the property inherits per its
/// CSS spec (cascade copies from parent when no declaration applies).</param>
/// <param name="AppliesTo">Element class the property applies to.</param>
/// <param name="Computed">How the cascade transforms the specified value into the
/// computed value at the post-cascade stage.</param>
internal readonly record struct PropertyMeta(
    PropertyId Id,
    string Name,
    PropertyType Type,
    string DefaultValue,
    bool Inherits,
    AppliesTo AppliesTo,
    ComputedValueKind Computed);
