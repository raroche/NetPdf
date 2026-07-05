// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;

namespace NetPdf.Samples.ReadmeSnippets;

internal static class Program
{
    public static int Main(string[] args)
    {
        // The snippets below mirror the README examples 1:1 (one-liner, options, async streaming, detailed).
        // They run against the live HtmlPdf facade and write their output to the temp directory; every
        // failure — a typed render failure OR an unexpected fault — is reported and counted so this stays a
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

        failures += TryRun("README example #3: async streaming", static () =>
        {
            var html = "<p>Streaming report body</p>";
            using var fs = File.Create(Path.Combine(Path.GetTempPath(), "report.pdf"));
            HtmlPdf.ConvertAsync(html, fs, new HtmlPdfOptions { PreferCssPageSize = true })
                .AsTask().GetAwaiter().GetResult();
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
        catch (Exception ex)
        {
            // An UNEXPECTED failure (temp-path I/O, an unforeseen runtime fault) must still be counted and
            // reported — not abort the process — so the remaining snippets keep running and this stays a
            // useful CI doc-test diagnostic (review P3 / Copilot).
            Console.WriteLine($"   FAILED (unexpected {ex.GetType().Name}): {ex.Message}");
            return 1;
        }
    }
}
