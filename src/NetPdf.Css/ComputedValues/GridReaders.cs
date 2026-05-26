// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// Per Phase 3 Task 17 cycle 0b — extension methods on <see cref="ComputedStyle"/>
/// that decode the six grid longhand slots into their typed AST values
/// (<see cref="TrackList"/> / <see cref="GridLineValue"/>).
///
/// <para><b>Slot tag dispatch.</b> All six grid longhands land their parsed AST in
/// the <see cref="ComputedStyle"/> side-table dictionary; the slot itself carries a
/// <see cref="ComputedSlotTag.SideTableIndex"/> marker. The default value (none for
/// the <see cref="TrackList"/> family + auto for the <see cref="GridLineValue"/>
/// family) lands as a <see cref="ComputedSlotTag.Keyword"/> slot with id 0 — the
/// reader maps that path to <see cref="TrackList.None"/> /
/// <see cref="GridLineValue.Auto"/> without a side-table lookup. Any other slot tag
/// (Unset, LengthPx leakage, etc.) ALSO falls back to the default — matches the
/// dimension family's slot-tag-mismatch fallback pattern in <c>ReadFlexBasis</c>.</para>
///
/// <para><b>Why extension methods.</b> Mirrors the
/// <c>ComputedStyleLayoutExtensions.ReadFlexBasis</c> pattern from Phase 3 Task 15
/// L8. Keeps the slot-decode logic out of the GridLayouter (= cycle 1+ work) and
/// localizes the side-table key convention here so the layouter can read via a
/// single typed call.</para>
///
/// <para><b>File location.</b> Lives in <c>NetPdf.Css</c> (not
/// <c>NetPdf.Layout</c>) because the AST types (<see cref="TrackList"/> /
/// <see cref="GridLineValue"/>) are CSS-internal — the readers belong with the
/// types they decode. The GridLayouter (cycle 1+, in <c>NetPdf.Layout</c>) calls
/// these via the InternalsVisibleTo wire-up.</para>
/// </summary>
internal static class GridReaders
{
    /// <summary>Per Phase 3 Task 17 cycle 0b — read the parsed
    /// <c>grid-template-rows</c> AST. Returns <see cref="TrackList.None"/> when the
    /// property is unset, was declared as <c>none</c> (= the default), or the
    /// side-table entry is missing / wrong-typed (= defensive fallback matching the
    /// dimension family's pattern).</summary>
    public static TrackList ReadGridTemplateRows(this ComputedStyle style)
    {
        return ReadTrackListProperty(style, PropertyId.GridTemplateRows);
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the parsed
    /// <c>grid-template-columns</c> AST. Same contract as
    /// <see cref="ReadGridTemplateRows"/>.</summary>
    public static TrackList ReadGridTemplateColumns(this ComputedStyle style)
    {
        return ReadTrackListProperty(style, PropertyId.GridTemplateColumns);
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the parsed
    /// <c>grid-row-start</c> value. Returns <see cref="GridLineValue.Auto"/> for
    /// unset / default / wrong-typed slots (= the CSS-spec default per §8.3).</summary>
    public static GridLineValue ReadGridRowStart(this ComputedStyle style)
    {
        return ReadGridLineProperty(style, PropertyId.GridRowStart);
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the parsed
    /// <c>grid-row-end</c> value. Same contract as
    /// <see cref="ReadGridRowStart"/>.</summary>
    public static GridLineValue ReadGridRowEnd(this ComputedStyle style)
    {
        return ReadGridLineProperty(style, PropertyId.GridRowEnd);
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the parsed
    /// <c>grid-column-start</c> value. Same contract as
    /// <see cref="ReadGridRowStart"/>.</summary>
    public static GridLineValue ReadGridColumnStart(this ComputedStyle style)
    {
        return ReadGridLineProperty(style, PropertyId.GridColumnStart);
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the parsed
    /// <c>grid-column-end</c> value. Same contract as
    /// <see cref="ReadGridRowStart"/>.</summary>
    public static GridLineValue ReadGridColumnEnd(this ComputedStyle style)
    {
        return ReadGridLineProperty(style, PropertyId.GridColumnEnd);
    }

    private static TrackList ReadTrackListProperty(ComputedStyle style, PropertyId id)
    {
        var slot = style.Get(id);
        // SideTableIndex tag → look up the AST. Any other tag (Keyword default,
        // Unset, etc.) maps to the property default (= None per §7.2).
        if (slot.Tag == ComputedSlotTag.SideTableIndex
            && style.TryGetSideTablePayload<TrackList>(id, out var list))
        {
            return list;
        }
        return TrackList.None;
    }

    private static GridLineValue ReadGridLineProperty(ComputedStyle style, PropertyId id)
    {
        var slot = style.Get(id);
        // SideTableIndex tag → look up the AST. Keyword(0) / Unset / wrong-typed
        // payload all map to Auto (= the §8.3 default).
        if (slot.Tag == ComputedSlotTag.SideTableIndex
            && style.TryGetSideTablePayloadStruct<GridLineValue>(id, out var value))
        {
            return value;
        }
        return GridLineValue.Auto;
    }
}
