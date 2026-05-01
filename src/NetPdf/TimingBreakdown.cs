// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Per-stage wall-clock timings. <see cref="Total"/> is the end-to-end conversion time;
/// the per-stage values sum to roughly that minus parallelism overlaps.
/// </summary>
public readonly record struct TimingBreakdown(
    TimeSpan Parse,
    TimeSpan Style,
    TimeSpan BoxGeneration,
    TimeSpan Layout,
    TimeSpan Pagination,
    TimeSpan Paint,
    TimeSpan Emit,
    TimeSpan Total);
