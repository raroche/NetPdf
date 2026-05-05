// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Properties;

/// <summary>
/// How the cascade's "computed value" stage transforms a property's specified value
/// (post-cascade) into its computed form (pre-layout). Used to dispatch per-property
/// computation in Tasks 9–10.
/// </summary>
/// <remarks>
/// Per CSS Cascade L4 §4.4, the computed value is the value as resolved relative to the
/// document context — colors resolve against <c>currentcolor</c>, lengths to absolute px,
/// percentages to their containing-block-relative form (deferred to layout), etc.
/// </remarks>
internal enum ComputedValueKind : byte
{
    /// <summary>Specified value is its own computed value (most keyword properties).</summary>
    Specified = 0,
    /// <summary>Color resolves against <c>currentcolor</c> + the document context.</summary>
    AbsoluteColor = 1,
    /// <summary>Length resolves to absolute px (em → font-size × value, etc.).</summary>
    AbsoluteLength = 2,
    /// <summary>Numeric form is normalized (e.g., <c>font-weight: bold</c> → <c>700</c>).</summary>
    ResolvedNumber = 3,
    /// <summary>Keyword is canonicalized against a fixed alias table.</summary>
    ResolvedKeyword = 4,
    /// <summary>Property has a custom computation hook (per-property logic).</summary>
    Custom = 255,
}
