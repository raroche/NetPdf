// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// The computed value of CSS <c>font-family</c> — the prioritized list of family
/// names + generic families (serif / sans-serif / monospace / …) in author order
/// (CSS Fonts 4 §2.1). A list doesn't fit an 8-byte <see cref="ComputedSlot"/>, so
/// it's stored in <see cref="ComputedStyle"/>'s side table (a
/// <see cref="ComputedSlotTag.SideTableIndex"/> slot points to it), mirroring how
/// the grid <c>TrackList</c> is stored. The font resolver / shaper walks the list
/// in order, falling back through the entries.
/// </summary>
internal sealed record FontFamilyList(ImmutableArray<string> Families)
{
    /// <summary>The initial value — the single generic <c>serif</c>.</summary>
    public static FontFamilyList Default { get; } = new(ImmutableArray.Create("serif"));

    /// <summary>The first (highest-priority) family, or <c>serif</c> when empty.</summary>
    public string Primary => Families.IsDefaultOrEmpty ? "serif" : Families[0];
}
