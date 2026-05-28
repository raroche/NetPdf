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

    /// <summary>Per Phase 3 Task 18 cycle 6 — read the parsed
    /// <c>grid-auto-rows</c> AST (= the per-row track-sizing pattern
    /// the implicit-track generator cycles through when items extend
    /// past the explicit grid). Returns a single-<see cref="GridTrackKind.Auto"/>
    /// <see cref="TrackList"/> for the property default (<c>auto</c>);
    /// any explicit declaration lands its AST in the side-table.
    ///
    /// <para><b>Cycling contract per §7.4:</b> when N implicit rows are
    /// needed and the AST has M entries, row at implicit index i uses
    /// entry <c>i mod M</c>. The empty-AST fallback (= unset / wrong-
    /// typed slot) returns a single <see cref="GridTrackKind.Auto"/>
    /// entry, matching the spec default.</para></summary>
    public static TrackList ReadGridAutoRows(this ComputedStyle style)
    {
        return ReadTrackListPropertyWithAutoDefault(style, PropertyId.GridAutoRows);
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 — read the parsed
    /// <c>grid-auto-columns</c> AST. Same contract as
    /// <see cref="ReadGridAutoRows"/>.</summary>
    public static TrackList ReadGridAutoColumns(this ComputedStyle style)
    {
        return ReadTrackListPropertyWithAutoDefault(style, PropertyId.GridAutoColumns);
    }

    /// <summary>Per Phase 3 Task 18 cycle 7a — read the parsed
    /// <c>grid-template-areas</c> AST. Returns
    /// <see cref="GridTemplateAreas.None"/> when the property is
    /// unset, declared as <c>none</c> (= default), or the side-table
    /// entry is missing / wrong-typed.</summary>
    public static GridTemplateAreas ReadGridTemplateAreas(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.GridTemplateAreas);
        if (slot.Tag == ComputedSlotTag.SideTableIndex
            && style.TryGetSideTablePayload<GridTemplateAreas>(
                PropertyId.GridTemplateAreas, out var areas))
        {
            return areas;
        }
        return GridTemplateAreas.None;
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 — read the
    /// <c>grid-auto-flow</c> keyword. Returns <see cref="GridAutoFlowValue.Row"/>
    /// for the unset / default / wrong-typed path (= the CSS-spec
    /// default per §7.7). Cycle 7 will add <c>dense</c> + the combined
    /// <c>row dense</c> / <c>column dense</c> forms.</summary>
    public static GridAutoFlowValue ReadGridAutoFlow(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.GridAutoFlow);
        if (slot.Tag == ComputedSlotTag.Keyword)
        {
            // Keyword id 0 = "row" (the default + first in the
            // KeywordResolver.Tables[GridAutoFlow] table).
            // Keyword id 1 = "column".
            return slot.AsKeyword() == 1
                ? GridAutoFlowValue.Column
                : GridAutoFlowValue.Row;
        }
        return GridAutoFlowValue.Row;
    }

    /// <summary>Helper that mirrors <see cref="ReadTrackListProperty"/>
    /// but maps the missing-side-table-entry path to a single
    /// <see cref="GridTrackKind.Auto"/> track (= the
    /// <c>grid-auto-rows</c> / <c>grid-auto-columns</c> spec default)
    /// instead of the empty <see cref="TrackList.None"/>. Without this,
    /// implicit-track generation in <c>GridSizing</c> would have no
    /// pattern to cycle through and items extending past the explicit
    /// grid would still be dropped.</summary>
    private static TrackList ReadTrackListPropertyWithAutoDefault(
        ComputedStyle style, PropertyId id)
    {
        var slot = style.Get(id);
        if (slot.Tag == ComputedSlotTag.SideTableIndex
            && style.TryGetSideTablePayload<TrackList>(id, out var list))
        {
            return list;
        }
        return SingleAutoTrack;
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 — the single-Auto-track
    /// TrackList used as the default for <c>grid-auto-rows</c> /
    /// <c>grid-auto-columns</c>. Allocated once + shared; the type is
    /// immutable.</summary>
    private static readonly TrackList SingleAutoTrack = new(
        System.Collections.Immutable.ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForAuto())));

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
