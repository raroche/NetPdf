// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Fuzz;
using SharpFuzz;

// NetPdf security fuzz harness. Two modes over the SAME target bodies (FuzzTargets):
//
//   (default) / --smoke   Deterministic, no-instrumentation smoke pass over the seeded
//                         corpus + reproducible mutations. This is what CI runs on every PR
//                         (`dotnet run --project tests/NetPdf.Fuzz -c Release -- --smoke`).
//                         Exit 0 = every target survived; exit 1 = a finding (crash / hang).
//
//   --libfuzzer           A real coverage-guided libFuzzer campaign. Requires the assemblies
//                         to be instrumented with `sharpfuzz` first — see docs/security/fuzzing.md.
//
// The default is the smoke pass precisely because libFuzzer mode needs native instrumentation
// that a plain `dotnet run` / CI checkout does not have.

if (Array.Exists(args, a => a is "--libfuzzer"))
{
    Fuzzer.LibFuzzer.Run(FuzzTargets.RunDispatch);
    return 0;
}

var mutations = ParseMutations(args, fallback: 64);
return SmokeRunner.Run(mutations, Console.Out);

static int ParseMutations(string[] args, int fallback)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--mutations" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n >= 0)
        {
            return n;
        }
    }

    return fallback;
}
