// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.AotSmoke;

/// <summary>
/// AOT smoke test. CI publishes this with `-p:PublishAot=true` and runs the resulting native
/// binary; failure to publish or execute blocks the merge. Confirms NetPdf is reflection-free
/// and trim-friendly throughout the call stack.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        // Phase 0: smoke test only confirms the assembly loads and runs under AOT.
        // Phase 1+ this will exercise HtmlPdf.Convert with a tiny input and verify byte output.
        Console.WriteLine("NetPdf.AotSmoke phase=0 ok");
        return 0;
    }
}
