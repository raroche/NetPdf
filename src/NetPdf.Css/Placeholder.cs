// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css;

/// <summary>
/// Phase 0 placeholder. Real CSS engine (tokenizer, parser, AST, selector compiler, cascade,
/// computed values) lands in Phase 2 per the architecture plan.
/// </summary>
internal static class CssEngineMarker
{
    internal const string Phase = "0";
    internal const string ExpectedShipPhase = "2";
}
