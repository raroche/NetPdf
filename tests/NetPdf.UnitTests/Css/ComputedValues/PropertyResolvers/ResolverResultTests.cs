// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Sanity tests for <see cref="ResolverResult"/> + <see cref="ResolutionState"/> —
/// the structured-return abstraction introduced in cycle 1's review pass that
/// distinguishes valid-but-deferred values from parse failures (which the cycle-1
/// implementation conflated under <see cref="ComputedSlot.Unset"/>).
/// </summary>
public sealed class ResolverResultTests
{
    [Fact]
    public void Resolved_factory_carries_slot_and_no_raw_text()
    {
        var slot = ComputedSlot.FromLengthPx(16.0);
        var r = ResolverResult.Resolved(slot);
        Assert.Equal(ResolutionState.Resolved, r.State);
        Assert.True(r.IsResolved);
        Assert.False(r.IsDeferred);
        Assert.False(r.IsInvalid);
        Assert.Equal(slot, r.Slot);
        Assert.Null(r.RawText);
    }

    [Fact]
    public void Deferred_factory_carries_raw_text_and_unset_slot()
    {
        var r = ResolverResult.Deferred("2em");
        Assert.Equal(ResolutionState.Deferred, r.State);
        Assert.False(r.IsResolved);
        Assert.True(r.IsDeferred);
        Assert.False(r.IsInvalid);
        Assert.Equal(ComputedSlot.Unset, r.Slot);
        Assert.Equal("2em", r.RawText);
    }

    [Fact]
    public void Invalid_factory_carries_no_slot_and_no_raw_text()
    {
        var r = ResolverResult.Invalid();
        Assert.Equal(ResolutionState.Invalid, r.State);
        Assert.False(r.IsResolved);
        Assert.False(r.IsDeferred);
        Assert.True(r.IsInvalid);
        Assert.Equal(ComputedSlot.Unset, r.Slot);
        Assert.Null(r.RawText);
    }

    [Fact]
    public void Three_states_are_mutually_distinct()
    {
        var resolved = ResolverResult.Resolved(ComputedSlot.FromLengthPx(0));
        var deferred = ResolverResult.Deferred("2em");
        var invalid = ResolverResult.Invalid();
        Assert.NotEqual(resolved, deferred);
        Assert.NotEqual(resolved, invalid);
        // Deferred(text) and Invalid() differ only in State + RawText. The record
        // equality should still split them.
        Assert.NotEqual(deferred, invalid);
    }
}
