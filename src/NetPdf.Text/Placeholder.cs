// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text;

/// <summary>
/// Phase 0 placeholder. Real text pipeline (HarfBuzz shaping, bidi UAX #9, line break UAX #14,
/// segmentation UAX #29, hyphenation, font registry) lands in Phase 1 per the architecture plan.
/// </summary>
internal static class TextEngineMarker
{
    internal const string Phase = "0";
    internal const string ExpectedShipPhase = "1";
}
