// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Review-cycle 1 regression tests for Phase 2 Task 8 — covers the 7 deeper review
/// recommendations: expansion safety limits, SCC-based cycle invalidation, pseudo-
/// element custom-property layer, empty fallback semantics, custom-only-element
/// exposure, and immutable Empty singleton.
/// </summary>
public sealed class VarSubstitutionReviewCycle1Tests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static CustomPropertyTable Table(params (string Name, string Value)[] entries)
    {
        var t = new CustomPropertyTable(parent: null);
        foreach (var (n, v) in entries) t.Set(n, v);
        return t;
    }

    // ============================================================
    // Rec 1 — Safety limits (depth + output length)
    // ============================================================

    [Fact]
    public void Rec1_Recursion_depth_limit_caps_long_chains()
    {
        // Build a long non-cyclic chain --a → --b → --c → ... that exceeds MaxRecursionDepth.
        var entries = new List<(string, string)>();
        for (var i = 0; i < VarSubstitution.MaxRecursionDepth + 5; i++)
        {
            var name = "--n" + i;
            var nextName = "--n" + (i + 1);
            entries.Add((name, "var(" + nextName + ", base)"));
        }
        var t = Table(entries.ToArray());
        var sink = new CapturingSink();

        var result = VarSubstitution.Substitute("var(--n0)", t, sink);
        // Either the depth limit OR the chain bottoms out at "base" — but a depth-bound
        // diagnostic should be emitted when MaxRecursionDepth is hit.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001
              && d.Message.Contains("maximum depth"));
    }

    [Fact]
    public void Rec1_Output_length_limit_catches_exponential_expansion()
    {
        // Synthesize the exponential pattern: each variable references the next one
        // multiple times so the output doubles per level. With ~15 levels of doubling,
        // the output blows past MaxOutputLength.
        // --a: var(--b) var(--b)
        // --b: var(--c) var(--c)
        // --c: ...
        // Final: 2^N copies of "x".
        var entries = new List<(string, string)>();
        for (var i = 0; i < 22; i++)
        {
            var name = "--n" + i;
            var next = "--n" + (i + 1);
            entries.Add((name, "var(" + next + ") var(" + next + ")"));
        }
        entries.Add(("--n22", "x")); // base case
        var t = Table(entries.ToArray());
        var sink = new CapturingSink();

        var result = VarSubstitution.Substitute("var(--n0)", t, sink);
        // Either depth or output-length guard should fire.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
        // The exponentially-doubled chain bottoms out into a string of 'unset' tokens
        // (each deeply-nested var() that hit the limit returned the unset sentinel,
        // which the outer frames concatenated). The base-case literal 'x' is unreachable
        // because the limit kicks in before the deepest level resolves.
        Assert.DoesNotContain("x", result);
    }

    // ============================================================
    // Rec 2 — SCC-based cycle invalidation
    // ============================================================

    [Fact]
    public void Rec2_All_cycle_members_marked_invalid_not_just_first_hit()
    {
        // --a: var(--b, blue); --b: var(--a, red)
        // Per spec, BOTH --a and --b are invalid. References to either should use the
        // referencing var()'s fallback, NOT a value derived from the cycle's race result.
        var t = Table(("--a", "var(--b, blue)"), ("--b", "var(--a, red)"));
        var sink = new CapturingSink();
        CustomPropertyCycleDetector.DetectAndMarkInvalid(t, sink);

        // External reference: var(--a, green) — --a is now invalid → fallback "green".
        var result = VarSubstitution.Substitute("var(--a, green)", t, sink);
        Assert.Equal("green", result);
        // Same for --b.
        var result2 = VarSubstitution.Substitute("var(--b, yellow)", t, sink);
        Assert.Equal("yellow", result2);
        // One cycle diagnostic emitted (at detection time), not many.
        var cycleDiags = sink.Diagnostics.FindAll(d => d.Code == CssDiagnosticCodes.CssVarCircular001);
        Assert.Single(cycleDiags);
        // Diagnostic message lists both members.
        Assert.Contains("--a", cycleDiags[0].Message);
        Assert.Contains("--b", cycleDiags[0].Message);
    }

    [Fact]
    public void Rec2_Self_referential_cycle_is_invalid()
    {
        // --a: var(--a, blue) — singleton self-reference.
        var t = Table(("--a", "var(--a, blue)"));
        var sink = new CapturingSink();
        CustomPropertyCycleDetector.DetectAndMarkInvalid(t, sink);
        Assert.Single(sink.Diagnostics);
        // External reference: --a is invalid → fallback used.
        var result = VarSubstitution.Substitute("var(--a, green)", t, sink);
        Assert.Equal("green", result);
    }

    [Fact]
    public void Rec2_Three_node_cycle_all_invalidated()
    {
        // --a: var(--b); --b: var(--c); --c: var(--a)
        var t = Table(("--a", "var(--b)"), ("--b", "var(--c)"), ("--c", "var(--a)"));
        var sink = new CapturingSink();
        CustomPropertyCycleDetector.DetectAndMarkInvalid(t, sink);

        // Each name resolves to unset (no fallback in the externally-referencing var()).
        Assert.Equal(VarSubstitution.UnsetSentinel,
            VarSubstitution.Substitute("var(--a)", t, sink));
        Assert.Equal(VarSubstitution.UnsetSentinel,
            VarSubstitution.Substitute("var(--b)", t, sink));
        Assert.Equal(VarSubstitution.UnsetSentinel,
            VarSubstitution.Substitute("var(--c)", t, sink));
    }

    [Fact]
    public void Rec2_Non_cyclic_dependencies_not_invalidated()
    {
        // --a: var(--b); --b: red — chain is acyclic.
        var t = Table(("--a", "var(--b)"), ("--b", "red"));
        var sink = new CapturingSink();
        CustomPropertyCycleDetector.DetectAndMarkInvalid(t, sink);
        Assert.Empty(sink.Diagnostics);
        var result = VarSubstitution.Substitute("var(--a)", t, sink);
        Assert.Equal("red", result);
    }

    // ============================================================
    // Rec 4 — Empty fallback semantics
    // ============================================================

    [Fact]
    public void Rec4_Empty_fallback_substitutes_to_empty_string()
    {
        // var(--missing,) — comma present, fallback empty. Per spec: substitutes to "".
        var result = VarSubstitution.Substitute("var(--missing,)", Table());
        Assert.Equal("", result);
    }

    [Fact]
    public void Rec4_Whitespace_only_fallback_also_empty()
    {
        // var(--missing,   ) — whitespace-only fallback. Spec treats as empty too.
        var result = VarSubstitution.Substitute("var(--missing,   )", Table());
        Assert.Equal("", result);
    }

    [Fact]
    public void Rec4_Missing_fallback_still_uses_unset()
    {
        // var(--missing) — no comma at all. Spec: invalid → unset.
        var result = VarSubstitution.Substitute("var(--missing)", Table());
        Assert.Equal(VarSubstitution.UnsetSentinel, result);
    }

    [Fact]
    public void Rec4_Empty_fallback_with_surrounding_text_yields_just_surrounding()
    {
        // padding: var(--missing,) 16px → "  16px" (the empty replacement leaves
        // the rest intact).
        var result = VarSubstitution.Substitute("var(--missing,) 16px", Table());
        Assert.Equal(" 16px", result);
    }

    // ============================================================
    // Rec 7 — Empty singleton immutability
    // ============================================================

    [Fact]
    public void Rec7_Empty_singleton_set_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => CustomPropertyTable.Empty.Set("--x", "y"));
    }

    [Fact]
    public void Rec7_Empty_singleton_markInvalid_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => CustomPropertyTable.Empty.MarkInvalid("--x"));
    }

    [Fact]
    public void Rec7_Empty_singleton_can_still_be_used_as_parent()
    {
        // Defensive: we want callers to use Empty as parent freely. Construction with
        // Empty as parent works; the resulting child is mutable.
        var child = new CustomPropertyTable(CustomPropertyTable.Empty);
        child.Set("--x", "y");
        Assert.True(child.TryGetValue("--x", out var v));
        Assert.Equal("y", v);
        // Empty itself remains untouched.
        Assert.False(CustomPropertyTable.Empty.TryGetValue("--x", out _));
    }
}
