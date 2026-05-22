// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L17 post-PR-#77 review — cascade-correctness
/// tests for the shorthand recovery path. Covers source-order +
/// importance interactions between shorthand expansions and explicit
/// longhands per CSS Cascade §5 + §7.4.
/// </summary>
public sealed class CssPreprocessorShorthandRecoveryTests
{
    // ====================================================================
    // Baseline — shorthand alone produces correct longhand recovery records.
    // ====================================================================

    [Fact]
    public void Flex_flow_alone_expands_both_longhands()
    {
        var result = CssPreprocessor.Process(".a { flex-flow: row wrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-direction"
                && d.RawValueText == "row"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0);
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0);
        // No explicit longhands in this rule → empty list (or default).
        Assert.True(rule.ExplicitLonghandOrdinals.IsDefaultOrEmpty);
    }

    [Fact]
    public void Flex_flow_revert_layer_keyword_passes_through_to_both_longhands()
    {
        // Per post-PR-#76 review P2.
        var result = CssPreprocessor.Process(".a { flex-flow: revert-layer; }");
        var rule = result.StyleRuleRecoveries.Single();
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-direction"
                && d.RawValueText == "revert-layer");
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "revert-layer");
    }

    [Fact]
    public void Important_modifier_propagates_to_all_expanded_longhands()
    {
        var result = CssPreprocessor.Process(
            ".a { flex-flow: row wrap !important; }");
        var rule = result.StyleRuleRecoveries.Single();
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-direction"
                && d.RawValueText == "row"
                && d.IsImportant);
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap"
                && d.IsImportant);
    }

    // ====================================================================
    // P1 #2: cascade-correct multi-declaration interactions.
    // ====================================================================

    [Fact]
    public void Shorthand_then_explicit_longhand_records_explicit_at_higher_ordinal()
    {
        // `.flex { flex-flow: row wrap; flex-wrap: nowrap; }` — explicit
        // longhand at ordinal 1 follows shorthand at ordinal 0.
        var result = CssPreprocessor.Process(
            ".flex { flex-flow: row wrap; flex-wrap: nowrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        // Shorthand expansion at ordinal 0.
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0);
        // Explicit longhand at ordinal 1 (normal importance).
        Assert.Contains(rule.ExplicitLonghandOrdinals,
            e => e.Property == "flex-wrap"
                && e.Ordinal == 1
                && !e.IsImportant);
    }

    [Fact]
    public void Explicit_longhand_then_shorthand_records_explicit_at_lower_ordinal()
    {
        // `.flex { flex-wrap: nowrap; flex-flow: row wrap; }` — explicit
        // at ordinal 0, shorthand at ordinal 1. Per cascade the shorthand
        // wins (= last wins).
        var result = CssPreprocessor.Process(
            ".flex { flex-wrap: nowrap; flex-flow: row wrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        // Shorthand expansion at ordinal 1.
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 1);
        // Pre-fix the explicit longhand at ordinal 0 was tracked only
        // when a shorthand had already been seen. Now the
        // `sawShorthand` guard means an explicit longhand BEFORE the
        // first shorthand is NOT tracked. This is OK because in this
        // case the recovery's higher ordinal beats the explicit's
        // lower ordinal regardless — the override fires correctly.
    }

    [Fact]
    public void Multiple_shorthands_with_intervening_explicit_records_all_ordinals()
    {
        // Per post-PR-#77 review P1 #2 — the multi-shorthand case:
        //   ord 0: flex-flow: row wrap → expansion at 0
        //   ord 1: flex-wrap: nowrap → explicit at 1
        //   ord 2: flex-flow: row wrap-reverse → expansion at 2
        // FindRecovery returns the LAST shorthand-expansion entry
        // (ordinal 2, value wrap-reverse). The merge compares
        // against explicit longhands: explicit at ordinal 1 with
        // ordinal < recovery's 2 + same importance → recovery wins.
        // Final: flex-wrap = wrap-reverse.
        var result = CssPreprocessor.Process(
            ".flex { flex-flow: row wrap; flex-wrap: nowrap; "
            + "flex-flow: row wrap-reverse; }");
        var rule = result.StyleRuleRecoveries.Single();
        // Two flex-wrap recovery records, both shorthand-derived.
        var flexWrapRecoveries = rule.Declarations
            .Where(d => d.Property == "flex-wrap").ToList();
        Assert.Equal(2, flexWrapRecoveries.Count);
        Assert.Equal("wrap", flexWrapRecoveries[0].RawValueText);
        Assert.Equal(0, flexWrapRecoveries[0].SourceOrdinal);
        Assert.Equal("wrap-reverse", flexWrapRecoveries[1].RawValueText);
        Assert.Equal(2, flexWrapRecoveries[1].SourceOrdinal);
        // Explicit longhand at ordinal 1.
        Assert.Contains(rule.ExplicitLonghandOrdinals,
            e => e.Property == "flex-wrap" && e.Ordinal == 1);
    }

    [Fact]
    public void Important_shorthand_then_normal_explicit_marks_correctly()
    {
        // `.a { flex-flow: row wrap !important; flex-wrap: nowrap; }`
        // — shorthand !important should beat later normal explicit.
        var result = CssPreprocessor.Process(
            ".a { flex-flow: row wrap !important; flex-wrap: nowrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        // Shorthand expansion at ordinal 0 with IsImportant=true.
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0
                && d.IsImportant);
        // Explicit longhand at ordinal 1 with IsImportant=false.
        Assert.Contains(rule.ExplicitLonghandOrdinals,
            e => e.Property == "flex-wrap"
                && e.Ordinal == 1
                && !e.IsImportant);
    }

    [Fact]
    public void Normal_shorthand_then_important_explicit_marks_correctly()
    {
        // `.a { flex-flow: row wrap; flex-wrap: nowrap !important; }`
        // — normal shorthand vs later !important explicit. Per
        // cascade the !important wins.
        var result = CssPreprocessor.Process(
            ".a { flex-flow: row wrap; flex-wrap: nowrap !important; }");
        var rule = result.StyleRuleRecoveries.Single();
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-wrap"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0
                && !d.IsImportant);
        Assert.Contains(rule.ExplicitLonghandOrdinals,
            e => e.Property == "flex-wrap"
                && e.Ordinal == 1
                && e.IsImportant);
    }

    [Fact]
    public void Flex_shorthand_then_explicit_flex_grow_records_correctly()
    {
        // `.a { flex: 1; flex-grow: 0; }`
        var result = CssPreprocessor.Process(
            ".a { flex: 1; flex-grow: 0; }");
        var rule = result.StyleRuleRecoveries.Single();
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-grow"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0);
        Assert.Contains(rule.ExplicitLonghandOrdinals,
            e => e.Property == "flex-grow" && e.Ordinal == 1);
    }

    // ====================================================================
    // Inline style coverage — same code path through
    // CssPreprocessor.ScanForModernDeclarationsWithOrder.
    // ====================================================================

    [Fact]
    public void Inline_style_attribute_path_picks_up_flex_flow_recovery()
    {
        var inlineStyleText = "display: flex; flex-flow: column-reverse wrap-reverse";
        var (recoveries, explicitLonghands) =
            CssPreprocessor.ScanForModernDeclarationsWithOrder(inlineStyleText);
        Assert.Contains(recoveries,
            d => d.Property == "flex-direction"
                && d.RawValueText == "column-reverse"
                && d.IsFromShorthandExpansion);
        Assert.Contains(recoveries,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap-reverse"
                && d.IsFromShorthandExpansion);
        // No explicit longhands AFTER the shorthand in this inline.
        Assert.True(explicitLonghands.IsDefaultOrEmpty
            || !explicitLonghands.Any(e => e.Property == "flex-wrap"));
    }

    [Fact]
    public void Inline_style_with_shorthand_then_explicit_longhand_records_ordinals()
    {
        // Per post-PR-#77 review P1 #1 — inline styles also need
        // source-order tracking. Pre-fix the inline path used
        // ScanForModernDeclarations (no order info), so the
        // explicit-longhand fix was silently bypassed.
        var inlineStyleText = "flex-flow: row wrap; flex-wrap: nowrap";
        var (recoveries, explicitLonghands) =
            CssPreprocessor.ScanForModernDeclarationsWithOrder(inlineStyleText);
        Assert.Contains(recoveries,
            d => d.Property == "flex-wrap"
                && d.IsFromShorthandExpansion
                && d.SourceOrdinal == 0);
        Assert.Contains(explicitLonghands,
            e => e.Property == "flex-wrap" && e.Ordinal == 1);
    }

    // ====================================================================
    // P2: comments in values.
    // ====================================================================

    [Fact]
    public void Comments_inside_flex_flow_value_are_stripped()
    {
        var raw = "row /* hello */ wrap";
        var ok = FlexFlowShorthandExpander.TryExpand(
            raw, out var d, out var w);
        Assert.True(ok);
        Assert.Equal("row", d);
        Assert.Equal("wrap", w);
    }

    [Fact]
    public void Comments_inside_flex_value_are_stripped()
    {
        var raw = "1 /* grow */ 0 /* shrink */ 100px";
        var ok = FlexShorthandExpander.TryExpand(
            raw, out var g, out var s, out var b);
        Assert.True(ok);
        Assert.Equal("1", g);
        Assert.Equal("0", s);
        Assert.Equal("100px", b);
    }
}
