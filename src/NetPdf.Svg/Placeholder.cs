// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Svg;

/// <summary>
/// Phase 0 placeholder. Real SVG renderer (shapes, paths, transforms, gradients, text) lands in
/// Phase 4 per the architecture plan.
/// </summary>
internal static class SvgRendererMarker
{
    internal const string Phase = "0";
    internal const string ExpectedShipPhase = "4";
}
