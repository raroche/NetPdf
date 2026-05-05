// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Structural tests for <see cref="SelectorBytecode"/>'s reader and the
/// <see cref="SelectorList"/> aggregate. Verifies that compiled bytecode walks correctly
/// via the public reader API and that aggregate properties (<see cref="SelectorList.MaxSpecificity"/>,
/// <see cref="SelectorList.ContainsHas"/>) reflect their underlying alternatives.
/// </summary>
public sealed class SelectorBytecodeTests
{
    [Fact]
    public void Reader_emits_End_at_terminator()
    {
        var alt = SelectorCompiler.Compile("div").Alternatives[0];
        var reader = alt.OpenReader();
        var first = reader.ReadOpcode();
        Assert.Equal(SelectorOpcode.MatchTag, first);
        var sym = reader.ReadSymbol();
        Assert.Equal("div", sym);
        Assert.Equal(SelectorOpcode.End, reader.ReadOpcode());
        Assert.True(reader.IsEnd);
    }

    [Fact]
    public void Reader_walks_combinator_then_match()
    {
        // "div p" → bytecode is right-to-left:
        //   MatchTag p, Descendant, MatchTag div, End
        var alt = SelectorCompiler.Compile("div p").Alternatives[0];
        var reader = alt.OpenReader();

        Assert.Equal(SelectorOpcode.MatchTag, reader.ReadOpcode());
        Assert.Equal("p", reader.ReadSymbol());
        Assert.Equal(SelectorOpcode.Descendant, reader.ReadOpcode());
        Assert.Equal(SelectorOpcode.MatchTag, reader.ReadOpcode());
        Assert.Equal("div", reader.ReadSymbol());
        Assert.Equal(SelectorOpcode.End, reader.ReadOpcode());
    }

    [Fact]
    public void Reader_walks_attribute_with_two_symbols_and_case_flag()
    {
        // [data-x="text"] → MatchAttrEquals, name=data-x, value=text, caseFlag=0, End.
        // Use data-x (not on the HTML CI list) so the case flag is deterministically 0.
        var alt = SelectorCompiler.Compile("[data-x=\"text\"]").Alternatives[0];
        var reader = alt.OpenReader();
        Assert.Equal(SelectorOpcode.MatchAttrEquals, reader.ReadOpcode());
        Assert.Equal("data-x", reader.ReadSymbol());
        Assert.Equal("text", reader.ReadSymbol());
        Assert.Equal((byte)0, reader.ReadByte());
        Assert.Equal(SelectorOpcode.End, reader.ReadOpcode());
    }

    [Fact]
    public void Reader_walks_attribute_with_html_ci_default_flag()
    {
        // 'type' is on the HTML CI attribute list, so the compiler defaults to caseFlag=1
        // even without an explicit `i` flag.
        var alt = SelectorCompiler.Compile("[type=\"text\"]").Alternatives[0];
        var reader = alt.OpenReader();
        Assert.Equal(SelectorOpcode.MatchAttrEquals, reader.ReadOpcode());
        Assert.Equal("type", reader.ReadSymbol());
        Assert.Equal("text", reader.ReadSymbol());
        Assert.Equal((byte)1, reader.ReadByte());
        Assert.Equal(SelectorOpcode.End, reader.ReadOpcode());
    }

    [Fact]
    public void Reader_walks_nth_with_int_operands()
    {
        // :nth-child(2n+1) → MatchNthChild, a=2, b=1, End
        var alt = SelectorCompiler.Compile(":nth-child(2n+1)").Alternatives[0];
        var reader = alt.OpenReader();
        Assert.Equal(SelectorOpcode.MatchNthChild, reader.ReadOpcode());
        Assert.Equal(2, reader.ReadInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(SelectorOpcode.End, reader.ReadOpcode());
    }

    [Fact]
    public void Reader_walks_subgroup_index()
    {
        var alt = SelectorCompiler.Compile("p:not(.foo)").Alternatives[0];
        var reader = alt.OpenReader();
        Assert.Equal(SelectorOpcode.MatchTag, reader.ReadOpcode());
        Assert.Equal("p", reader.ReadSymbol());
        Assert.Equal(SelectorOpcode.MatchNot, reader.ReadOpcode());
        var sub = reader.ReadSubGroup();
        Assert.Single(sub.SubGroups);
        Assert.Equal(SelectorOpcode.End, reader.ReadOpcode());
    }

    [Fact]
    public void Symbols_are_interned()
    {
        // The same tag name appearing twice should appear only once in the symbols table.
        var alt = SelectorCompiler.Compile("div div div").Alternatives[0];
        // 'div' is the only tag — symbols table should be length 1.
        Assert.Single(alt.Symbols);
        Assert.Equal("div", alt.Symbols[0]);
    }

    [Fact]
    public void List_reports_max_specificity()
    {
        var list = SelectorCompiler.Compile(".foo, #bar, p");
        Assert.Equal(new Specificity(1, 0, 0), list.MaxSpecificity);
    }

    [Fact]
    public void List_reports_ContainsHas_when_any_alternative_uses_has()
    {
        var list = SelectorCompiler.Compile("p, article:has(h1)");
        Assert.True(list.ContainsHas);
        Assert.False(list.Alternatives[0].ContainsHas);
        Assert.True(list.Alternatives[1].ContainsHas);
    }

    [Fact]
    public void Empty_list_aggregates_to_zero_and_no_has()
    {
        var list = SelectorList.Empty;
        Assert.Equal(Specificity.Zero, list.MaxSpecificity);
        Assert.False(list.ContainsHas);
    }
}
