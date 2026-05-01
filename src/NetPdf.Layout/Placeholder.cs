// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout;

/// <summary>
/// Phase 0 placeholder. Real layout (block/inline/flex/grid/table/multicol) lands in Phase 3
/// per the architecture plan.
/// </summary>
internal static class LayoutEngineMarker
{
    internal const string Phase = "0";
    internal const string ExpectedShipPhase = "3";
}
