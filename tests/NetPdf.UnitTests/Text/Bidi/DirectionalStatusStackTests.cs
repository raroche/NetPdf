// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

public sealed class DirectionalStatusStackTests
{
    [Fact]
    public void Top_throws_on_empty_stack()
    {
        var stack = new DirectionalStatusStack();
        Assert.Throws<InvalidOperationException>(() => stack.Top);
    }

    [Fact]
    public void Pop_throws_on_empty_stack()
    {
        var stack = new DirectionalStatusStack();
        Assert.Throws<InvalidOperationException>(stack.Pop);
    }

    [Fact]
    public void Push_then_Top_returns_pushed_entry()
    {
        var stack = new DirectionalStatusStack();
        stack.Push(level: 0, DirectionalOverride.Neutral, isIsolate: false);
        Assert.Equal(0, stack.Top.Level);
        Assert.Equal(DirectionalOverride.Neutral, stack.Top.Override);
        Assert.False(stack.Top.IsIsolate);
        Assert.Equal(1, stack.Depth);
    }

    [Fact]
    public void Push_multiple_entries_tracks_depth_and_LIFO_order()
    {
        var stack = new DirectionalStatusStack();
        stack.Push(0, DirectionalOverride.Neutral, false);
        stack.Push(1, DirectionalOverride.R, false);
        stack.Push(2, DirectionalOverride.L, true);
        Assert.Equal(3, stack.Depth);
        Assert.Equal(2, stack.Top.Level);
        Assert.Equal(DirectionalOverride.L, stack.Top.Override);
        Assert.True(stack.Top.IsIsolate);

        stack.Pop();
        Assert.Equal(2, stack.Depth);
        Assert.Equal(1, stack.Top.Level);
        Assert.Equal(DirectionalOverride.R, stack.Top.Override);

        stack.Pop();
        Assert.Equal(1, stack.Depth);
        Assert.Equal(0, stack.Top.Level);
    }

    [Fact]
    public void Clear_resets_depth_to_zero()
    {
        var stack = new DirectionalStatusStack();
        stack.Push(0, DirectionalOverride.Neutral, false);
        stack.Push(1, DirectionalOverride.R, false);
        stack.Clear();
        Assert.Equal(0, stack.Depth);
    }

    [Fact]
    public void MaxEmbeddingLevel_is_125()
    {
        Assert.Equal(125, DirectionalStatusStack.MaxEmbeddingLevel);
    }
}
