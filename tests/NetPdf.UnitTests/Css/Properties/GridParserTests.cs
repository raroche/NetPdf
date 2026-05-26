// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Phase 3 Task 17 cycle 0b — CSS parser/resolver tests for the grid
/// longhand family. Exercises <see cref="GridTemplateListResolver"/> and
/// <see cref="GridLineResolver"/> via the dispatch + cascade path, then
/// reads back via <see cref="GridReaders"/> to verify the side-table
/// round-trip.
///
/// <para><b>Test shape</b>: each test follows the pattern
/// <c>(declared text) → PropertyResolverDispatch.Resolve →
/// ResolverResult.MaterializeInto(style) → style.ReadGridXxx() → assert
/// AST equality</c>. The intermediate slot tag is checked to confirm the
/// side-table path was used.</para>
///
/// <para><b>Test conventions</b> mirror <see cref="GridPropertyMetadataTests"/>
/// (cycle 0a) — same project, same naming style, same use of validating
/// factories where direct-construction is needed.</para>
/// </summary>
public sealed class GridParserTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static ComputedStyle Materialize(PropertyId id, string value, out CapturingSink sink)
    {
        sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(id, value, sink);
        var style = ComputedStyle.Rent();
        result.MaterializeInto(style, id);
        return style;
    }

    // =====================================================================
    //  grid-template-rows / grid-template-columns — TrackList round-trip
    // =====================================================================

    [Fact]
    public void GridTemplateRows_none_resolves_to_default_without_side_table()
    {
        using var style = Materialize(PropertyId.GridTemplateRows, "none", out var sink);
        Assert.Empty(sink.Diagnostics);

        var slot = style.Get(PropertyId.GridTemplateRows);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);  // default keyword path
        Assert.Equal(GridTemplateListResolver.KeywordIdNone, slot.AsKeyword());

        var list = style.ReadGridTemplateRows();
        Assert.Same(TrackList.None, list);
    }

    [Fact]
    public void GridTemplateRows_two_pixel_tracks_round_trip_through_side_table()
    {
        using var style = Materialize(PropertyId.GridTemplateRows, "100px 200px", out var sink);
        Assert.Empty(sink.Diagnostics);

        var slot = style.Get(PropertyId.GridTemplateRows);
        Assert.Equal(ComputedSlotTag.SideTableIndex, slot.Tag);

        var list = style.ReadGridTemplateRows();
        Assert.Equal(2, list.Items.Length);
        var e0 = Assert.IsType<TrackListEntry>(list.Items[0]);
        var e1 = Assert.IsType<TrackListEntry>(list.Items[1]);
        Assert.Equal(GridTrackKind.Length, e0.Entry.Kind);
        Assert.Equal(100.0, e0.Entry.LengthPx);
        Assert.False(e0.Entry.IsPercentage);
        Assert.Equal(200.0, e1.Entry.LengthPx);
    }

    [Fact]
    public void GridTemplateColumns_mixed_fr_and_length_round_trip()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "200px 1fr 2fr", out var sink);
        Assert.Empty(sink.Diagnostics);

        var list = style.ReadGridTemplateColumns();
        Assert.Equal(3, list.Items.Length);
        var px = ((TrackListEntry)list.Items[0]).Entry;
        var fr1 = ((TrackListEntry)list.Items[1]).Entry;
        var fr2 = ((TrackListEntry)list.Items[2]).Entry;
        Assert.Equal(GridTrackKind.Length, px.Kind);
        Assert.Equal(200.0, px.LengthPx);
        Assert.Equal(GridTrackKind.Fr, fr1.Kind);
        Assert.Equal(1.0, fr1.FrValue);
        Assert.Equal(GridTrackKind.Fr, fr2.Kind);
        Assert.Equal(2.0, fr2.FrValue);
    }

    [Fact]
    public void GridTemplateRows_percent_track_preserves_unit()
    {
        using var style = Materialize(PropertyId.GridTemplateRows, "25% 50%", out var sink);
        Assert.Empty(sink.Diagnostics);

        var list = style.ReadGridTemplateRows();
        Assert.Equal(2, list.Items.Length);
        var e0 = ((TrackListEntry)list.Items[0]).Entry;
        var e1 = ((TrackListEntry)list.Items[1]).Entry;
        Assert.Equal(GridTrackKind.Length, e0.Kind);
        Assert.True(e0.IsPercentage);
        Assert.Equal(25.0, e0.LengthPx);
        Assert.True(e1.IsPercentage);
        Assert.Equal(50.0, e1.LengthPx);
    }

    [Fact]
    public void GridTemplateRows_auto_keyword_preserves_kind()
    {
        using var style = Materialize(PropertyId.GridTemplateRows, "auto", out var sink);
        Assert.Empty(sink.Diagnostics);
        var entry = ((TrackListEntry)Assert.Single(style.ReadGridTemplateRows().Items)).Entry;
        Assert.Equal(GridTrackKind.Auto, entry.Kind);
    }

    [Fact]
    public void GridTemplateRows_min_content_keyword_preserves_kind()
    {
        using var style = Materialize(PropertyId.GridTemplateRows, "min-content", out var sink);
        Assert.Empty(sink.Diagnostics);
        var entry = ((TrackListEntry)Assert.Single(style.ReadGridTemplateRows().Items)).Entry;
        Assert.Equal(GridTrackKind.MinContent, entry.Kind);
    }

    [Fact]
    public void GridTemplateRows_max_content_keyword_preserves_kind()
    {
        using var style = Materialize(PropertyId.GridTemplateRows, "max-content", out var sink);
        Assert.Empty(sink.Diagnostics);
        var entry = ((TrackListEntry)Assert.Single(style.ReadGridTemplateRows().Items)).Entry;
        Assert.Equal(GridTrackKind.MaxContent, entry.Kind);
    }

    [Fact]
    public void GridTemplateColumns_minmax_with_length_and_fr()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "minmax(100px, 1fr)", out var sink);
        Assert.Empty(sink.Diagnostics);

        var list = style.ReadGridTemplateColumns();
        var entry = ((TrackListEntry)Assert.Single(list.Items)).Entry;
        Assert.Equal(GridTrackKind.MinMax, entry.Kind);
        Assert.Equal(GridTrackKind.Length, entry.MinSubKind);
        Assert.Equal(100.0, entry.MinSubLengthPx);
        Assert.Equal(GridTrackKind.Fr, entry.MaxSubKind);
        Assert.Equal(1.0, entry.MaxSubFrValue);
    }

    [Fact]
    public void GridTemplateColumns_fit_content_with_length()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "fit-content(200px)", out var sink);
        Assert.Empty(sink.Diagnostics);
        var entry = ((TrackListEntry)Assert.Single(style.ReadGridTemplateColumns().Items)).Entry;
        Assert.Equal(GridTrackKind.FitContent, entry.Kind);
        Assert.Equal(200.0, entry.LengthPx);
        Assert.False(entry.IsPercentage);
    }

    [Fact]
    public void GridTemplateColumns_fit_content_with_percentage()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "fit-content(40%)", out var sink);
        Assert.Empty(sink.Diagnostics);
        var entry = ((TrackListEntry)Assert.Single(style.ReadGridTemplateColumns().Items)).Entry;
        Assert.Equal(GridTrackKind.FitContent, entry.Kind);
        Assert.True(entry.IsPercentage);
        Assert.Equal(40.0, entry.LengthPx);
    }

    [Fact]
    public void GridTemplateColumns_integer_repeat_preserves_compact_form()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(3, 100px)", out var sink);
        Assert.Empty(sink.Diagnostics);

        var list = style.ReadGridTemplateColumns();
        var repeat = ((TrackListRepeat)Assert.Single(list.Items)).Repeat;
        Assert.Equal(3, repeat.Count);
        var inner = Assert.Single(repeat.Pattern);
        var innerEntry = Assert.IsType<TrackRepeatEntry>(inner);
        Assert.Equal(GridTrackKind.Length, innerEntry.Entry.Kind);
        Assert.Equal(100.0, innerEntry.Entry.LengthPx);
    }

    [Fact]
    public void GridTemplateColumns_repeat_pattern_can_carry_multiple_entries()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(2, 100px 1fr)", out var sink);
        Assert.Empty(sink.Diagnostics);
        var repeat = ((TrackListRepeat)Assert.Single(style.ReadGridTemplateColumns().Items)).Repeat;
        Assert.Equal(2, repeat.Count);
        Assert.Equal(2, repeat.Pattern.Length);
        Assert.Equal(100.0, ((TrackRepeatEntry)repeat.Pattern[0]).Entry.LengthPx);
        Assert.Equal(1.0, ((TrackRepeatEntry)repeat.Pattern[1]).Entry.FrValue);
    }

    [Fact]
    public void GridTemplateColumns_auto_fill_repeat_carries_count_zero_marker()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(auto-fill, 100px)", out var sink);
        Assert.Empty(sink.Diagnostics);
        var repeat = ((TrackListRepeat)Assert.Single(style.ReadGridTemplateColumns().Items)).Repeat;
        Assert.Equal(0, repeat.Count);
    }

    [Fact]
    public void GridTemplateColumns_auto_fit_repeat_carries_count_minus_one_marker()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(auto-fit, 100px)", out var sink);
        Assert.Empty(sink.Diagnostics);
        var repeat = ((TrackListRepeat)Assert.Single(style.ReadGridTemplateColumns().Items)).Repeat;
        Assert.Equal(-1, repeat.Count);
    }

    [Fact]
    public void GridTemplateRows_named_lines_interleave_with_entries()
    {
        using var style = Materialize(
            PropertyId.GridTemplateRows, "[start] 100px [mid] 200px [end]", out var sink);
        Assert.Empty(sink.Diagnostics);
        var items = style.ReadGridTemplateRows().Items;
        Assert.Equal(5, items.Length);
        Assert.Equal("start", ((TrackListNamedLine)items[0]).Name);
        Assert.Equal(100.0, ((TrackListEntry)items[1]).Entry.LengthPx);
        Assert.Equal("mid", ((TrackListNamedLine)items[2]).Name);
        Assert.Equal(200.0, ((TrackListEntry)items[3]).Entry.LengthPx);
        Assert.Equal("end", ((TrackListNamedLine)items[4]).Name);
    }

    [Fact]
    public void GridTemplateColumns_repeat_can_carry_named_lines_inside_pattern()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(2, [col-start] 1fr [col-end])", out var sink);
        Assert.Empty(sink.Diagnostics);
        var repeat = ((TrackListRepeat)Assert.Single(style.ReadGridTemplateColumns().Items)).Repeat;
        Assert.Equal(2, repeat.Count);
        Assert.Equal(3, repeat.Pattern.Length);
        Assert.Equal("col-start", ((TrackRepeatNamedLine)repeat.Pattern[0]).Name);
        Assert.IsType<TrackRepeatEntry>(repeat.Pattern[1]);
        Assert.Equal("col-end", ((TrackRepeatNamedLine)repeat.Pattern[2]).Name);
    }

    // =====================================================================
    //  grid-template-rows / -columns — invalid / DoS rejection
    // =====================================================================

    [Fact]
    public void GridTemplateRows_calc_track_is_rejected_with_diagnostic()
    {
        using var style = Materialize(
            PropertyId.GridTemplateRows, "calc(100px + 1fr)", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
        // Invalid → reverts to property default (TrackList.None).
        Assert.Same(TrackList.None, style.ReadGridTemplateRows());
    }

    [Fact]
    public void GridTemplateColumns_repeat_with_excessive_count_is_rejected()
    {
        // Per PR-#89 P2 #7 — DoS guard at parse time.
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(99999999, 1px)", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Same(TrackList.None, style.ReadGridTemplateColumns());
    }

    [Fact]
    public void GridTemplateColumns_at_MaxRepeatCount_is_accepted()
    {
        // Boundary — exactly MaxRepeatCount is OK.
        var declared = $"repeat({TrackRepeat.MaxRepeatCount}, 1px)";
        using var style = Materialize(PropertyId.GridTemplateColumns, declared, out var sink);
        Assert.Empty(sink.Diagnostics);
        var repeat = ((TrackListRepeat)Assert.Single(style.ReadGridTemplateColumns().Items)).Repeat;
        Assert.Equal(TrackRepeat.MaxRepeatCount, repeat.Count);
    }

    [Fact]
    public void GridTemplateColumns_negative_track_size_is_rejected()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "-100px", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Same(TrackList.None, style.ReadGridTemplateColumns());
    }

    [Fact]
    public void GridTemplateColumns_minmax_rejects_fr_in_min()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "minmax(1fr, 200px)", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Same(TrackList.None, style.ReadGridTemplateColumns());
    }

    [Fact]
    public void GridTemplateColumns_nested_repeat_is_rejected()
    {
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "repeat(2, repeat(3, 100px))", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Same(TrackList.None, style.ReadGridTemplateColumns());
    }

    [Fact]
    public void GridTemplateColumns_relative_units_are_rejected()
    {
        // Cycle 0b doesn't have font/viewport context — em / vw are rejected.
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "10em", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Same(TrackList.None, style.ReadGridTemplateColumns());
    }

    [Fact]
    public void GridTemplateColumns_empty_track_list_is_rejected()
    {
        // Whitespace only — no tokens.
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "   ", out var sink);
        // Dispatch returns Invalid for empty trimmed input; sink may or may not
        // have a diagnostic (empty value short-circuits before resolver). Either
        // way the property reads None.
        Assert.Same(TrackList.None, style.ReadGridTemplateColumns());
    }

    // =====================================================================
    //  grid-row-start / -end / grid-column-start / -end — GridLineValue round-trip
    // =====================================================================

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]  // case-insensitive
    public void GridRowStart_auto_default_path(string css)
    {
        using var style = Materialize(PropertyId.GridRowStart, css, out var sink);
        Assert.Empty(sink.Diagnostics);
        var slot = style.Get(PropertyId.GridRowStart);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);  // default path
        Assert.Equal(GridLineKind.Auto, style.ReadGridRowStart().Kind);
    }

    [Fact]
    public void GridRowStart_bare_integer_round_trip()
    {
        using var style = Materialize(PropertyId.GridRowStart, "2", out var sink);
        Assert.Empty(sink.Diagnostics);
        var slot = style.Get(PropertyId.GridRowStart);
        Assert.Equal(ComputedSlotTag.SideTableIndex, slot.Tag);
        var v = style.ReadGridRowStart();
        Assert.Equal(GridLineKind.LineNumber, v.Kind);
        Assert.Equal(2, v.LineNumber);
        Assert.Null(v.NamedLine);
    }

    [Fact]
    public void GridRowEnd_negative_integer_round_trip()
    {
        using var style = Materialize(PropertyId.GridRowEnd, "-1", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.LineNumber, v.Kind);
        Assert.Equal(-1, v.LineNumber);
    }

    [Fact]
    public void GridColumnStart_zero_integer_is_rejected_per_spec()
    {
        // Per CSS Grid §8.3 — line number 0 is invalid.
        using var style = Materialize(PropertyId.GridColumnStart, "0", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        // Invalid reverts to property default (auto).
        Assert.Equal(GridLineKind.Auto, style.ReadGridColumnStart().Kind);
    }

    [Fact]
    public void GridColumnStart_bare_ident_resolves_to_named_line()
    {
        using var style = Materialize(PropertyId.GridColumnStart, "foo", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridColumnStart();
        Assert.Equal(GridLineKind.NamedLine, v.Kind);
        Assert.Equal("foo", v.NamedLine);
        Assert.Equal(0, v.LineNumber);
    }

    [Fact]
    public void GridColumnStart_ident_then_integer_combines()
    {
        using var style = Materialize(PropertyId.GridColumnStart, "foo 2", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridColumnStart();
        Assert.Equal(GridLineKind.LineNumber, v.Kind);
        Assert.Equal(2, v.LineNumber);
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridColumnStart_integer_then_ident_combines()
    {
        // && grammar — same value as "foo 2", just authored in reverse.
        using var style = Materialize(PropertyId.GridColumnStart, "2 foo", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridColumnStart();
        Assert.Equal(GridLineKind.LineNumber, v.Kind);
        Assert.Equal(2, v.LineNumber);
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridColumnEnd_span_integer()
    {
        using var style = Materialize(PropertyId.GridColumnEnd, "span 3", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridColumnEnd();
        Assert.Equal(GridLineKind.Span, v.Kind);
        Assert.Equal(3, v.LineNumber);
        Assert.Null(v.NamedLine);
    }

    [Fact]
    public void GridColumnEnd_span_ident()
    {
        using var style = Materialize(PropertyId.GridColumnEnd, "span foo", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridColumnEnd();
        Assert.Equal(GridLineKind.Span, v.Kind);
        Assert.Equal(0, v.LineNumber);  // 0 = no explicit count
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridColumnEnd_span_ident_then_integer()
    {
        using var style = Materialize(PropertyId.GridColumnEnd, "span foo 2", out var sink);
        Assert.Empty(sink.Diagnostics);
        var v = style.ReadGridColumnEnd();
        Assert.Equal(GridLineKind.Span, v.Kind);
        Assert.Equal(2, v.LineNumber);
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridColumnEnd_span_alone_is_rejected()
    {
        using var style = Materialize(PropertyId.GridColumnEnd, "span", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Equal(GridLineKind.Auto, style.ReadGridColumnEnd().Kind);
    }

    [Fact]
    public void GridColumnEnd_span_zero_is_rejected()
    {
        using var style = Materialize(PropertyId.GridColumnEnd, "span 0", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void GridColumnEnd_span_negative_is_rejected()
    {
        using var style = Materialize(PropertyId.GridColumnEnd, "span -2", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void GridLine_calc_is_rejected()
    {
        using var style = Materialize(PropertyId.GridRowStart, "calc(1 + 1)", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void GridLine_with_two_integers_is_rejected()
    {
        using var style = Materialize(PropertyId.GridRowStart, "2 3", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void GridLine_with_two_idents_is_rejected()
    {
        using var style = Materialize(PropertyId.GridRowStart, "foo bar", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    // =====================================================================
    //  Cross-cutting — side-table cleanup invariants
    // =====================================================================

    [Fact]
    public void Side_table_payload_is_replaced_when_property_resets_to_default()
    {
        // Bug guard — a subsequent cascade winner with the default keyword
        // must wipe the prior side-table AST.
        using var style = ComputedStyle.Rent();
        // First — set to a non-default value (= side-table populated).
        var r1 = PropertyResolverDispatch.Resolve(PropertyId.GridTemplateRows, "100px");
        Assert.True(r1.MaterializeInto(style, PropertyId.GridTemplateRows));
        Assert.Single(style.ReadGridTemplateRows().Items);

        // Second — overwrite with "none" (default). Side-table must drop.
        var r2 = PropertyResolverDispatch.Resolve(PropertyId.GridTemplateRows, "none");
        Assert.True(r2.MaterializeInto(style, PropertyId.GridTemplateRows));
        Assert.Same(TrackList.None, style.ReadGridTemplateRows());
        var slot = style.Get(PropertyId.GridTemplateRows);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
    }

    [Fact]
    public void Side_table_payload_round_trip_equals_authored_AST()
    {
        // Verify the stored AST equals what direct construction would build.
        // Note: TrackList's record equality compares ImmutableArray by reference,
        // not by item value — so we compare item-by-item below. The TrackListItem
        // sealed-record subtypes DO implement value equality on their fields, so
        // we can equality-check each pair.
        using var style = Materialize(
            PropertyId.GridTemplateColumns, "100px 1fr [end]", out var sink);
        Assert.Empty(sink.Diagnostics);

        var list = style.ReadGridTemplateColumns();
        var expected = System.Collections.Immutable.ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100.0)),
            new TrackListEntry(TrackEntry.ForFr(1.0)),
            TrackListNamedLine.Create("end"));
        Assert.Equal(expected.Length, list.Items.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], list.Items[i]);
        }
    }

    [Fact]
    public void Side_table_is_cleared_by_Unset()
    {
        // Cascade revert / unset must drop the AST too.
        using var style = ComputedStyle.Rent();
        var r = PropertyResolverDispatch.Resolve(PropertyId.GridTemplateRows, "100px 200px");
        r.MaterializeInto(style, PropertyId.GridTemplateRows);
        Assert.Equal(2, style.ReadGridTemplateRows().Items.Length);

        style.Unset(PropertyId.GridTemplateRows);
        Assert.Same(TrackList.None, style.ReadGridTemplateRows());
    }

    [Fact]
    public void Pooled_reset_drops_prior_side_table_entries()
    {
        // After Dispose + Rent, the next caller must not see prior payloads.
        ComputedStyle pooled;
        {
            using var first = ComputedStyle.Rent();
            var r = PropertyResolverDispatch.Resolve(PropertyId.GridTemplateRows, "100px");
            r.MaterializeInto(first, PropertyId.GridTemplateRows);
            pooled = first;
        }
        // first was disposed (= returned to pool). The pool may or may not
        // re-rent the same instance, but if it does the side-table must
        // be cleared by Reset.
        using var second = ComputedStyle.Rent();
        Assert.Same(TrackList.None, second.ReadGridTemplateRows());
    }
}
