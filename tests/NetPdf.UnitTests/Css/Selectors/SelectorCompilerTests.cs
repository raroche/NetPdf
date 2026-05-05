// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Per-selector-form unit tests for <see cref="SelectorCompiler"/>. Each test fixes one
/// input shape and verifies the emitted bytecode's structure (opcode list, symbols,
/// specificity, required-token sets). The matcher's behavioral tests live in
/// <see cref="SelectorMatcherTests"/>.
/// </summary>
public sealed class SelectorCompilerTests
{
    [Fact]
    public void Empty_input_returns_empty_list()
    {
        var list = SelectorCompiler.Compile("");
        Assert.Empty(list.Alternatives);
    }

    [Fact]
    public void Whitespace_only_input_returns_empty_list()
    {
        var list = SelectorCompiler.Compile("  \t\n  ");
        Assert.Empty(list.Alternatives);
    }

    [Fact]
    public void Type_selector_compiles()
    {
        var list = SelectorCompiler.Compile("div");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 1), alt.Specificity);
        // FrozenSet<string> implements both ISet<T> and IReadOnlySet<T>; the cast
        // disambiguates Xunit's overloads.
        Assert.Contains("div", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Empty(alt.RequiredClasses);
        Assert.Empty(alt.RequiredIds);
    }

    [Fact]
    public void Universal_selector_compiles()
    {
        var list = SelectorCompiler.Compile("*");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(Specificity.Zero, alt.Specificity);
        Assert.Empty(alt.RequiredTags);
    }

    [Fact]
    public void Class_selector_compiles()
    {
        var list = SelectorCompiler.Compile(".foo");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
        Assert.Contains("foo", (IReadOnlyCollection<string>)alt.RequiredClasses);
    }

    [Fact]
    public void Id_selector_compiles()
    {
        var list = SelectorCompiler.Compile("#bar");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(1, 0, 0), alt.Specificity);
        Assert.Contains("bar", (IReadOnlyCollection<string>)alt.RequiredIds);
    }

    [Fact]
    public void Compound_selector_specificity_sums()
    {
        // div.foo#bar has 1 id + 1 class + 1 type — (1, 1, 1).
        var list = SelectorCompiler.Compile("div.foo#bar");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(1, 1, 1), alt.Specificity);
    }

    [Fact]
    public void Descendant_combinator_compiles()
    {
        // "div p" — both required, descendant combinator between them.
        var list = SelectorCompiler.Compile("div p");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 2), alt.Specificity);
        Assert.Contains("div", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
    }

    [Fact]
    public void Child_combinator_compiles()
    {
        var list = SelectorCompiler.Compile("div > p");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 2), alt.Specificity);
    }

    [Fact]
    public void Adjacent_sibling_combinator_compiles()
    {
        var list = SelectorCompiler.Compile("h1 + p");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 2), alt.Specificity);
    }

    [Fact]
    public void General_sibling_combinator_compiles()
    {
        var list = SelectorCompiler.Compile("h1 ~ p");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 2), alt.Specificity);
    }

    [Fact]
    public void Selector_list_alternatives_compile_independently()
    {
        var list = SelectorCompiler.Compile(".a, #b, p");
        Assert.Equal(3, list.Alternatives.Length);
        Assert.Equal(new Specificity(0, 1, 0), list.Alternatives[0].Specificity);
        Assert.Equal(new Specificity(1, 0, 0), list.Alternatives[1].Specificity);
        Assert.Equal(new Specificity(0, 0, 1), list.Alternatives[2].Specificity);
    }

    [Fact]
    public void Attribute_exists_compiles()
    {
        var list = SelectorCompiler.Compile("[disabled]");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
    }

    [Theory]
    [InlineData("[type=\"text\"]")]
    [InlineData("[type='text']")]
    [InlineData("[type=text]")]
    public void Attribute_equals_supports_quoted_and_unquoted_value(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
    }

    [Theory]
    [InlineData("[class~=\"foo\"]")]
    [InlineData("[class|=\"foo\"]")]
    [InlineData("[class^=\"foo\"]")]
    [InlineData("[class$=\"foo\"]")]
    [InlineData("[class*=\"foo\"]")]
    public void Attribute_operators_all_compile(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
    }

    [Fact]
    public void Attribute_case_insensitive_flag_is_skipped()
    {
        // [type="text" i] should parse without error; the 'i' flag is silently ignored
        // (case-insensitive matching not supported in v1).
        var list = SelectorCompiler.Compile("[type=\"text\" i]");
        Assert.Single(list.Alternatives);
    }

    [Theory]
    [InlineData(":first-child")]
    [InlineData(":last-child")]
    [InlineData(":only-child")]
    [InlineData(":first-of-type")]
    [InlineData(":last-of-type")]
    [InlineData(":only-of-type")]
    [InlineData(":empty")]
    [InlineData(":root")]
    public void Structural_pseudo_classes_compile(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
    }

    [Theory]
    [InlineData(":hover")]
    [InlineData(":focus")]
    [InlineData(":active")]
    [InlineData(":visited")]
    [InlineData(":link")]
    [InlineData(":any-link")]
    public void Dynamic_state_pseudo_classes_compile(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
    }

    [Theory]
    [InlineData(":nth-child(2n+1)")]
    [InlineData(":nth-child(odd)")]
    [InlineData(":nth-child(even)")]
    [InlineData(":nth-child(3)")]
    [InlineData(":nth-child(-n+3)")]
    [InlineData(":nth-child(n)")]
    [InlineData(":nth-last-child(2)")]
    [InlineData(":nth-of-type(2n)")]
    [InlineData(":nth-last-of-type(odd)")]
    public void Nth_pseudo_classes_compile(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 1, 0), alt.Specificity);
    }

    [Fact]
    public void Not_specificity_uses_max_argument()
    {
        // :not(#id) → A=1, plus the key compound contributes 1 to C → (1, 0, 1).
        var list = SelectorCompiler.Compile("p:not(#id)");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(1, 0, 1), alt.Specificity);
    }

    [Fact]
    public void Is_specificity_uses_max_argument()
    {
        // :is(.a, #b) → max(B=1, A=1) = (1, 0, 0). Plus type p → (1, 0, 1).
        var list = SelectorCompiler.Compile("p:is(.a, #b)");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(1, 0, 1), alt.Specificity);
    }

    [Fact]
    public void Where_specificity_is_zero()
    {
        // :where(.a, #b) contributes ZERO. Type p contributes (0, 0, 1).
        var list = SelectorCompiler.Compile("p:where(.a, #b)");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 1), alt.Specificity);
    }

    [Fact]
    public void Has_specificity_uses_max_argument_and_flags_containment()
    {
        var list = SelectorCompiler.Compile("article:has(h1)");
        var alt = Assert.Single(list.Alternatives);
        Assert.True(alt.ContainsHas);
        Assert.True(list.ContainsHas);
        // article (C=1) + :has(h1)'s max specificity (C=1) = (0, 0, 2).
        Assert.Equal(new Specificity(0, 0, 2), alt.Specificity);
    }

    [Fact]
    public void Pseudo_element_double_colon_parses_and_contributes_specificity()
    {
        // p::before — pseudo-element bumps C by 1. p adds 1 → (0, 0, 2).
        var list = SelectorCompiler.Compile("p::before");
        var alt = Assert.Single(list.Alternatives);
        Assert.Equal(new Specificity(0, 0, 2), alt.Specificity);
    }

    [Fact]
    public void Required_tokens_exclude_pseudo_subgroup_arguments()
    {
        // :is(.foo) MUST NOT add 'foo' to RequiredClasses — the bloom-filter pre-filter
        // would over-reject. Same for :not, :where, :has.
        var list = SelectorCompiler.Compile("p:is(.foo)");
        var alt = Assert.Single(list.Alternatives);
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.DoesNotContain("foo", (IReadOnlyCollection<string>)alt.RequiredClasses);
    }

    [Theory]
    [InlineData("[")]              // unterminated attribute selector
    [InlineData("[attr")]
    [InlineData("[attr=]")]        // missing value
    [InlineData(":")]              // missing pseudo name
    [InlineData(":nth-child(")]    // unterminated function
    [InlineData(":unknownpseudo")] // unsupported pseudo
    [InlineData(">")]              // bare combinator
    [InlineData("div >")]          // dangling combinator
    public void Malformed_selectors_throw_parse_exception(string input)
    {
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(input));
    }

    [Fact]
    public void Source_text_is_preserved_per_alternative()
    {
        var list = SelectorCompiler.Compile(".a, .b > .c");
        Assert.Equal(2, list.Alternatives.Length);
        Assert.Equal(".a", list.Alternatives[0].SourceText);
        Assert.Equal(".b > .c", list.Alternatives[1].SourceText);
    }

    [Fact]
    public void Long_chain_compiles()
    {
        // Stress: 6 compound selectors with mixed combinators.
        var list = SelectorCompiler.Compile("body > main #content .row + .col p:first-child");
        var alt = Assert.Single(list.Alternatives);
        // body(C=1) + main(C=1) + #content(A=1) + .row(B=1) + .col(B=1) + p(C=1) + :first-child(B=1)
        // = (1, 3, 3)
        Assert.Equal(new Specificity(1, 3, 3), alt.Specificity);
    }

    [Fact]
    public void Required_tokens_collected_across_chain()
    {
        var list = SelectorCompiler.Compile("body > main .container p");
        var alt = Assert.Single(list.Alternatives);
        Assert.Contains("body", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("main", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("container", (IReadOnlyCollection<string>)alt.RequiredClasses);
    }

    [Fact]
    public void Comma_inside_paren_does_not_split_alternatives()
    {
        // :is(.a, .b) is one alternative even though it contains a comma.
        var list = SelectorCompiler.Compile(":is(.a, .b)");
        Assert.Single(list.Alternatives);
    }

    [Fact]
    public void Selector_list_max_specificity_is_max_across_alternatives()
    {
        var list = SelectorCompiler.Compile(".a, #b");
        Assert.Equal(new Specificity(1, 0, 0), list.MaxSpecificity);
    }
}
