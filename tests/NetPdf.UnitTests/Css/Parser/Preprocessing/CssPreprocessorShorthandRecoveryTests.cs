// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L16 post-PR-#76 review — cascade-interaction
/// regression tests for the shorthand recovery path. Documents the
/// current behavior of <c>CssPreprocessor.ScanDeclarations</c> +
/// <c>CssParserAdapter.AdaptDeclarationsWithRecovery</c> when
/// shorthand expansion (<c>flex</c> / <c>flex-flow</c>) interacts
/// with explicit longhand declarations in the same rule.
/// </summary>
/// <remarks>
/// <para>
/// The pre-PR-#76 implementation overrides AngleSharp's emit for any
/// longhand that has a recovery entry — including
/// shorthand-derived recoveries. The post-PR-#76 review (P1 finding)
/// noted this can break CSS Cascade §7.4 last-decl-wins for cases
/// like <c>flex-flow: row wrap; flex-wrap: nowrap;</c>. The fix
/// would require source-position tracking on both AngleSharp's emit
/// and the recovery records so the merge interleaves them by source
/// order; that's a substantial refactor.
/// </para>
/// <para>
/// These tests pin the current behavior so future work can flip the
/// assertions when the proper fix lands. Tests marked
/// <c>_known_gap_</c> document the LIMITATION; tests without the
/// marker document the WORKING cases (= shorthand alone OR
/// shorthand-then-longhand where AngleSharp also emits the explicit
/// longhand, which happens to work via the existing override path).
/// </para>
/// </remarks>
public sealed class CssPreprocessorShorthandRecoveryTests
{
    [Fact]
    public void Flex_flow_alone_expands_both_longhands()
    {
        // Sanity baseline: shorthand alone produces correct
        // longhands. The recovery emits both flex-direction +
        // flex-wrap; the merge sees AngleSharp's (possibly
        // incorrect) emit + overrides with recovery; net effect =
        // correct (row, wrap).
        var result = CssPreprocessor.Process(".a { flex-flow: row wrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        Assert.Contains(decls,
            d => d.Property == "flex-direction"
                && d.RawValueText == "row"
                && d.IsFromShorthandExpansion);
        Assert.Contains(decls,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap"
                && d.IsFromShorthandExpansion);
    }

    [Fact]
    public void Flex_flow_revert_layer_keyword_passes_through_to_both_longhands()
    {
        // Per post-PR-#76 review P2 — the expander supports
        // `revert-layer` but the expander-unit-test suite was missing
        // it. This test pins it through the preprocessor pipeline.
        var result = CssPreprocessor.Process(".a { flex-flow: revert-layer; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        Assert.Contains(decls,
            d => d.Property == "flex-direction"
                && d.RawValueText == "revert-layer");
        Assert.Contains(decls,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "revert-layer");
    }

    [Fact]
    public void Mixed_flex_flow_then_explicit_flex_wrap_records_explicit_winner()
    {
        // Per CSS Cascade §7.4 last-decl-wins:
        //   `.flex { flex-flow: row wrap; flex-wrap: nowrap; }`
        // should produce flex-direction: row + flex-wrap: nowrap
        // (the LATER explicit longhand wins over the shorthand
        // expansion's earlier flex-wrap: wrap).
        //
        // Per Phase 3 Task 15 L17 the source-order tracking landed:
        // `ScanDeclarations` records the explicit `flex-wrap:
        // nowrap` as an "explicit winner" because it appears AFTER
        // a `flex-flow` shorthand expansion that targets `flex-wrap`.
        // The merge in `CssParserAdapter.AdaptDeclarationsWithRecovery`
        // uses this set to skip the shorthand-expansion override for
        // `flex-wrap`, letting AngleSharp's emit (which respects
        // last-decl-wins for explicit longhands) survive intact.
        var result = CssPreprocessor.Process(
            ".flex { flex-flow: row wrap; flex-wrap: nowrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        // Recovery list still contains BOTH shorthand-expansion
        // longhands (flex-direction + flex-wrap), but the merge will
        // skip the flex-wrap override because the rule's
        // ExplicitLonghandsAfterShorthand set contains it.
        Assert.Contains(decls,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap"
                && d.IsFromShorthandExpansion);
        // Per Phase 3 Task 15 L17 — `flex-wrap` IS in the
        // explicit-winners set for this rule.
        Assert.Contains("flex-wrap", rule.ExplicitLonghandsAfterShorthand);
        // `flex-direction` is NOT in the set (no later explicit
        // longhand for it).
        Assert.DoesNotContain("flex-direction", rule.ExplicitLonghandsAfterShorthand);
    }

    [Fact]
    public void Mixed_explicit_flex_wrap_then_flex_flow_pins_recovery_state()
    {
        // Per CSS Cascade §7.4 last-decl-wins:
        //   `.flex { flex-wrap: nowrap; flex-flow: row wrap; }`
        // should produce flex-direction: row + flex-wrap: wrap
        // (the shorthand expansion wins because it came LATER).
        //
        // This case happens to work correctly under the current
        // override path: AngleSharp's emit for flex-wrap (whatever
        // it ends up being) gets overridden by the recovery's
        // expansion-derived `wrap` value — which is the spec-correct
        // outcome. The override "bug" doesn't surface here because
        // recovery's value MATCHES what last-decl-wins would produce.
        var result = CssPreprocessor.Process(
            ".flex { flex-wrap: nowrap; flex-flow: row wrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        // Recovery captures both the explicit-longhand `flex-wrap:
        // nowrap` (NOT marked IsFromShorthandExpansion — it's an
        // explicit longhand from a known-dropped property) AND the
        // shorthand expansion's `flex-wrap: wrap`. The merge's
        // FindRecovery returns the LAST one in source order = `wrap`
        // (from flex-flow which came later).
        var flexWrapRecoveries = decls
            .Where(d => d.Property == "flex-wrap")
            .ToList();
        // Pin: 1 entry from explicit `flex-wrap` is NOT in recovery
        // (AngleSharp handles it natively; only `flex-flow` is in
        // KnownDroppedProperties). The expansion's `wrap` is in
        // recovery, IsFromShorthandExpansion = true.
        Assert.Single(flexWrapRecoveries);
        Assert.Equal("wrap", flexWrapRecoveries[0].RawValueText);
        Assert.True(flexWrapRecoveries[0].IsFromShorthandExpansion);
    }

    [Fact]
    public void Flex_shorthand_then_explicit_flex_grow_records_explicit_winner()
    {
        // Per CSS Cascade §7.4: `.a { flex: 1; flex-grow: 0; }`
        // should produce flex-grow: 0 (the LATER explicit longhand
        // wins). Per Phase 3 Task 15 L17 source-order tracking the
        // explicit `flex-grow: 0` is recorded as an explicit winner
        // (= it appears AFTER the `flex` shorthand expansion's
        // `flex-grow: 1` target), so the merge skips the override
        // and AngleSharp's emit survives.
        var result = CssPreprocessor.Process(
            ".a { flex: 1; flex-grow: 0; }");
        var rule = result.StyleRuleRecoveries.Single();
        // Recovery list contains all three shorthand expansions.
        Assert.Contains(rule.Declarations,
            d => d.Property == "flex-grow"
                && d.RawValueText == "1"
                && d.IsFromShorthandExpansion);
        // Per Phase 3 Task 15 L17 — `flex-grow` is in the
        // explicit-winners set (= later explicit longhand wins).
        Assert.Contains("flex-grow", rule.ExplicitLonghandsAfterShorthand);
        // The other two longhands (flex-shrink, flex-basis) are
        // NOT in the explicit-winners set (no later explicit
        // declaration overrode them).
        Assert.DoesNotContain("flex-shrink", rule.ExplicitLonghandsAfterShorthand);
        Assert.DoesNotContain("flex-basis", rule.ExplicitLonghandsAfterShorthand);
    }

    [Fact]
    public void Important_modifier_propagates_to_all_expanded_longhands()
    {
        // Per CSS Cascade §6.5 + the `important` flag: the modifier
        // applies to all longhands the shorthand expands to.
        var result = CssPreprocessor.Process(
            ".a { flex-flow: row wrap !important; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        Assert.Contains(decls,
            d => d.Property == "flex-direction"
                && d.RawValueText == "row"
                && d.IsImportant);
        Assert.Contains(decls,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap"
                && d.IsImportant);
    }

    [Fact]
    public void Inline_style_attribute_path_picks_up_flex_flow_recovery()
    {
        // Per post-PR-#76 review P2 — inline styles
        // (`style="..."` attributes) go through the same recovery
        // path. ScanForModernDeclarations is called directly on
        // inline style text by CssParserAdapter.AdaptInlineStyleWithRecovery
        // (see CssParserAdapter.cs line ~159).
        var inlineStyleText = "display: flex; flex-flow: column-reverse wrap-reverse";
        var recoveries = CssPreprocessor.ScanForModernDeclarations(inlineStyleText);
        Assert.Contains(recoveries,
            d => d.Property == "flex-direction"
                && d.RawValueText == "column-reverse"
                && d.IsFromShorthandExpansion);
        Assert.Contains(recoveries,
            d => d.Property == "flex-wrap"
                && d.RawValueText == "wrap-reverse"
                && d.IsFromShorthandExpansion);
    }

    [Fact]
    public void Comments_inside_flex_flow_value_are_stripped()
    {
        // Per Phase 3 Task 15 L17 (post-PR-#76 P2 closeout) — CSS
        // block comments (`/* ... */`) per CSS Syntax §4.3.2 are
        // syntactic whitespace + must not affect the value's
        // tokenization. The expander pre-normalizes comments to
        // single spaces before splitting via
        // `CssShorthandHelpers.StripBlockComments`.
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
        // Same as above, for the `flex` shorthand expander.
        var raw = "1 /* grow */ 0 /* shrink */ 100px";
        var ok = FlexShorthandExpander.TryExpand(
            raw, out var g, out var s, out var b);
        Assert.True(ok);
        Assert.Equal("1", g);
        Assert.Equal("0", s);
        Assert.Equal("100px", b);
    }
}
