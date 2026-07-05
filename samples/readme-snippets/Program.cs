// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;

namespace NetPdf.Samples.ReadmeSnippets;

internal static class Program
{
    public static int Main(string[] args)
    {
        // The snippets below mirror the README examples. They run against the live HtmlPdf facade and write
        // their output to the temp directory; a render failure is reported and counted so this stays a
        // CI-validatable doc test (non-zero exit on any failure).
        Console.WriteLine($"NetPdf version {HtmlPdf.Version}");

        var failures = 0;

        failures += TryRun("README example #1: one-liner", static () =>
        {
            var pdf = HtmlPdf.Convert("<h1>Invoice #1234</h1><p>Hello world.</p>");
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "out.pdf"), pdf);
        });

        failures += TryRun("README example #2: with options", static () =>
        {
            var html = "<p>Letter-sized doc</p>";
            var pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
            {
                BaseUri = new Uri("file:///app/templates/"),
                PageSize = PageSize.Letter,
                PrintBackgrounds = true,
            });
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "letter.pdf"), pdf);
        });

        failures += TryRun("README example #4: detailed mode", static () =>
        {
            var result = HtmlPdf.ConvertDetailed("<p>hello</p>");
            foreach (var d in result.Warnings)
            {
                Console.WriteLine($"  warn [{d.Code}]: {d.Message}");
            }
        });

        return failures == 0 ? 0 : 1;
    }

    private static int TryRun(string label, Action body)
    {
        Console.WriteLine($"-- {label}");
        try
        {
            body();
            Console.WriteLine("   ok");
            return 0;
        }
        catch (HtmlPdfException ex)
        {
            // NetPdf surfaces a hard render failure as a typed exception carrying a stable diagnostic code.
            Console.WriteLine($"   FAILED [{ex.Code}]: {ex.Message}");
            return 1;
        }
    }
}
