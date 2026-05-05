// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Tests for <see cref="SelectorBloomFilter"/>. The only soundness invariant for a Bloom
/// filter is "no false negatives" — anything inserted MUST report present. False positives
/// are allowed but should stay rare for typical CSS workloads.
/// </summary>
public sealed class SelectorBloomFilterTests
{
    [Fact]
    public void Empty_filter_reports_nothing_present()
    {
        var filter = default(SelectorBloomFilter);
        Assert.False(filter.MightContain("div"));
        Assert.False(filter.MightContain("foo"));
        Assert.False(filter.MightContain(""));
    }

    [Fact]
    public void Inserted_token_reports_present()
    {
        var filter = default(SelectorBloomFilter);
        filter.Add("div");
        Assert.True(filter.MightContain("div"));
    }

    [Fact]
    public void No_false_negatives_for_typical_token_sets()
    {
        var filter = default(SelectorBloomFilter);
        var tokens = new[]
        {
            "html", "body", "div", "span", "p", "h1", "h2", "h3", "h4", "h5", "h6",
            "a", "ul", "ol", "li", "table", "thead", "tbody", "tr", "td", "th",
            "container", "row", "col", "header", "footer", "main", "nav", "aside",
            "btn", "btn-primary", "btn-secondary", "card", "card-header", "card-body",
            "id-main", "id-content", "id-sidebar",
        };
        foreach (var t in tokens) filter.Add(t);
        // Every inserted token must report present — soundness invariant.
        foreach (var t in tokens) Assert.True(filter.MightContain(t), $"missing: {t}");
    }

    [Fact]
    public void False_positive_rate_stays_below_5_percent_for_50_tokens()
    {
        // Smoke gauge — not a tight bound, just a soundness signal that the hashing isn't
        // producing pathological collisions. With 50 inserted tokens and 4096 bits + 2 hashes,
        // theoretical FP rate is well under 1%; allow 5% slack for the random sample variance.
        var filter = default(SelectorBloomFilter);
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        var rng = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            var token = $"token-{rng.Next(0, 100_000)}";
            inserted.Add(token);
            filter.Add(token);
        }

        var falsePositives = 0;
        const int probes = 1000;
        for (var i = 0; i < probes; i++)
        {
            var probe = $"probe-{rng.Next(100_000, 200_000)}";
            if (inserted.Contains(probe)) continue;
            if (filter.MightContain(probe)) falsePositives++;
        }
        Assert.True(falsePositives < probes * 5 / 100,
            $"FP rate {falsePositives}/{probes} exceeds 5% — hash distribution may be broken.");
    }

    [Fact]
    public void Clear_resets_all_bits()
    {
        var filter = default(SelectorBloomFilter);
        filter.Add("foo");
        filter.Add("bar");
        Assert.True(filter.MightContain("foo"));
        filter.Clear();
        Assert.False(filter.MightContain("foo"));
        Assert.False(filter.MightContain("bar"));
    }

    [Fact]
    public void Add_and_MightContain_accept_span_and_string_equivalently()
    {
        var f1 = default(SelectorBloomFilter);
        var f2 = default(SelectorBloomFilter);
        f1.Add("hello");
        f2.Add("hello".AsSpan());
        // Two filters seeded with equivalent tokens via different overloads must agree on
        // membership for any probe — implies the hashing is span-vs-string consistent.
        Assert.Equal(f1.MightContain("hello"), f2.MightContain("hello"));
        Assert.Equal(f1.MightContain("nonpresent"), f2.MightContain("nonpresent"));
    }

    [Fact]
    public void Add_null_token_throws()
    {
        var filter = default(SelectorBloomFilter);
        Assert.Throws<ArgumentNullException>(() => filter.Add((string)null!));
    }

    [Fact]
    public void MightContain_null_token_throws()
    {
        var filter = default(SelectorBloomFilter);
        Assert.Throws<ArgumentNullException>(() => filter.MightContain((string)null!));
    }

    [Fact]
    public void Filter_size_is_512_bytes()
    {
        // 4096 bits / 8 = 512 bytes per filter — matches the type-level constants. Use
        // Unsafe.SizeOf to avoid requiring AllowUnsafeBlocks on the test project.
        Assert.Equal(
            SelectorBloomFilter.BitCount / 8,
            System.Runtime.CompilerServices.Unsafe.SizeOf<SelectorBloomFilter>());
    }
}
