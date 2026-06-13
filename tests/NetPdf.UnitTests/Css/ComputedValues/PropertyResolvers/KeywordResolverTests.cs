// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

public sealed class KeywordResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // Note: PropertyId is internal so [InlineData] cannot reference its members
    // (xUnit needs public test signatures). Each admitted-keyword case is its own
    // [Fact] — verbose but keeps the assertions strongly-typed.

    private static void AssertResolves(string keyword, PropertyId pid)
    {
        var result = KeywordResolver.Resolve(keyword, pid, pid.ToString(), null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.True(result.Slot.AsKeyword() >= 0);
    }

    private static void AssertInvalid(string keyword, PropertyId pid)
    {
        var sink = new CapturingSink();
        var result = KeywordResolver.Resolve(keyword, pid, pid.ToString(), sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    [Fact] public void Display_block_resolves()           => AssertResolves("block", PropertyId.Display);
    [Fact] public void Display_BLOCK_resolves_case_insensitive() => AssertResolves("BLOCK", PropertyId.Display);
    [Fact] public void Display_inline_block_resolves()    => AssertResolves("inline-block", PropertyId.Display);
    [Fact] public void Display_flex_resolves()            => AssertResolves("flex", PropertyId.Display);
    [Fact] public void Display_grid_resolves()            => AssertResolves("grid", PropertyId.Display);
    [Fact] public void Display_none_resolves()            => AssertResolves("none", PropertyId.Display);
    [Fact] public void Position_static_resolves()         => AssertResolves("static", PropertyId.Position);
    [Fact] public void Position_absolute_resolves()       => AssertResolves("absolute", PropertyId.Position);
    [Fact] public void Position_sticky_resolves()         => AssertResolves("sticky", PropertyId.Position);
    [Fact] public void BoxSizing_content_box_resolves()   => AssertResolves("content-box", PropertyId.BoxSizing);
    [Fact] public void BoxSizing_border_box_resolves()    => AssertResolves("border-box", PropertyId.BoxSizing);
    [Fact] public void TextAlign_center_resolves()        => AssertResolves("center", PropertyId.TextAlign);
    [Fact] public void TextAlign_justify_resolves()       => AssertResolves("justify", PropertyId.TextAlign);
    [Fact] public void FlexDirection_row_reverse_resolves() => AssertResolves("row-reverse", PropertyId.FlexDirection);
    [Fact] public void BorderTopStyle_solid_resolves()    => AssertResolves("solid", PropertyId.BorderTopStyle);
    [Fact] public void BorderTopStyle_dotted_resolves()   => AssertResolves("dotted", PropertyId.BorderTopStyle);
    // object-fit (object-fit cycle + PR #168 review P2 — the registered keyword property).
    [Fact] public void ObjectFit_fill_resolves()          => AssertResolves("fill", PropertyId.ObjectFit);
    [Fact] public void ObjectFit_contain_resolves()       => AssertResolves("contain", PropertyId.ObjectFit);
    [Fact] public void ObjectFit_cover_resolves()         => AssertResolves("cover", PropertyId.ObjectFit);
    [Fact] public void ObjectFit_none_resolves()          => AssertResolves("none", PropertyId.ObjectFit);
    [Fact] public void ObjectFit_scale_down_resolves()    => AssertResolves("scale-down", PropertyId.ObjectFit);
    [Fact] public void ObjectFit_bogus_emits_diagnostic() => AssertInvalid("bogus", PropertyId.ObjectFit);

    // background-origin / background-clip (bg-origin / bg-clip cycles + PR #170 review P2 — the
    // registered keyword properties: border-box / padding-box / content-box).
    [Fact] public void BackgroundOrigin_padding_box_resolves() => AssertResolves("padding-box", PropertyId.BackgroundOrigin);
    [Fact] public void BackgroundOrigin_content_box_resolves() => AssertResolves("content-box", PropertyId.BackgroundOrigin);
    [Fact] public void BackgroundOrigin_bogus_emits_diagnostic() => AssertInvalid("bogus", PropertyId.BackgroundOrigin);
    [Fact] public void BackgroundClip_border_box_resolves()    => AssertResolves("border-box", PropertyId.BackgroundClip);
    [Fact] public void BackgroundClip_content_box_resolves()   => AssertResolves("content-box", PropertyId.BackgroundClip);
    [Fact] public void BackgroundClip_bogus_emits_diagnostic() => AssertInvalid("bogus", PropertyId.BackgroundClip);

    // background-attachment (bg-attachment cycle — registered keyword: scroll / fixed / local).
    [Fact] public void BackgroundAttachment_scroll_resolves() => AssertResolves("scroll", PropertyId.BackgroundAttachment);
    [Fact] public void BackgroundAttachment_fixed_resolves()  => AssertResolves("fixed", PropertyId.BackgroundAttachment);
    [Fact] public void BackgroundAttachment_local_resolves()  => AssertResolves("local", PropertyId.BackgroundAttachment);
    [Fact] public void BackgroundAttachment_bogus_emits_diagnostic() => AssertInvalid("bogus", PropertyId.BackgroundAttachment);

    [Fact] public void Display_foo_emits_diagnostic()         => AssertInvalid("foo", PropertyId.Display);
    [Fact] public void Display_block_flex_emits_diagnostic()  => AssertInvalid("block-flex", PropertyId.Display);
    [Fact] public void Position_stickys_emits_diagnostic()    => AssertInvalid("stickys", PropertyId.Position);
    [Fact] public void BoxSizing_padding_box_emits_diagnostic() => AssertInvalid("padding-box", PropertyId.BoxSizing);

    // Phase 3 Task 10 cycle 2 — CSS Text L3 §5+§6 keyword tables.
    [Fact] public void OverflowWrap_normal_resolves()     => AssertResolves("normal", PropertyId.OverflowWrap);
    [Fact] public void OverflowWrap_anywhere_resolves()   => AssertResolves("anywhere", PropertyId.OverflowWrap);
    [Fact] public void OverflowWrap_break_word_resolves() => AssertResolves("break-word", PropertyId.OverflowWrap);
    [Fact] public void OverflowWrap_invalid_emits_diagnostic() => AssertInvalid("foo", PropertyId.OverflowWrap);
    [Fact] public void WordBreak_normal_resolves()        => AssertResolves("normal", PropertyId.WordBreak);
    [Fact] public void WordBreak_break_all_resolves()     => AssertResolves("break-all", PropertyId.WordBreak);
    [Fact] public void WordBreak_keep_all_resolves()      => AssertResolves("keep-all", PropertyId.WordBreak);
    [Fact] public void WordBreak_break_word_resolves()    => AssertResolves("break-word", PropertyId.WordBreak);
    [Fact] public void WordBreak_invalid_emits_diagnostic() => AssertInvalid("bogus", PropertyId.WordBreak);
    [Fact] public void Hyphens_none_resolves()            => AssertResolves("none", PropertyId.Hyphens);
    [Fact] public void Hyphens_manual_resolves()          => AssertResolves("manual", PropertyId.Hyphens);
    [Fact] public void Hyphens_auto_resolves()            => AssertResolves("auto", PropertyId.Hyphens);
    [Fact] public void Hyphens_invalid_emits_diagnostic() => AssertInvalid("always", PropertyId.Hyphens);
    [Fact] public void Hyphens_AUTO_resolves_case_insensitive() => AssertResolves("AUTO", PropertyId.Hyphens);

    [Fact]
    public void TryGetId_returns_dense_zero_based_ids()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.BoxSizing, "content-box", out var contentBoxId));
        Assert.Equal(0, contentBoxId);
        Assert.True(KeywordResolver.TryGetId(PropertyId.BoxSizing, "border-box", out var borderBoxId));
        Assert.Equal(1, borderBoxId);
    }

    // Per Phase 3 Task 10 cycle 2 review (User #3): pin EXACT keyword
    // ids for the new tables. The ids are part of the cascade →
    // materializer contract; reordering would silently break any
    // downstream switch. Adding new keywords appends; never reorders.

    [Fact]
    public void OverflowWrap_keyword_ids_are_pinned()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.OverflowWrap, "normal", out var id0));
        Assert.Equal(0, id0);
        Assert.True(KeywordResolver.TryGetId(PropertyId.OverflowWrap, "anywhere", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.OverflowWrap, "break-word", out var id2));
        Assert.Equal(2, id2);
    }

    [Fact]
    public void WordBreak_keyword_ids_are_pinned()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "normal", out var id0));
        Assert.Equal(0, id0);
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "break-all", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "keep-all", out var id2));
        Assert.Equal(2, id2);
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "break-word", out var id3));
        Assert.Equal(3, id3);
    }

    [Fact]
    public void Hyphens_keyword_ids_are_pinned()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.Hyphens, "none", out var id0));
        Assert.Equal(0, id0);
        Assert.True(KeywordResolver.TryGetId(PropertyId.Hyphens, "manual", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.Hyphens, "auto", out var id2));
        Assert.Equal(2, id2);
    }

    // Phase 3 Task 15 L7 post-PR-#67 review hardening F#3 — pin EXACT
    // keyword ids for the 29-entry align-content table. The ids are
    // part of the parser → cascade → ReadAlignContent contract;
    // reordering would silently break the reader's switch. Pins all
    // four families: 0=normal, 1-4=<content-distribution>,
    // 5-7=<baseline-position> (Phase 3 Task 15 L7 post-PR-#67 F#6),
    // 8-14=<content-position>, 15-21=safe X, 22-28=unsafe X.
    [Fact]
    public void AlignContent_keyword_ids_are_pinned()
    {
        // 0 = normal.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "normal", out var id0));
        Assert.Equal(0, id0);
        // 1-4 = <content-distribution>.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "space-between", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "space-around", out var id2));
        Assert.Equal(2, id2);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "space-evenly", out var id3));
        Assert.Equal(3, id3);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "stretch", out var id4));
        Assert.Equal(4, id4);
        // 5-7 = <baseline-position> (Phase 3 Task 15 L7 post-PR-#67 F#6).
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "baseline", out var id5));
        Assert.Equal(5, id5);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "first baseline", out var id6));
        Assert.Equal(6, id6);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "last baseline", out var id7));
        Assert.Equal(7, id7);
        // 8-14 = <content-position>.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "center", out var id8));
        Assert.Equal(8, id8);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "start", out var id9));
        Assert.Equal(9, id9);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "end", out var id10));
        Assert.Equal(10, id10);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "flex-start", out var id11));
        Assert.Equal(11, id11);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "flex-end", out var id12));
        Assert.Equal(12, id12);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "left", out var id13));
        Assert.Equal(13, id13);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "right", out var id14));
        Assert.Equal(14, id14);
        // 15-21 = safe <content-position>.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe center", out var id15));
        Assert.Equal(15, id15);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe start", out var id16));
        Assert.Equal(16, id16);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe end", out var id17));
        Assert.Equal(17, id17);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe flex-start", out var id18));
        Assert.Equal(18, id18);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe flex-end", out var id19));
        Assert.Equal(19, id19);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe left", out var id20));
        Assert.Equal(20, id20);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "safe right", out var id21));
        Assert.Equal(21, id21);
        // 22-28 = unsafe <content-position>.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe center", out var id22));
        Assert.Equal(22, id22);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe start", out var id23));
        Assert.Equal(23, id23);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe end", out var id24));
        Assert.Equal(24, id24);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe flex-start", out var id25));
        Assert.Equal(25, id25);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe flex-end", out var id26));
        Assert.Equal(26, id26);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe left", out var id27));
        Assert.Equal(27, id27);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignContent, "unsafe right", out var id28));
        Assert.Equal(28, id28);
    }

    // Per Phase 3 Task 15 L9 post-PR-#69 review hardening F#2 — pin
    // EXACT keyword ids for the 28-entry align-self table. The ids
    // are part of the parser → cascade → ReadAlignSelf contract;
    // reordering would silently break the reader's decoder (which
    // delegates to the shared <self-position> grid via a -1 shift).
    // Pins all four families: 0=auto, 1-6=bare alignment values
    // (normal / stretch / anchor-center / baseline / first baseline /
    // last baseline), 7-13=<self-position>, 14-20=safe X, 21-27=unsafe X.
    [Fact]
    public void AlignSelf_keyword_ids_are_pinned()
    {
        // 0 = auto (defers to container's align-items per §4.3).
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "auto", out var id0));
        Assert.Equal(0, id0);
        // 1-3 = bare alignment values (normal / stretch / anchor-center).
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "normal", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "stretch", out var id2));
        Assert.Equal(2, id2);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "anchor-center", out var id3));
        Assert.Equal(3, id3);
        // 4-6 = <baseline-position> triple.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "baseline", out var id4));
        Assert.Equal(4, id4);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "first baseline", out var id5));
        Assert.Equal(5, id5);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "last baseline", out var id6));
        Assert.Equal(6, id6);
        // 7-13 = <self-position> (center, start, end, self-start,
        // self-end, flex-start, flex-end) per the SelfPositions array
        // ordering in KeywordResolver.cs.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "center", out var id7));
        Assert.Equal(7, id7);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "start", out var id8));
        Assert.Equal(8, id8);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "end", out var id9));
        Assert.Equal(9, id9);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "self-start", out var id10));
        Assert.Equal(10, id10);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "self-end", out var id11));
        Assert.Equal(11, id11);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "flex-start", out var id12));
        Assert.Equal(12, id12);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "flex-end", out var id13));
        Assert.Equal(13, id13);
        // 14-20 = safe <self-position>.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe center", out var id14));
        Assert.Equal(14, id14);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe start", out var id15));
        Assert.Equal(15, id15);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe end", out var id16));
        Assert.Equal(16, id16);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe self-start", out var id17));
        Assert.Equal(17, id17);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe self-end", out var id18));
        Assert.Equal(18, id18);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe flex-start", out var id19));
        Assert.Equal(19, id19);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe flex-end", out var id20));
        Assert.Equal(20, id20);
        // 21-27 = unsafe <self-position>.
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe center", out var id21));
        Assert.Equal(21, id21);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe start", out var id22));
        Assert.Equal(22, id22);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe end", out var id23));
        Assert.Equal(23, id23);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe self-start", out var id24));
        Assert.Equal(24, id24);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe self-end", out var id25));
        Assert.Equal(25, id25);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe flex-start", out var id26));
        Assert.Equal(26, id26);
        Assert.True(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe flex-end", out var id27));
        Assert.Equal(27, id27);
    }

    // Per Phase 3 Task 15 L9 post-PR-#69 review hardening F#2 — align-self
    // uses <self-position> grammar (CSS Box Alignment §6.2), NOT
    // <content-position>. The directional keywords `left` / `right`
    // belong to <content-position> only (admitted by justify-content +
    // align-content). align-self MUST reject them; pre-PR-#69 the
    // negative case was untested.
    [Fact]
    public void AlignSelf_rejects_left_and_right_keywords()
    {
        Assert.False(KeywordResolver.TryGetId(PropertyId.AlignSelf, "left", out _));
        Assert.False(KeywordResolver.TryGetId(PropertyId.AlignSelf, "right", out _));
        Assert.False(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe left", out _));
        Assert.False(KeywordResolver.TryGetId(PropertyId.AlignSelf, "safe right", out _));
        Assert.False(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe left", out _));
        Assert.False(KeywordResolver.TryGetId(PropertyId.AlignSelf, "unsafe right", out _));
    }

    [Fact]
    public void Property_with_no_table_returns_UnsupportedUnvalidated_with_raw_text()
    {
        // Per the hardening review: a Keyword-typed property with no table
        // registered yet should be UnsupportedUnvalidated (not Deferred — that
        // implies "validated"). FontWeight isn't a Keyword PropertyType so its
        // table isn't registered.
        var sink = new CapturingSink();
        var result = KeywordResolver.Resolve("bold", PropertyId.FontWeight, "font-weight", sink, default);
        Assert.True(result.IsUnsupportedUnvalidated);
        Assert.False(result.IsDeferred);
        Assert.Equal("bold", result.RawText);
        Assert.Empty(sink.Diagnostics);
    }

    // ============================================================
    // Phase 3 Task 18 cycle 7d + post-PR-#108 review P3 — grid-auto-flow
    // grammar coverage. Per CSS Grid L1 §7.7 the grammar is
    //   grid-auto-flow = [ row | column ] || dense
    // The `||` operator allows any order + any subset of one component
    // from each alternative group. This means seven valid authored forms
    // (row, column, dense, row dense, column dense, dense row,
    // dense column) collapse to four canonical IDs (0..3). Invalid
    // forms — repeating a component (`row row`, `dense dense`) or mixing
    // two from the same group (`row column`) — must fall through to the
    // resolver's invalid-keyword path.
    // ============================================================

    [Fact]
    public void GridAutoFlow_row_resolves_to_id_0()
        => AssertResolves("row", PropertyId.GridAutoFlow);
    [Fact]
    public void GridAutoFlow_column_resolves_to_id_1()
        => AssertResolves("column", PropertyId.GridAutoFlow);
    [Fact]
    public void GridAutoFlow_dense_resolves_to_id_2()
        => AssertResolves("dense", PropertyId.GridAutoFlow);
    [Fact]
    public void GridAutoFlow_row_dense_resolves_to_id_2()
        => AssertResolves("row dense", PropertyId.GridAutoFlow);
    [Fact]
    public void GridAutoFlow_dense_row_resolves_to_id_2()
        => AssertResolves("dense row", PropertyId.GridAutoFlow);
    [Fact]
    public void GridAutoFlow_column_dense_resolves_to_id_3()
        => AssertResolves("column dense", PropertyId.GridAutoFlow);
    [Fact]
    public void GridAutoFlow_dense_column_resolves_to_id_3()
        => AssertResolves("dense column", PropertyId.GridAutoFlow);

    [Fact]
    public void GridAutoFlow_keyword_ids_are_pinned()
    {
        // Per Phase 3 Task 10 cycle-2 review pattern — pin the IDs
        // because they're part of the cascade → materializer contract.
        // GridReaders.ReadGridAutoFlow switches on these IDs; reordering
        // would silently break the layout.
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "row", out var idRow));
        Assert.Equal(0, idRow);
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "column", out var idCol));
        Assert.Equal(1, idCol);
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "dense", out var idDense));
        Assert.Equal(2, idDense);
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "row dense", out var idRowDense));
        Assert.Equal(2, idRowDense);
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "dense row", out var idDenseRow));
        Assert.Equal(2, idDenseRow);
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "column dense", out var idColDense));
        Assert.Equal(3, idColDense);
        Assert.True(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, "dense column", out var idDenseCol));
        Assert.Equal(3, idDenseCol);
    }

    [Fact]
    public void GridAutoFlow_ROW_DENSE_case_insensitive_resolves()
    {
        // The resolver lowercases input before lookup, so authored
        // case variations all canonicalize.
        AssertResolves("ROW DENSE", PropertyId.GridAutoFlow);
        AssertResolves("Row Dense", PropertyId.GridAutoFlow);
        AssertResolves("DENSE", PropertyId.GridAutoFlow);
        AssertResolves("Column Dense", PropertyId.GridAutoFlow);
    }

    [Fact]
    public void GridAutoFlow_compact_whitespace_normalized()
    {
        // Authors may write any amount of whitespace between the parts
        // per the resolver's NormalizeKeywordWhitespace contract.
        AssertResolves("row  dense", PropertyId.GridAutoFlow);
        AssertResolves("\trow dense\t", PropertyId.GridAutoFlow);
        AssertResolves("dense\trow", PropertyId.GridAutoFlow);
    }

    [Fact]
    public void GridAutoFlow_row_column_combination_is_invalid()
    {
        // `row column` mixes two values from the same group — per §7.7
        // grammar `[ row | column ]` is a single alternative.
        AssertInvalid("row column", PropertyId.GridAutoFlow);
        AssertInvalid("column row", PropertyId.GridAutoFlow);
    }

    [Fact]
    public void GridAutoFlow_repeated_keywords_invalid()
    {
        AssertInvalid("dense dense", PropertyId.GridAutoFlow);
        AssertInvalid("row row", PropertyId.GridAutoFlow);
        AssertInvalid("column column", PropertyId.GridAutoFlow);
    }

    [Fact]
    public void GridAutoFlow_unknown_keyword_invalid()
    {
        AssertInvalid("sparse", PropertyId.GridAutoFlow);
        AssertInvalid("inline", PropertyId.GridAutoFlow);
        AssertInvalid("block", PropertyId.GridAutoFlow);
        // Empty / whitespace-only should also fall through (no table entry).
        AssertInvalid("", PropertyId.GridAutoFlow);
    }

    [Fact]
    public void GridAutoFlow_three_keyword_combinations_invalid()
    {
        // Per §7.7 grammar there is no third component — the spec
        // expressly limits `||` to one from each group, and `dense` is
        // the only modifier. `row dense column` etc. must fail.
        AssertInvalid("row dense column", PropertyId.GridAutoFlow);
        AssertInvalid("dense row column", PropertyId.GridAutoFlow);
        AssertInvalid("column dense row", PropertyId.GridAutoFlow);
    }
}
