// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;

namespace NetPdf.Samples.ReadmeSnippets;

internal static class Program
{
    public static int Main(string[] args)
    {
        // The snippets below mirror the README. Each is wrapped in a try/catch because
        // the public HtmlPdf.Convert facade still throws NotImplementedException at the
        // 0.1.0-alpha milestone — the internal byte writer, font subsetter, image
        // embedders, and text shaping shipped in Phase 1, but the HTML parsing + CSS
        // cascade glue that this facade depends on lands in Phase 2 (`0.3.0-alpha`).
        // Once Phase 2 ships, the catches are removed and the snippets become a
        // CI-validated doc test.

        Console.WriteLine($"NetPdf version {HtmlPdf.Version}");
        Console.WriteLine("(Phase 1 alpha — internal engine shipped; HtmlPdf.Convert wires up in Phase 2.)");

        TryRun("README example #1: one-liner", static () =>
        {
            var pdf = HtmlPdf.Convert("<h1>Invoice #1234</h1><p>Hello world.</p>");
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "out.pdf"), pdf);
        });

        TryRun("README example #2: with options", static () =>
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

        TryRun("README example #4: detailed mode", static () =>
        {
            var result = HtmlPdf.ConvertDetailed("<p>hello</p>");
            foreach (var d in result.Warnings)
                Console.WriteLine($"  warn [{d.Code}]: {d.Message}");
        });

        return 0;
    }

    private static void TryRun(string label, Action body)
    {
        Console.WriteLine($"-- {label}");
        try
        {
            body();
            Console.WriteLine("   ok");
        }
        catch (NotImplementedException)
        {
            Console.WriteLine("   skipped (Phase 1 alpha — HtmlPdf.Convert wires up in Phase 2)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
