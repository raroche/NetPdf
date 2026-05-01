// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Microsoft.CodeAnalysis;

namespace NetPdf.SourceGen;

[Generator]
internal sealed class PlaceholderGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
            ctx.AddSource("NetPdf.SourceGen.Marker.g.cs",
                "// Placeholder. Real generators (CSS property tables, selector bytecode, font tables) follow in Phase 2.\n"));
    }
}
