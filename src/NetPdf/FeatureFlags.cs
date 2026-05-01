// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Opt-in flags that toggle experimental or non-default behavior. v1 ships with all
/// non-default flags off; consumers opt in deliberately.
/// </summary>
[Flags]
public enum FeatureFlags
{
    Default = 0,

    /// <summary>Render <c>@container</c> queries (post-v1; experimental).</summary>
    EnableContainerQueries = 1 << 0,

    /// <summary>Render <c>subgrid</c> values (post-v1; experimental).</summary>
    EnableSubgrid = 1 << 1,

    /// <summary>
    /// Throw <see cref="HtmlPdfException"/> instead of emitting a warning when an unsupported
    /// CSS feature is encountered. Useful in CI pipelines that should fail closed.
    /// </summary>
    StrictUnsupportedCss = 1 << 2,

    /// <summary>
    /// Freeze <c>/CreationDate</c> and <c>/ModDate</c> to a stable timestamp so byte-equal
    /// inputs produce byte-equal outputs. Recommended in CI.
    /// </summary>
    DeterministicTimestamps = 1 << 3,

    /// <summary>Generate PDF outlines (bookmarks) from <c>&lt;h1&gt;</c>–<c>&lt;h6&gt;</c>.</summary>
    GenerateOutlines = 1 << 4,
}
