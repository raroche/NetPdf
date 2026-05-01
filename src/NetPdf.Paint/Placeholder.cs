// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Phase 0 placeholder. Real paint pipeline (display list, gradients, shadows, raster fallback)
/// lands across Phases 1 (skeleton) → 4 (visual parity) per the architecture plan.
/// </summary>
internal static class PainterMarker
{
    internal const string Phase = "0";
    internal const string ExpectedShipPhase = "1-4";
}
