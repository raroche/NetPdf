// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Security.Cryptography;

namespace NetPdf.AotSmoke;

/// <summary>
/// AOT smoke entry point. The actual document construction lives in
/// <see cref="SmokeDocumentFactory"/> so the JIT/AOT parity test in
/// <c>NetPdf.UnitTests.Pdf.AotJitParityTests</c> can call the same code without
/// duplicating it. This <see cref="Main"/> just runs the factory, validates the
/// emitted bytes structurally, prints a stable "byteCount=&lt;N&gt; sha256=&lt;HEX&gt;"
/// line that the parity test parses, and (optionally) writes the bytes to a file.
/// <para>
/// Exit codes: 0 = success; 1 = build/save threw; 2 = structural verification
/// failed; 3 = optional output-path write failed. Any non-zero blocks the CI step
/// that runs this binary.
/// </para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBuildOrSaveFailed = 1;
    private const int ExitStructuralSanityFailed = 2;
    private const int ExitWriteFailed = 3;

    public static int Main(string[] args)
    {
        byte[] bytes;
        try
        {
            bytes = SmokeDocumentFactory.BuildSmokeDocument();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("AOT smoke: build/save threw.");
            Console.Error.WriteLine(ex);
            return ExitBuildOrSaveFailed;
        }

        if (!SmokeDocumentFactory.TryVerifyPdfStructure(bytes, out var sanityFailure))
        {
            Console.Error.WriteLine($"AOT smoke: structural verification failed: {sanityFailure}");
            return ExitStructuralSanityFailed;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"NetPdf.AotSmoke phase=1 ok byteCount={bytes.Length} sha256={hash}"));

        if (args.Length >= 1)
        {
            try
            {
                File.WriteAllBytes(args[0], bytes);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"NetPdf.AotSmoke wrote {bytes.Length} bytes to {args[0]}"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AOT smoke: writing to '{args[0]}' failed: {ex.Message}");
                return ExitWriteFailed;
            }
        }

        return ExitOk;
    }
}
