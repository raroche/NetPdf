// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Immutable;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Phase 3 Task 17 cycle 0a (+ post-PR-#89 review hardening) —
/// tests for the grid property registration + AST type contract.
///
/// <para><b>Cycle 0a scope (foundation only)</b>. The CSS parser
/// integration is cycle 0b; shorthand expansion is cycle 0c. These
/// tests pin the property registration + AST type shape +
/// validating-factory invariants so the parser work in 0b/0c has a
/// solid foundation to extend.</para>
/// </summary>
public sealed class GridPropertyMetadataTests
{
    // =====================================================================
    //  Property registration
    // =====================================================================

    [Fact]
    public void GridTemplateRows_is_registered()
    {
        Assert.True(PropertyMetadata.NameToId.ContainsKey("grid-template-rows"));
        var id = PropertyMetadata.NameToId["grid-template-rows"];
        var meta = PropertyMetadata.Table[(int)id];
        Assert.Equal("grid-template-rows", meta.Name);
        Assert.Equal(PropertyType.GridTemplateList, meta.Type);
        Assert.Equal(AppliesTo.GridContainers, meta.AppliesTo);
        Assert.False(meta.Inherits);
    }

    [Fact]
    public void GridTemplateColumns_is_registered()
    {
        Assert.True(PropertyMetadata.NameToId.ContainsKey("grid-template-columns"));
        var id = PropertyMetadata.NameToId["grid-template-columns"];
        var meta = PropertyMetadata.Table[(int)id];
        Assert.Equal(PropertyType.GridTemplateList, meta.Type);
        Assert.Equal(AppliesTo.GridContainers, meta.AppliesTo);
    }

    [Theory]
    [InlineData("grid-row-start")]
    [InlineData("grid-row-end")]
    [InlineData("grid-column-start")]
    [InlineData("grid-column-end")]
    public void GridLine_longhands_are_registered(string name)
    {
        Assert.True(PropertyMetadata.NameToId.ContainsKey(name));
        var id = PropertyMetadata.NameToId[name];
        var meta = PropertyMetadata.Table[(int)id];
        Assert.Equal(PropertyType.GridLine, meta.Type);
        Assert.Equal(AppliesTo.GridItems, meta.AppliesTo);
        Assert.False(meta.Inherits);
    }

    // =====================================================================
    //  AST defaults
    // =====================================================================

    [Fact]
    public void TrackList_None_is_empty()
    {
        Assert.Empty(TrackList.None.Items);
    }

    [Fact]
    public void GridLineValue_Auto_default_is_auto_kind()
    {
        Assert.Equal(GridLineKind.Auto, GridLineValue.Auto.Kind);
        Assert.Equal(0, GridLineValue.Auto.LineNumber);
        Assert.Null(GridLineValue.Auto.NamedLine);
    }

    // =====================================================================
    //  TrackEntry kinds (per PR-#89 review P2 #5: Auto / MinContent /
    //  MaxContent are DISTINCT kinds)
    // =====================================================================

    [Fact]
    public void TrackEntry_ForLength_carries_pixel_value()
    {
        var entry = TrackEntry.ForLength(100.0);
        Assert.Equal(GridTrackKind.Length, entry.Kind);
        Assert.Equal(100.0, entry.LengthPx);
        Assert.False(entry.IsPercentage);
    }

    [Fact]
    public void TrackEntry_ForPercentage_marks_IsPercentage_true()
    {
        // Per PR-#89 review P2 #6 — TrackEntry carries percentages
        // separately from pixels so the AST doesn't lose the unit.
        var entry = TrackEntry.ForPercentage(25.0);
        Assert.Equal(GridTrackKind.Length, entry.Kind);
        Assert.Equal(25.0, entry.LengthPx);
        Assert.True(entry.IsPercentage);
    }

    [Fact]
    public void TrackEntry_ForFr_carries_flex_value()
    {
        var entry = TrackEntry.ForFr(1.5);
        Assert.Equal(GridTrackKind.Fr, entry.Kind);
        Assert.Equal(1.5, entry.FrValue);
    }

    [Fact]
    public void TrackEntry_intrinsic_kinds_are_distinct()
    {
        // Per PR-#89 review P2 #5 — Auto / MinContent / MaxContent
        // are three DISTINCT kinds. The cycle-3 layouter may map
        // all three to the same approximate contribution under the
        // L19 deferral, but the AST preserves the authored keyword.
        Assert.Equal(GridTrackKind.Auto, TrackEntry.ForAuto().Kind);
        Assert.Equal(GridTrackKind.MinContent, TrackEntry.ForMinContent().Kind);
        Assert.Equal(GridTrackKind.MaxContent, TrackEntry.ForMaxContent().Kind);
        Assert.NotEqual(GridTrackKind.Auto, GridTrackKind.MinContent);
        Assert.NotEqual(GridTrackKind.MinContent, GridTrackKind.MaxContent);
    }

    [Fact]
    public void TrackEntry_ForMinMax_stores_sub_args_inline()
    {
        var entry = TrackEntry.ForMinMax(
            min: TrackEntry.ForLength(100.0),
            max: TrackEntry.ForFr(1.0));
        Assert.Equal(GridTrackKind.MinMax, entry.Kind);
        Assert.Equal(GridTrackKind.Length, entry.MinSubKind);
        Assert.Equal(100.0, entry.MinSubLengthPx);
        Assert.Equal(GridTrackKind.Fr, entry.MaxSubKind);
        Assert.Equal(1.0, entry.MaxSubFrValue);
    }

    [Fact]
    public void TrackEntry_ForFitContent_carries_limit()
    {
        var pxLimit = TrackEntry.ForFitContent(200.0);
        Assert.Equal(GridTrackKind.FitContent, pxLimit.Kind);
        Assert.Equal(200.0, pxLimit.LengthPx);
        Assert.False(pxLimit.IsPercentage);

        var pctLimit = TrackEntry.ForFitContent(50.0, isPercentage: true);
        Assert.True(pctLimit.IsPercentage);
        Assert.Equal(50.0, pctLimit.LengthPx);
    }

    // =====================================================================
    //  TrackEntry invariant validation (per PR-#89 review P3 #8)
    // =====================================================================

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(-100.0)]
    public void TrackEntry_ForLength_rejects_invalid(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrackEntry.ForLength(bad));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void TrackEntry_ForPercentage_rejects_invalid(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrackEntry.ForPercentage(bad));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void TrackEntry_ForFr_rejects_invalid(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrackEntry.ForFr(bad));
    }

    [Fact]
    public void TrackEntry_ForFr_accepts_zero_fr()
    {
        // 0fr is valid per CSS Grid §7.2.3 — receives 0 of leftover
        // space + behaves as a fixed 0-width track.
        var entry = TrackEntry.ForFr(0.0);
        Assert.Equal(0.0, entry.FrValue);
    }

    [Fact]
    public void TrackEntry_ForMinMax_rejects_fr_in_min()
    {
        // Per CSS Grid §7.2.4 — minmax() min arg cannot be <flex>.
        Assert.Throws<ArgumentException>(() => TrackEntry.ForMinMax(
            min: TrackEntry.ForFr(1.0),
            max: TrackEntry.ForLength(100.0)));
    }

    [Fact]
    public void TrackEntry_ForMinMax_rejects_nested_minmax_or_fit_content()
    {
        var outer = TrackEntry.ForMinMax(
            TrackEntry.ForLength(50),
            TrackEntry.ForLength(100));
        Assert.Throws<ArgumentException>(() => TrackEntry.ForMinMax(outer, TrackEntry.ForFr(1)));
        Assert.Throws<ArgumentException>(() => TrackEntry.ForMinMax(
            TrackEntry.ForLength(0),
            TrackEntry.ForFitContent(200)));
    }

    // =====================================================================
    //  GridLineValue grammar (per PR-#89 review P2 #4 — combined
    //  named-line + occurrence forms)
    // =====================================================================

    [Fact]
    public void GridLineValue_ForLineNumber_accepts_positive_and_negative()
    {
        Assert.Equal(2, GridLineValue.ForLineNumber(2).LineNumber);
        Assert.Equal(-1, GridLineValue.ForLineNumber(-1).LineNumber);
    }

    [Fact]
    public void GridLineValue_ForLineNumber_rejects_zero()
    {
        // Per CSS Grid §8.3 — line number 0 is invalid.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GridLineValue.ForLineNumber(0));
    }

    [Fact]
    public void GridLineValue_ForNamedLineNumber_combines_name_and_occurrence()
    {
        // Per PR-#89 review P2 #4 — CSS Grid §8.3 allows `foo 2`
        // = 2nd occurrence of named line "foo".
        var v = GridLineValue.ForNamedLineNumber("foo", 2);
        Assert.Equal(GridLineKind.LineNumber, v.Kind);
        Assert.Equal(2, v.LineNumber);
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridLineValue_ForSpan_count_must_be_positive()
    {
        Assert.Equal(2, GridLineValue.ForSpan(2).LineNumber);
        Assert.Throws<ArgumentOutOfRangeException>(() => GridLineValue.ForSpan(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridLineValue.ForSpan(-1));
    }

    [Fact]
    public void GridLineValue_ForSpanName_carries_name_with_zero_count()
    {
        // `span foo` = span to next occurrence of "foo".
        var v = GridLineValue.ForSpanName("foo");
        Assert.Equal(GridLineKind.Span, v.Kind);
        Assert.Equal(0, v.LineNumber);  // 0 = no explicit count
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridLineValue_ForSpanNameOccurrence_combines_count_and_name()
    {
        // `span foo 2` = span 2 occurrences of "foo".
        var v = GridLineValue.ForSpanNameOccurrence("foo", 2);
        Assert.Equal(GridLineKind.Span, v.Kind);
        Assert.Equal(2, v.LineNumber);
        Assert.Equal("foo", v.NamedLine);
    }

    [Fact]
    public void GridLineValue_ForNamedLine_carries_bare_name()
    {
        // Bare `foo` = first occurrence of named line.
        var v = GridLineValue.ForNamedLine("foo");
        Assert.Equal(GridLineKind.NamedLine, v.Kind);
        Assert.Equal(0, v.LineNumber);
        Assert.Equal("foo", v.NamedLine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GridLineValue_named_forms_reject_null_or_empty(string? bad)
    {
        Assert.Throws<ArgumentException>(() => GridLineValue.ForNamedLine(bad!));
        Assert.Throws<ArgumentException>(() => GridLineValue.ForSpanName(bad!));
        Assert.Throws<ArgumentException>(() => GridLineValue.ForNamedLineNumber(bad!, 1));
        Assert.Throws<ArgumentException>(() => GridLineValue.ForSpanNameOccurrence(bad!, 1));
    }

    // =====================================================================
    //  TrackRepeat — per PR-#89 review P1 #2 + P2 #7
    // =====================================================================

    [Fact]
    public void TrackRepeat_supports_named_lines_inside_pattern()
    {
        // Per PR-#89 review P1 #2 — CSS Grid §7.2.3 allows
        // repeat(N, [name] <track> [name]). Named lines INSIDE the
        // repeat group preserve their position + repeat with each
        // iteration.
        var pattern = ImmutableArray.Create<TrackRepeatItem>(
            TrackRepeatNamedLine.Create("col-start"),
            new TrackRepeatEntry(TrackEntry.ForFr(1.0)),
            TrackRepeatNamedLine.Create("col-end"));
        var repeat = TrackRepeat.Create(2, pattern);
        Assert.Equal(2, repeat.Count);
        Assert.Equal(3, repeat.Pattern.Length);
        Assert.IsType<TrackRepeatNamedLine>(repeat.Pattern[0]);
        Assert.IsType<TrackRepeatEntry>(repeat.Pattern[1]);
        Assert.IsType<TrackRepeatNamedLine>(repeat.Pattern[2]);
    }

    [Fact]
    public void TrackRepeat_Create_rejects_count_above_MaxRepeatCount()
    {
        // Per PR-#89 review P2 #7 — DoS guard.
        // repeat(1000000000, 1px) must NOT be accepted.
        var pattern = ImmutableArray.Create<TrackRepeatItem>(
            new TrackRepeatEntry(TrackEntry.ForLength(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TrackRepeat.Create(TrackRepeat.MaxRepeatCount + 1, pattern));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TrackRepeat.Create(1_000_000_000, pattern));
    }

    [Fact]
    public void TrackRepeat_Create_accepts_special_count_values()
    {
        var pattern = ImmutableArray.Create<TrackRepeatItem>(
            new TrackRepeatEntry(TrackEntry.ForLength(1)));
        // 0 = auto-fill, -1 = auto-fit, positive = explicit
        Assert.Equal(0, TrackRepeat.Create(0, pattern).Count);
        Assert.Equal(-1, TrackRepeat.Create(-1, pattern).Count);
        Assert.Equal(TrackRepeat.MaxRepeatCount, TrackRepeat.Create(TrackRepeat.MaxRepeatCount, pattern).Count);
    }

    [Fact]
    public void TrackRepeat_Create_rejects_invalid_count_below_minus_one()
    {
        var pattern = ImmutableArray.Create<TrackRepeatItem>(
            new TrackRepeatEntry(TrackEntry.ForLength(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrackRepeat.Create(-2, pattern));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrackRepeat.Create(int.MinValue, pattern));
    }

    [Fact]
    public void TrackRepeat_Create_rejects_empty_pattern()
    {
        Assert.Throws<ArgumentException>(
            () => TrackRepeat.Create(2, ImmutableArray<TrackRepeatItem>.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TrackRepeatNamedLine_Create_rejects_null_or_empty(string? bad)
    {
        Assert.Throws<ArgumentException>(() => TrackRepeatNamedLine.Create(bad!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TrackListNamedLine_Create_rejects_null_or_empty(string? bad)
    {
        Assert.Throws<ArgumentException>(() => TrackListNamedLine.Create(bad!));
    }

    [Fact]
    public void TrackListItem_hierarchy_can_be_discriminated()
    {
        TrackListItem entry = new TrackListEntry(TrackEntry.ForLength(100));
        TrackListItem repeat = new TrackListRepeat(
            TrackRepeat.Create(3, ImmutableArray.Create<TrackRepeatItem>(
                new TrackRepeatEntry(TrackEntry.ForLength(50)))));
        TrackListItem named = TrackListNamedLine.Create("header");

        Assert.IsType<TrackListEntry>(entry);
        Assert.IsType<TrackListRepeat>(repeat);
        Assert.IsType<TrackListNamedLine>(named);
        Assert.Equal("header", ((TrackListNamedLine)named).Name);
    }
}
