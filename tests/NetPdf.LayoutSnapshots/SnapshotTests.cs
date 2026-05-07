// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Threading.Tasks;
using NetPdf.LayoutSnapshots.Serialization;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.LayoutSnapshots;

/// <summary>
/// Task 18 cycle 1 — drives the snapshot framework against every fixture
/// under <c>tests/NetPdf.LayoutSnapshots/Fixtures/</c>. Each fixture has:
/// <list type="bullet">
///   <item><c>input.html</c> — the document to render through the
///     full Phase 2 pipeline.</item>
///   <item><c>box-tree.txt</c> — the deterministic box-tree serialization
///     (golden).</item>
///   <item><c>semantic-tree.txt</c> — the deterministic semantic-tree
///     serialization (golden).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding a new fixture.</b>
/// <list type="number">
///   <item>Create <c>Fixtures/&lt;name&gt;/input.html</c>.</item>
///   <item>Add the fixture name to the theory's <c>InlineData</c> entries
///     below.</item>
///   <item>Run <c>NETPDF_UPDATE_SNAPSHOTS=1 dotnet test
///     tests/NetPdf.LayoutSnapshots</c> — the helper writes the box-tree
///     + semantic-tree snapshots to the source tree.</item>
///   <item>Review the diff in git + commit.</item>
/// </list>
/// </para>
/// <para>
/// <b>Updating after a change.</b> When a Phase 2 change intentionally
/// reshapes the box / semantic tree, run with
/// <c>NETPDF_UPDATE_SNAPSHOTS=1</c> to overwrite the goldens. Review the
/// diff carefully — accidentally regenerating drops the regression-
/// detection value of the snapshots entirely.
/// </para>
/// </remarks>
public sealed class SnapshotTests
{
    public static TheoryData<string> FixtureNames => new()
    {
        "01-simple-paragraph",
        "02-list-with-markers",
        "03-table-with-caption",
        "04-var-and-calc",
        "05-pseudo-with-attr",
        "06-hidden-and-aria",
        "07-figure-with-img",
    };

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task BoxTree_matches_snapshot(string fixtureName)
    {
        var html = LoadInputHtml(fixtureName);
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var actual = BoxTreeSerializer.Serialize(result.BoxRoot);
        SnapshotAssert.MatchesFile(actual, Path.Combine(fixtureName, "box-tree.txt"));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task SemanticTree_matches_snapshot(string fixtureName)
    {
        var html = LoadInputHtml(fixtureName);
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var actual = SemanticTreeSerializer.Serialize(result.SemanticRoot);
        SnapshotAssert.MatchesFile(actual, Path.Combine(fixtureName, "semantic-tree.txt"));
    }

    private static string LoadInputHtml(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName, "input.html");
        return File.ReadAllText(path);
    }
}
