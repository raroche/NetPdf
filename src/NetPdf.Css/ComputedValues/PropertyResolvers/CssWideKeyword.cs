// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// The CSS-wide keywords (CSS Cascade L5 §7.x: <c>initial</c> / <c>inherit</c> /
/// <c>unset</c> / <c>revert</c> / <c>revert-layer</c>). They are valid on EVERY property
/// and are resolved by the CASCADE, never by a property-specific leaf resolver — so a
/// resolver that would otherwise store them as a literal value (e.g. a
/// <c>font-family</c> named "inherit", or a named grid line "initial") rejects them
/// defensively via <see cref="Is"/>.
/// </summary>
/// <remarks>
/// A central interceptor in the cascade (substituting the property's initial / inherited
/// / previous-layer value before dispatch) is the proper fix and a separate cycle's
/// scope. Until then each complex resolver self-defends; for an INHERITED property the
/// cascade's invalid-fallback yields the inherited value, which is correct for
/// <c>inherit</c> / <c>unset</c> but NOT for <c>initial</c> / <c>revert</c> (a tracked
/// gap — see <c>deferrals.md#layout-to-pdf-pipeline</c>).
/// </remarks>
internal static class CssWideKeyword
{
    /// <summary><see langword="true"/> when <paramref name="value"/> (trimmed) is one of
    /// the five CSS-wide keywords, case-insensitively.</summary>
    public static bool Is(ReadOnlySpan<char> value)
    {
        var v = value.Trim();
        return v.Equals("initial", StringComparison.OrdinalIgnoreCase)
            || v.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || v.Equals("unset", StringComparison.OrdinalIgnoreCase)
            || v.Equals("revert", StringComparison.OrdinalIgnoreCase)
            || v.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);
    }
}
