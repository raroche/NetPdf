// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Semantic;

namespace NetPdf.Phase2;

/// <summary>
/// The artifacts produced by one invocation of <see cref="Phase2Pipeline"/>:
/// the box tree (visual layout), the semantic tree (accessibility / structure
/// for PDF/UA), the resolved cascade (typed property values per element /
/// pseudo), and the adapted stylesheet list (kept for snapshot tests + future
/// re-runs without re-parsing).
/// </summary>
/// <param name="BoxRoot">The root <see cref="Box"/> from
/// <see cref="BoxBuilder.Build"/>; always <see cref="BoxKind.Root"/>.</param>
/// <param name="SemanticRoot">The root <see cref="SemanticNode"/> from
/// <see cref="SemanticTreeBuilder.Build"/>; always <see cref="SemanticKind.Document"/>.</param>
/// <param name="Cascade">The cascade output post <see cref="VarResolver"/>;
/// available so consumers can re-resolve specific elements / pseudos for
/// ad-hoc inspection.</param>
/// <param name="Sheets">The adapted stylesheets in cascade order.</param>
internal readonly record struct Phase2Result(
    Box BoxRoot,
    SemanticNode SemanticRoot,
    ResolvedCascadeResult Cascade,
    ImmutableArray<CssStylesheet> Sheets);
