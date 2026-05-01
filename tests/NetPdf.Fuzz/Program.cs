// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Fuzz;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Phase 0: SharpFuzz harness will be wired up in Phase 2 once the HTML+CSS parsing
        // surface is exposed for fuzzing. For now this is a do-nothing entry point that exists
        // only so the project compiles.
        return 0;
    }
}
