// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Cascade;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="CustomPropertyTable"/> — verifies the layered inheritance
/// chain (own writes win over parent; parent walked when own misses), case-sensitive
/// lookup per CSS Custom Properties L1 §2, and the empty / root-table behavior.
/// </summary>
public sealed class CustomPropertyTableTests
{
    [Fact]
    public void Empty_table_returns_false_for_any_lookup()
    {
        Assert.False(CustomPropertyTable.Empty.TryGetValue("--foo", out _));
    }

    [Fact]
    public void Own_layer_lookup_succeeds()
    {
        var t = new CustomPropertyTable(parent: null);
        t.Set("--color", "red");
        Assert.True(t.TryGetValue("--color", out var value));
        Assert.Equal("red", value);
    }

    [Fact]
    public void Own_layer_overrides_parent()
    {
        var parent = new CustomPropertyTable(parent: null);
        parent.Set("--color", "red");
        var child = new CustomPropertyTable(parent);
        child.Set("--color", "blue");

        Assert.True(child.TryGetValue("--color", out var value));
        Assert.Equal("blue", value);
    }

    [Fact]
    public void Parent_chain_walked_when_own_misses()
    {
        var grandparent = new CustomPropertyTable(parent: null);
        grandparent.Set("--inherited", "from-grandparent");
        var parent = new CustomPropertyTable(grandparent);
        var child = new CustomPropertyTable(parent);

        // child + parent both miss; grandparent has it.
        Assert.True(child.TryGetValue("--inherited", out var value));
        Assert.Equal("from-grandparent", value);
    }

    [Fact]
    public void Lookup_is_case_sensitive_per_spec()
    {
        var t = new CustomPropertyTable(parent: null);
        t.Set("--Color", "red");

        Assert.True(t.TryGetValue("--Color", out _));
        Assert.False(t.TryGetValue("--color", out _));
        Assert.False(t.TryGetValue("--COLOR", out _));
    }

    [Fact]
    public void OwnCount_excludes_inherited()
    {
        var parent = new CustomPropertyTable(parent: null);
        parent.Set("--from-parent", "x");
        var child = new CustomPropertyTable(parent);
        child.Set("--from-child", "y");

        Assert.Equal(1, child.OwnCount);
        Assert.Equal(1, parent.OwnCount);
    }

    [Fact]
    public void OwnNames_lists_only_local_writes()
    {
        var parent = new CustomPropertyTable(parent: null);
        parent.Set("--p", "1");
        var child = new CustomPropertyTable(parent);
        child.Set("--c", "2");

        Assert.Single(child.OwnNames);
        Assert.Contains("--c", child.OwnNames);
        Assert.DoesNotContain("--p", child.OwnNames);
    }

    [Fact]
    public void Empty_singleton_can_be_used_as_root_parent()
    {
        var child = new CustomPropertyTable(CustomPropertyTable.Empty);
        child.Set("--x", "y");
        Assert.True(child.TryGetValue("--x", out _));
        Assert.False(child.TryGetValue("--missing", out _));
    }
}
