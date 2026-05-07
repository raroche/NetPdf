// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.LayoutSnapshots.Serialization;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.LayoutSnapshots;

/// <summary>
/// Task 18 cycle 1 + hardening — snapshot driver. Each fixture under
/// <c>tests/NetPdf.LayoutSnapshots/Fixtures/</c> holds:
/// <list type="bullet">
///   <item><c>input.html</c> — minified HTML to render through the
///     full Phase 2 pipeline. Authors keep these one-line on purpose
///     (see Task 18 hardening Rec 4): pretty-printed HTML produces
///     spec-correct but noisy whitespace-only <c>TextRun</c>s that
///     drown semantic content in the golden — minified input keeps
///     the snapshot focused on structural output.</item>
///   <item><c>box-tree.txt</c> — golden box-tree serialization.</item>
///   <item><c>semantic-tree.txt</c> — golden semantic-tree serialization.</item>
///   <item><c>diagnostics.txt</c> — golden CSS-diagnostic emission set
///     (added in hardening Rec 3). Empty file means "this fixture runs
///     clean"; non-empty captures the exact codes + severities.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding a new fixture.</b>
/// <list type="number">
///   <item>Create <c>Fixtures/&lt;name&gt;/input.html</c> (minified —
///     one element per logical position, no inter-tag whitespace).</item>
///   <item>Run <c>NETPDF_UPDATE_SNAPSHOTS=1 dotnet test
///     tests/NetPdf.LayoutSnapshots</c> — auto-discovery picks up the
///     new directory and the helper writes box-tree, semantic-tree,
///     and diagnostics goldens to the source tree.</item>
///   <item>Review the diff in git + commit.</item>
/// </list>
/// Per Rec 5 the runner auto-discovers every directory under
/// <c>Fixtures/</c> at test-discovery time so a new fixture cannot sit
/// silently un-executed.
/// </para>
/// <para>
/// <b>Updating after a change.</b> When a Phase 2 change intentionally
/// reshapes the box / semantic / diagnostic output, run with
/// <c>NETPDF_UPDATE_SNAPSHOTS=1</c> to overwrite the goldens. Review the
/// diff carefully — accidentally regenerating drops the regression-
/// detection value of the snapshots entirely.
/// </para>
/// </remarks>
public sealed class SnapshotTests
{
    private static readonly string FixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Auto-discovered fixture directory names. Sorted ordinal so
    /// CI / IDE output is stable across runs and operating systems.</summary>
    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            if (!Directory.Exists(FixturesRoot)) return data;

            foreach (var name in Directory.EnumerateDirectories(FixturesRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal))
            {
                data.Add(name!);
            }
            return data;
        }
    }

    /// <summary>Rec 5 — guard against the failure mode where a fixture
    /// directory exists but is missing its <c>input.html</c>. The bootstrap
    /// in <see cref="SnapshotAssert"/> handles missing snapshot files (it
    /// writes them on first run); a missing input.html would surface as a
    /// confusing IO exception deep inside the pipeline run, so catch it
    /// up front. Also fails when the fixtures directory is entirely empty
    /// — that's almost certainly a build-output misconfiguration.</summary>
    [Fact]
    public void Every_fixture_has_input_html_and_at_least_one_fixture_exists()
    {
        Assert.True(Directory.Exists(FixturesRoot),
            $"Fixtures root not found at '{FixturesRoot}'. Check NetPdf.LayoutSnapshots.csproj's CopyToOutputDirectory entry.");

        var dirs = Directory.EnumerateDirectories(FixturesRoot).ToList();
        Assert.NotEmpty(dirs);

        foreach (var dir in dirs)
        {
            var inputPath = Path.Combine(dir, "input.html");
            Assert.True(File.Exists(inputPath),
                $"Fixture '{Path.GetFileName(dir)}' is missing input.html at '{inputPath}'.");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task BoxTree_matches_snapshot(string fixtureName)
    {
        var (boxRoot, _, _) = await RunPipelineAsync(fixtureName);
        var actual = BoxTreeSerializer.Serialize(boxRoot);
        SnapshotAssert.MatchesFile(actual, Path.Combine(fixtureName, "box-tree.txt"));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task SemanticTree_matches_snapshot(string fixtureName)
    {
        var (_, semanticRoot, _) = await RunPipelineAsync(fixtureName);
        var actual = SemanticTreeSerializer.Serialize(semanticRoot);
        SnapshotAssert.MatchesFile(actual, Path.Combine(fixtureName, "semantic-tree.txt"));
    }

    /// <summary>Rec 3 — captures the CSS-diagnostic emission set per
    /// fixture so an unsupported-feature regression (e.g., a previously-
    /// silent code starts firing) breaks the build instead of staying
    /// invisible. See <see cref="DiagnosticsSerializer"/> for format.</summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task Diagnostics_match_snapshot(string fixtureName)
    {
        var (_, _, diagnostics) = await RunPipelineAsync(fixtureName);
        var actual = DiagnosticsSerializer.Serialize(diagnostics);
        SnapshotAssert.MatchesFile(actual, Path.Combine(fixtureName, "diagnostics.txt"));
    }

    /// <summary>Rec 2 — the box-tree snapshot for <c>04-var-and-calc</c>
    /// only proves the tree shape; it does NOT prove the var()/calc() chain
    /// resolves to the expected pixel value because <see cref="BoxTreeSerializer"/>
    /// doesn't emit computed-style fields by design (capturing every
    /// resolved value in the golden would lock in cycle-1 internals + churn
    /// every fixture every time a property landed). This paired assertion
    /// closes that gap by reading <c>padding-left</c> from the styled
    /// <c>p.x</c> box and asserting it resolves to the spec-defined 20px
    /// (8px base × 2 doubled + 4px = 20px).</summary>
    [Fact]
    public async Task VarAndCalc_resolves_padding_left_to_20px()
    {
        var (boxRoot, _, _) = await RunPipelineAsync("04-var-and-calc");
        var p = WalkBoxes(boxRoot)
            .FirstOrDefault(b => b.Kind == BoxKind.BlockContainer
                              && b.SourceElement?.LocalName == "p");
        Assert.NotNull(p);

        var paddingLeft = p!.Style.Get(PropertyId.PaddingLeft);
        Assert.Equal(ComputedSlotTag.LengthPx, paddingLeft.Tag);
        Assert.Equal(20.0, paddingLeft.AsLengthPx(), precision: 4);
    }

    private static async Task<(Box BoxRoot, Layout.Semantic.SemanticNode SemanticRoot, IReadOnlyList<CssDiagnostic> Diagnostics)>
        RunPipelineAsync(string fixtureName)
    {
        var html = LoadInputHtml(fixtureName);
        var sink = new CapturingSink();
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        return (result.BoxRoot, result.SemanticRoot, sink.Diagnostics);
    }

    private static string LoadInputHtml(string fixtureName)
    {
        var path = Path.Combine(FixturesRoot, fixtureName, "input.html");
        return File.ReadAllText(path);
    }

    private static IEnumerable<Box> WalkBoxes(Box root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in WalkBoxes(child))
                yield return descendant;
    }

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }
}
