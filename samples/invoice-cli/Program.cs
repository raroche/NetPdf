// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;

namespace NetPdf.Samples.InvoiceCli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("usage: invoice-cli <input.html> <output.pdf>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Renders an HTML invoice to a PDF file.");
            Console.Error.WriteLine($"NetPdf version {HtmlPdf.Version}");
            return 1;
        }

        var inputPath = args[0];
        var outputPath = args[1];

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"input not found: {inputPath}");
            return 2;
        }

        try
        {
            var html = File.ReadAllText(inputPath);
            var pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
            {
                BaseUri = new Uri(Path.GetFullPath(inputPath)),
                PageSize = PageSize.A4,
                Margins = PageMargins.Default,
                PrintBackgrounds = true,
                Title = Path.GetFileNameWithoutExtension(inputPath),
                Features = FeatureFlags.DeterministicTimestamps,
            });
            File.WriteAllBytes(outputPath, pdf);
            Console.WriteLine($"wrote {outputPath} ({pdf.Length:N0} bytes)");
            return 0;
        }
        catch (NotImplementedException ex)
        {
            // NetPdf 0.1.0-alpha shipped the internal byte writer, font subsetter, image
            // embedders, and text shaping — but the public HtmlPdf.Convert facade still
            // throws because the HTML+CSS pipeline glue lands in Phase 2 (`0.3.0-alpha`).
            // This catch goes away once Phase 2 ships; until then the error is expected
            // and the exit code (3) is stable for CI scripts that wrap this CLI.
            Console.Error.WriteLine(
                $"NetPdf {HtmlPdf.Version} — HtmlPdf.Convert wires up in Phase 2.");
            Console.Error.WriteLine($"  {ex.Message}");
            return 3;
        }
        catch (HtmlPdfException ex)
        {
            Console.Error.WriteLine($"NetPdf error [{ex.Code}]: {ex.Message}");
            return 4;
        }
    }
}
