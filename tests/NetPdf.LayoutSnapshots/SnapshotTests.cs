// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Semantic;
using NetPdf.LayoutSnapshots.Serialization;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.LayoutSnapshots;

/// <summary>
/// Task 18 cycle 1 + hardening + hardening 2 — snapshot driver. Each
/// fixture under <c>tests/NetPdf.LayoutSnapshots/Fixtures/</c> holds:
/// <list type="bullet">
///   <item><c>input.html</c> — minified HTML to render through the
///     full Phase 2 pipeline. Authors keep these one-line on purpose
///     (Task 18 hardening Rec 4): pretty-printed HTML produces
///     spec-correct but noisy whitespace-only <c>TextRun</c>s that
///     drown semantic content in the golden — minified input keeps
///     the snapshot focused on structural output.</item>
///   <item><c>box-tree.txt</c> — golden box-tree serialization.</item>
///   <item><c>semantic-tree.txt</c> — golden semantic-tree serialization.</item>
///   <item><c>diagnostics.txt</c> — golden diagnostic emission set
///     (Task 18 hardening Rec 3). Empty file means "this fixture runs
///     clean"; non-empty captures exact codes + severities + source
///     locations. Now sourced from the public <c>IDiagnosticsSink</c>
///     so HTML-stage diagnostics (<c>HTML-SCRIPT-IGNORED-001</c>,
///     <c>HTML-JAVASCRIPT-URL-IGNORED-001</c>) land alongside CSS-stage
///     codes (Task 18 hardening 2 Rec 1).</item>
/// </list>
/// </summary>
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

    /// <summary>Task 18 hardening Rec 3 — captures the diagnostic emission
    /// set per fixture so an unsupported-feature regression (e.g., a
    /// previously-silent code starts firing) breaks the build instead of
    /// staying invisible. Per Task 18 hardening 2 Rec 1, the capture path
    /// is now the public <see cref="IDiagnosticsSink"/> so HTML-stage
    /// diagnostics land alongside CSS-stage. See
    /// <see cref="DiagnosticsSerializer"/> for format.</summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task Diagnostics_match_snapshot(string fixtureName)
    {
        var (_, _, diagnostics) = await RunPipelineAsync(fixtureName);
        var actual = DiagnosticsSerializer.Serialize(diagnostics);
        SnapshotAssert.MatchesFile(actual, Path.Combine(fixtureName, "diagnostics.txt"));
    }

    /// <summary>Task 18 hardening Rec 2 — closes the value-coverage gap
    /// for <c>04-var-and-calc</c>. The box-tree snapshot only proves the
    /// tree shape because <see cref="BoxTreeSerializer"/> doesn't emit
    /// computed-style fields (capturing every resolved value would lock
    /// in cycle-1 internals + churn every fixture every time a property
    /// landed). Reads <c>padding-left</c> from the styled <c>p.x</c> box
    /// and asserts it resolves to the spec-defined 20px (8px base × 2
    /// doubled + 4px = 20px) — proves the var()/calc() chain through
    /// Tasks 8/9/10.</summary>
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

    /// <summary>Task 18 hardening 2 review Rec 3 — pin the v1 policy that
    /// <c>::before</c> / <c>::after</c> generated text is intentionally
    /// <i>absent</i> from the semantic tree. The semantic tree walks the
    /// static DOM; generated content is materialized by <c>BoxBuilder</c>
    /// against the rendered tree — bridging is deferred to the Phase 5
    /// PDF/UA pass that will re-source the semantic tree from the rendered
    /// box tree (so generated text becomes accessible content per WCAG
    /// 1.1.1, with the marker / before / after distinction routing
    /// generated text to <c>InlineText</c> vs <c>/Artifact</c>).
    /// <para>
    /// This Fact pins the current state explicitly: the <c>05-pseudo-with-attr</c>
    /// fixture's box snapshot DOES carry the <c>"[WIDGET] "</c> generated
    /// text but the semantic tree does NOT. A future change that bridges
    /// generated content into the semantic tree will fail this assertion
    /// — the failure tells the author "you crossed an intentional v1
    /// boundary; update the policy + the snapshot together" rather than
    /// the snapshot drift looking like an accidental regression.
    /// </para></summary>
    [Fact]
    public async Task PseudoWithAttr_generated_text_is_absent_from_semantic_tree_until_phase_5_pdfua()
    {
        var (boxRoot, semanticRoot, _) = await RunPipelineAsync("05-pseudo-with-attr");

        // The pseudo flag lives on the principal InlineBox; the generated
        // text is its TextRun child. Walk pseudos + collect child text.
        var pseudoBoxes = WalkBoxes(boxRoot)
            .Where(b => b.Pseudo == BoxPseudo.Before || b.Pseudo == BoxPseudo.After)
            .ToList();
        Assert.NotEmpty(pseudoBoxes);
        var beforeText = pseudoBoxes.Single(b => b.Pseudo == BoxPseudo.Before)
            .Children.Single(c => c.Kind == BoxKind.TextRun).Text;
        var afterText = pseudoBoxes.Single(b => b.Pseudo == BoxPseudo.After)
            .Children.Single(c => c.Kind == BoxKind.TextRun).Text;
        Assert.Equal("[WIDGET] ", beforeText);
        Assert.Equal(".", afterText);

        var aggregateSemanticText = AllSemanticText(semanticRoot);
        Assert.DoesNotContain("[WIDGET]", aggregateSemanticText);
        Assert.Equal("item description", aggregateSemanticText.Trim());
    }

    private static async Task<(Box BoxRoot, SemanticNode SemanticRoot, IReadOnlyList<Diagnostic> Diagnostics)>
        RunPipelineAsync(string fixtureName)
    {
        var html = LoadInputHtml(fixtureName);
        var sink = new CapturingSink();
        var options = new HtmlPdfOptions { Diagnostics = sink };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
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

    private static string AllSemanticText(SemanticNode node)
    {
        var sb = new System.Text.StringBuilder();
        Visit(node, sb);
        return sb.ToString();

        static void Visit(SemanticNode n, System.Text.StringBuilder buf)
        {
            if (n.Kind == SemanticKind.InlineText) buf.Append(n.Text);
            foreach (var child in n.Children) Visit(child, buf);
        }
    }

    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<Diagnostic> Diagnostics { get; } = new();
        public void Emit(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }
}
