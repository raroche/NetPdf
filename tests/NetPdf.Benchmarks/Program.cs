// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Running;

namespace NetPdf.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Phase 0: nothing to benchmark yet. BenchmarkDotNet will report "no benchmarks found"
        // and exit gracefully, which is the expected state for the architecture-lock phase.
        // Real benchmark suites land in Phase 1 (PDF writer / text shaping) and onward.
        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
