// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// A feature encountered in the input that is parsed but not yet rendered. Distinct from
/// a <see cref="Diagnostic"/> in that it represents a known gap, not a runtime issue.
/// Equivalent diagnostic codes are also emitted for convenience.
/// </summary>
public sealed class UnsupportedFeature
{
    public required string Code { get; init; }
    public required string Description { get; init; }
    public int OccurrenceCount { get; init; }
}
