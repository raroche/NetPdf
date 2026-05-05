// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using NetPdf.Css.Parser;
using NetPdf.Css.Selectors;

namespace NetPdf.Css.Cascade;

/// <summary>
/// One <see cref="CssStyleRule"/> with its compiled selector list ready for matching.
/// <see cref="CascadeResolver"/> precompiles every rule once at the start of
/// <see cref="CascadeResolver.Resolve"/> so the per-element matching loop pays the
/// selector-parse cost zero additional times across N elements.
/// </summary>
/// <param name="Selectors">The compiled <see cref="SelectorList"/>; <see langword="null"/>
/// when selector compilation failed (a <c>CSS-PARSE-WARNING-001</c> diagnostic was emitted
/// at compile time and this rule contributes nothing to the cascade).</param>
/// <param name="Declarations">The rule's longhand declarations (already expanded by
/// AngleSharp.Css's parser).</param>
/// <param name="Origin">Cascade origin from the owning stylesheet.</param>
/// <param name="StylesheetOrder">Global source-order index across all stylesheets.</param>
/// <param name="RuleOrder">Within-stylesheet rule index in source order.</param>
/// <param name="LayerOrder">Effective <c>@layer</c> index this rule lives in (per
/// <see cref="LayerRegistry"/>); <c>0</c> when unlayered. Applied to every matched
/// declaration's <see cref="CascadeKey.LayerOrder"/>.</param>
internal sealed record CompiledRule(
    SelectorList? Selectors,
    ImmutableArray<CssDeclaration> Declarations,
    CssStylesheetOrigin Origin,
    int StylesheetOrder,
    int RuleOrder,
    int LayerOrder = 0);
