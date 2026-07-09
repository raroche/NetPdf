// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPdf.UnitTests.Docs;

/// <summary>Per the docs-deferrals PR review Finding #4 — parity guard
/// for <c>docs/deferrals.md</c>. The deferrals doc declares an update
/// rule (edit in the same commit as any code change that adds or
/// removes a deferral); this test enforces the rule by asserting every
/// known deferral ID still has an entry in the file. When a deferral
/// is **picked up + removed**, the developer deletes both the doc
/// section AND the matching <see cref="ExpectedDeferralIds"/> entry
/// in the SAME commit — the parity test fails otherwise. When a
/// deferral is **added**, the developer adds both the doc section
/// AND the new ID to <see cref="ExpectedDeferralIds"/> in the same
/// commit.
///
/// <para>The test is intentionally LOW-FRICTION: it only checks IDs +
/// status appear in the doc, not the full schema (Behavior/Missing/
/// Trigger/Files/Added/Removal). Schema parity is enforced by code
/// review + the doc's own schema preamble. This test guards against
/// the silent-drop failure mode (someone deletes an entry without
/// removing the throw / approximation in source).</para>
///
/// <para><b>Maintenance.</b> The hardcoded
/// <see cref="ExpectedDeferralIds"/> list is the contract. When the
/// doc's entries diverge from this list (either drop OR addition), the
/// test fails with a clear message pointing at the missing ID. Both
/// directions are checked.</para></summary>
public sealed class DeferralsParityTests
{
    /// <summary>Stable kebab-case IDs that MUST appear in
    /// <c>docs/deferrals.md</c>. Add to this list (in the same commit)
    /// when adding a new deferral entry; remove (in the same commit)
    /// when picking one up.</summary>
    private static readonly string[] ExpectedDeferralIds = new[]
    {
        "hyphens-auto-language-routing",
        "uax-24-script-detection",
        "fragmentation-control-residuals",
        "phase-4-painter-wiring",
        "fuzzing-infrastructure",
        "inline-atomic-boxes",
        "inline-box-decoration-painting",
        "table-auto-fixed-spans-borders",
        "table-cell-vertical-align-baseline",
        "multicol-balancing-pagination",
        "float-continuation-propagation",
        "flex-layouter-features",
        "grid-track-sizing-cycle3-narrowed-scope",
        "grid-maximize-extra-space-receiver-deferred",
        "grid-box-sizing-border-box-deferred",
        "grid-sizing-perf-optimizations-deferred",
        "grid-sizing-architecture-followups-deferred",
        "grid-break-resolver-integration-deferred",
        "grid-fragment-plan-shared-sizing-deferral",
        "grid-spanning-item-intrinsic-distribution-deferral",
        "grid-implicit-named-area-and-occurrence-syntax-deferral",
        "recursive-block-continuation-consumed-extent-accounting-deferral",
        "abspos-cycle-1-explicit-only",
        "fixed-cycle-1",
        "layout-to-pdf-pipeline",
        "visual-regression-cross-engine-tolerance",
        "ci-nonblocking-platform-native-deps",
        "ci-nonblocking-macos-x64-runner-availability",
    };

    [Fact]
    public void Deferrals_doc_contains_all_expected_ids()
    {
        var content = LoadDeferralsDoc();
        foreach (var id in ExpectedDeferralIds)
        {
            // Each entry's section heading + ID label both contain
            // the ID — checking the explicit `**ID** — `<id>`` label
            // catches typos AND ensures the schema is followed.
            var anchor = $"**ID** — `{id}`";
            Assert.True(content.Contains(anchor, StringComparison.Ordinal),
                $"docs/deferrals.md is missing the deferral ID '{id}'. " +
                $"If you picked it up, also remove it from " +
                $"DeferralsParityTests.ExpectedDeferralIds in the same " +
                $"commit. If this is a new ID you forgot to add to the " +
                $"test, add it to ExpectedDeferralIds. Expected anchor: " +
                $"`{anchor}`.");
        }
    }

    [Fact]
    public void Deferrals_doc_does_not_contain_unexpected_ids()
    {
        // Reverse-direction parity: scan the doc for `**ID** — `…``
        // labels + assert each ID is in the expected list. Catches
        // the case where someone adds a deferral to the doc but
        // forgets to add it to the test (or vice versa — adds a
        // typo'd ID).
        var content = LoadDeferralsDoc();
        const string idMarker = "**ID** — `";
        var pos = 0;
        while (true)
        {
            var start = content.IndexOf(idMarker, pos, StringComparison.Ordinal);
            if (start < 0) break;
            start += idMarker.Length;
            var end = content.IndexOf('`', start);
            if (end < 0) break;
            var id = content.Substring(start, end - start);
            Assert.Contains(id, ExpectedDeferralIds);
            pos = end + 1;
        }
    }

    [Fact]
    public void Deferrals_doc_uses_only_defined_status_values()
    {
        // Every Status line in the doc must use one of the three
        // defined values. Catches typos like "deferred" or "wip"
        // sneaking in instead of the documented vocabulary.
        var content = LoadDeferralsDoc();
        const string statusMarker = "**Status** — ";
        var pos = 0;
        while (true)
        {
            var start = content.IndexOf(statusMarker, pos, StringComparison.Ordinal);
            if (start < 0) break;
            start += statusMarker.Length;
            // Look at the next ~80 chars to capture the status value
            // (may contain qualifier like "(uniform)" after the
            // primary value).
            var snippetEnd = Math.Min(start + 80, content.Length);
            var snippet = content.Substring(start, snippetEnd - start);
            // Status must contain at least one of the defined values
            // (may be a hybrid like "approximated (uniform), throws
            // (mismatch)").
            Assert.True(
                snippet.Contains("approximated", StringComparison.Ordinal)
                || snippet.Contains("throws", StringComparison.Ordinal)
                || snippet.Contains("not-started", StringComparison.Ordinal),
                $"Status line in docs/deferrals.md uses an undefined " +
                $"vocabulary. Allowed: approximated | throws | not-started. " +
                $"Saw: '{snippet.Trim()}'.");
            pos = snippetEnd;
        }
    }

    [Fact]
    public void Deferrals_priority_lines_use_a_defined_level_and_carry_a_rationale()
    {
        // The optional Priority field, when present, must use P1|P2|P3 AND carry a one-line rationale
        // (schema preamble: "`P1` (high) / `P2` (medium) / `P3` (low), with a one-line rationale"). Without
        // this, a future edit could write `P0`/`High`, omit the rationale, or malform the syntax and CI would
        // stay green. Lightweight schema protection matching the ID/status guards above.
        var content = LoadDeferralsDoc();
        const string marker = "**Priority** — ";
        // Priority value is a bold token like `**P2 (medium).**` immediately followed by the rationale on the
        // same line (which may then wrap onto following lines — we only assert the rationale STARTS non-empty).
        var lineRegex = new Regex(@"^\*\*(P[123])\b[^\r\n*]*\*\*\s+(\S[^\r\n]*)$");
        var pos = 0;
        var seen = 0;
        while (true)
        {
            var start = content.IndexOf(marker, pos, StringComparison.Ordinal);
            if (start < 0) break;
            start += marker.Length;
            var eol = content.IndexOfAny(new[] { '\r', '\n' }, start);
            if (eol < 0) eol = content.Length;
            var line = content.Substring(start, eol - start);
            var m = lineRegex.Match(line);
            Assert.True(m.Success,
                "Priority line in docs/deferrals.md is malformed. Expected " +
                "`**P1|P2|P3 (word).** <rationale>` (levels: P1 high / P2 medium / P3 low). " +
                $"Saw: '{line.Trim()}'.");
            Assert.True(m.Groups[2].Value.Trim().Length > 0,
                $"Priority '{m.Groups[1].Value}' in docs/deferrals.md has no rationale. Saw: '{line.Trim()}'.");
            seen++;
            pos = eol;
        }
        // Guard against the marker/format silently changing so nothing is validated (the newer entries carry
        // a Priority, so at least a couple must be present + well-formed).
        Assert.True(seen >= 2,
            $"expected the newer deferral entries to carry a well-formed Priority field; matched {seen}.");
    }

    private static string LoadDeferralsDoc()
    {
        // Walk upward from the test assembly's location to find the
        // repo root (signaled by NetPdf.slnx). Works for both `dotnet
        // test` from the repo root + IDE test runners.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "deferrals.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate docs/deferrals.md from the test assembly's " +
            "location. Did the file move? AppContext.BaseDirectory = " +
            $"{AppContext.BaseDirectory}.");
    }
}
