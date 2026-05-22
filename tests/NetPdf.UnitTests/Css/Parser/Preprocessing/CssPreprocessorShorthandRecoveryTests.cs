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
    public void Mixed_flex_flow_then_explicit_flex_wrap_known_gap_pins_current_behavior()
    {
        // Per CSS Cascade §7.4 last-decl-wins:
        //   `.flex { flex-flow: row wrap; flex-wrap: nowrap; }`
        // should produce flex-direction: row + flex-wrap: nowrap
        // (the LATER explicit longhand wins over the shorthand
        // expansion's earlier flex-wrap: wrap).
        //
        // CURRENT BEHAVIOR (= the post-PR-#76 P1 finding's known
        // gap): the recovery's expansion-derived flex-wrap: wrap
        // appears in the recovery list. AdaptDeclarationsWithRecovery
        // overrides AngleSharp's emit for flex-wrap, regardless of
        // source order. So the merged output is incorrectly
        // flex-wrap: wrap.
        //
        // This test pins the CURRENT incorrect behavior — it should
        // flip when source-position tracking lands. The test exists
        // so the limitation doesn't drift silently.
        var result = CssPreprocessor.Process(
            ".flex { flex-flow: row wrap; flex-wrap: nowrap; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        // The recovery list contains:
        //   - flex-direction: row (from flex-flow shorthand)
        //   - flex-wrap: wrap (from flex-flow shorthand)
        //   - flex-wrap: nowrap (NOT in recovery — AngleSharp
        //     handles the explicit longhand directly + recovery
        //     only picks up shorthands per the KnownDroppedProperties
        //     filter).
        var flexWrapRecoveries = decls
            .Where(d => d.Property == "flex-wrap")
            .ToList();
        // CURRENT BEHAVIOR: only the shorthand-expansion entry is in
        // the recovery list. The explicit `flex-wrap: nowrap` is
        // expected to come through AngleSharp's emit + survive the
        // merge if AngleSharp emits it correctly. If the merge
        // override fires, recovery's `wrap` will win incorrectly.
        Assert.Single(flexWrapRecoveries);
        Assert.Equal("wrap", flexWrapRecoveries[0].RawValueText);
        Assert.True(flexWrapRecoveries[0].IsFromShorthandExpansion);
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
    public void Flex_shorthand_then_explicit_flex_grow_known_gap_pins_current_behavior()
    {
        // Per CSS Cascade §7.4: `.a { flex: 1; flex-grow: 0; }`
        // should produce flex-grow: 0 (the LATER explicit longhand
        // wins). CURRENT BEHAVIOR (= known gap, same root cause as
        // Mixed_flex_flow above): the recovery's expansion-derived
        // `flex-grow: 1` overrides AngleSharp's emit for flex-grow,
        // regardless of source order. So the merged output is
        // incorrectly flex-grow: 1.
        var result = CssPreprocessor.Process(
            ".a { flex: 1; flex-grow: 0; }");
        var rule = result.StyleRuleRecoveries.Single();
        var decls = rule.Declarations;
        var flexGrowRecoveries = decls
            .Where(d => d.Property == "flex-grow")
            .ToList();
        // Pin the broken-by-spec behavior: recovery only carries the
        // expansion's flex-grow: 1 (the explicit `flex-grow: 0` from
        // the author surfaces via AngleSharp's emit, NOT via
        // recovery — `flex-grow` is not in KnownDroppedProperties).
        Assert.Single(flexGrowRecoveries);
        Assert.Equal("1", flexGrowRecoveries[0].RawValueText);
        Assert.True(flexGrowRecoveries[0].IsFromShorthandExpansion);
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
    public void Comments_between_tokens_known_gap_pins_current_behavior()
    {
        // Per post-PR-#76 review P2 — `flex-flow: row /* comment */
        // wrap` is valid CSS. The current
        // FlexFlowShorthandExpander tokenizes via string.Split on
        // whitespace + does NOT strip comments, so the comment
        // remains a token + expansion fails (returns false).
        // CssTokenizer.ReadUntilAnyTopLevel preserves the original
        // span including comments.
        //
        // Pin the CURRENT behavior so future comment-stripping work
        // surfaces this test. (Fix: pre-normalize comments to
        // whitespace before splitting, OR use a CSS-aware token
        // reader instead of string.Split.)
        var raw = "row /* hello */ wrap";
        var ok = FlexFlowShorthandExpander.TryExpand(
            raw, out _, out _);
        // CURRENT: returns false because comment is treated as a
        // bogus 3rd token. This SHOULD return true post-fix.
        Assert.False(ok,
            "Known gap: comments inside flex-flow values are not "
            + "stripped before tokenization; the expansion fails. "
            + "Flip this assertion when the expander gains CSS-aware "
            + "comment skipping (= P2 from PR-#76 review).");
    }
}
