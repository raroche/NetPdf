// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Phase 3 Task 17 cycle 0 — tests for the grid property registration
/// (= the 6 new properties added to <c>properties.json</c>:
/// <c>grid-template-rows</c>, <c>grid-template-columns</c>,
/// <c>grid-row-start</c>, <c>-end</c>, <c>grid-column-start</c>,
/// <c>-end</c>) + the AST types in <c>Grid.cs</c>.
///
/// <para>Cycle 0a scope: <b>foundation only</b>. The CSS parser
/// integration (= cascade resolves a CSS string into a
/// <see cref="TrackList"/> AST) is cycle 0b; shorthand expansion
/// for <c>grid-row</c> + <c>grid-column</c> + <c>grid-area</c> is
/// cycle 0c. These tests pin the property registration + AST type
/// shape so the parser work in 0b/0c has a solid foundation to
/// extend.</para>
/// </summary>
public sealed class GridPropertyMetadataTests
{
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
        Assert.True(PropertyMetadata.NameToId.ContainsKey(name),
            $"Property '{name}' must be registered (cycle 0 foundation).");
        var id = PropertyMetadata.NameToId[name];
        var meta = PropertyMetadata.Table[(int)id];
        Assert.Equal(PropertyType.GridLine, meta.Type);
        Assert.Equal(AppliesTo.GridItems, meta.AppliesTo);
        Assert.False(meta.Inherits);
    }

    [Fact]
    public void TrackList_None_is_empty()
    {
        // The CSS default (= grid-template-rows: none) is an empty
        // track list. Layout-time expansion of an empty list yields
        // zero explicit tracks; implicit-only grid semantics apply.
        Assert.NotNull(TrackList.None);
        Assert.Empty(TrackList.None.Items);
    }

    [Fact]
    public void GridLineValue_Auto_default_carries_auto_kind_and_zero_line()
    {
        var auto = GridLineValue.Auto;
        Assert.Equal(GridLineKind.Auto, auto.Kind);
        Assert.Equal(0, auto.LineNumber);
        Assert.Null(auto.NamedLine);
    }

    [Fact]
    public void GridLineValue_can_encode_explicit_line_number()
    {
        // Cycle 0 supports integer line numbers. Negative numbers
        // count from the explicit grid's end per CSS Grid §8.3.
        var positive = new GridLineValue(GridLineKind.LineNumber, 2, null);
        Assert.Equal(GridLineKind.LineNumber, positive.Kind);
        Assert.Equal(2, positive.LineNumber);

        var negative = new GridLineValue(GridLineKind.LineNumber, -1, null);
        Assert.Equal(-1, negative.LineNumber);
    }

    [Fact]
    public void GridLineValue_can_encode_span()
    {
        // span N: the line value's LineNumber stores the span count
        // (always positive per §8.3).
        var span = new GridLineValue(GridLineKind.Span, 3, null);
        Assert.Equal(GridLineKind.Span, span.Kind);
        Assert.Equal(3, span.LineNumber);
    }

    [Fact]
    public void GridLineValue_can_encode_named_line()
    {
        // Cycle 7 ships named-line resolution; cycle 0 just verifies
        // the AST shape can carry the name.
        var named = new GridLineValue(GridLineKind.NamedLine, 0, "header");
        Assert.Equal(GridLineKind.NamedLine, named.Kind);
        Assert.Equal("header", named.NamedLine);
    }

    [Fact]
    public void TrackEntry_LengthPx_kind_carries_pixel_value()
    {
        // Cycle 1 will produce these for <length> tracks.
        var entry = new TrackEntry(
            Kind: GridTrackKind.LengthPx,
            LengthPx: 100.0,
            FrValue: 0,
            MinSubKind: default,
            MinSubLengthPx: 0,
            MaxSubKind: default,
            MaxSubLengthPx: 0,
            MaxSubFrValue: 0);
        Assert.Equal(GridTrackKind.LengthPx, entry.Kind);
        Assert.Equal(100.0, entry.LengthPx);
    }

    [Fact]
    public void TrackEntry_Fr_kind_carries_flex_value()
    {
        // Cycle 2 will produce these for <flex> tracks.
        var entry = new TrackEntry(
            Kind: GridTrackKind.Fr,
            LengthPx: 0,
            FrValue: 1.5,
            MinSubKind: default,
            MinSubLengthPx: 0,
            MaxSubKind: default,
            MaxSubLengthPx: 0,
            MaxSubFrValue: 0);
        Assert.Equal(GridTrackKind.Fr, entry.Kind);
        Assert.Equal(1.5, entry.FrValue);
    }

    [Fact]
    public void TrackEntry_MinMax_kind_carries_both_sub_args_inline()
    {
        // Per PR-#88 review P2 #4 — the MinMax case stores sub-args
        // inline (= no struct recursion). Cycle 4 will produce these.
        var entry = new TrackEntry(
            Kind: GridTrackKind.MinMax,
            LengthPx: 0,
            FrValue: 0,
            MinSubKind: GridTrackKind.LengthPx,
            MinSubLengthPx: 100.0,
            MaxSubKind: GridTrackKind.Fr,
            MaxSubLengthPx: 0,
            MaxSubFrValue: 1.0);
        Assert.Equal(GridTrackKind.MinMax, entry.Kind);
        Assert.Equal(GridTrackKind.LengthPx, entry.MinSubKind);
        Assert.Equal(100.0, entry.MinSubLengthPx);
        Assert.Equal(GridTrackKind.Fr, entry.MaxSubKind);
        Assert.Equal(1.0, entry.MaxSubFrValue);
    }

    [Fact]
    public void TrackListItem_hierarchy_can_be_discriminated()
    {
        // The sealed-hierarchy abstract record allows exhaustive
        // switching at layout time. Cycle 1 ships the inline-entry
        // case; cycles 4 + 7 ship the repeat + named-line cases.
        TrackListItem entry = new TrackListEntry(
            new TrackEntry(GridTrackKind.LengthPx, 100, 0, default, 0, default, 0, 0));
        TrackListItem repeat = new TrackListRepeat(
            new TrackRepeat(3, ImmutableArrayOf(
                new TrackEntry(GridTrackKind.LengthPx, 50, 0, default, 0, default, 0, 0))));
        TrackListItem named = new TrackListNamedLine("header");

        Assert.IsType<TrackListEntry>(entry);
        Assert.IsType<TrackListRepeat>(repeat);
        Assert.IsType<TrackListNamedLine>(named);
        Assert.Equal("header", ((TrackListNamedLine)named).Name);
    }

    [Fact]
    public void TrackRepeat_Count_encoding_matches_design()
    {
        // Per the v2 design doc §4: Count > 0 = explicit; Count == 0
        // = auto-fill; Count == -1 = auto-fit. Cycle 4 implements
        // the integer case; cycle 7 implements auto-fill/auto-fit.
        var explicit3 = new TrackRepeat(
            3,
            ImmutableArrayOf(new TrackEntry(GridTrackKind.LengthPx, 50, 0, default, 0, default, 0, 0)));
        Assert.Equal(3, explicit3.Count);

        var autoFill = new TrackRepeat(
            0,
            ImmutableArrayOf(new TrackEntry(GridTrackKind.LengthPx, 50, 0, default, 0, default, 0, 0)));
        Assert.Equal(0, autoFill.Count);

        var autoFit = new TrackRepeat(
            -1,
            ImmutableArrayOf(new TrackEntry(GridTrackKind.LengthPx, 50, 0, default, 0, default, 0, 0)));
        Assert.Equal(-1, autoFit.Count);
    }

    // Helper because xUnit + ImmutableArray construction is verbose.
    private static System.Collections.Immutable.ImmutableArray<T> ImmutableArrayOf<T>(params T[] items) =>
        System.Collections.Immutable.ImmutableArray.Create(items);
}
