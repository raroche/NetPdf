// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;
using NetPdf.Text.Bidi;
using NetPdf.Text.Bidi.Rules;

namespace NetPdf.UnitTests.Text.Bidi.Conformance;

/// <summary>
/// Test harness for the UCD <c>BidiTest.txt</c> and <c>BidiCharacterTest.txt</c>
/// conformance suites (Stage 12.4 of the bidi delivery). Loads gzipped test files
/// from embedded resources, parses them, runs the algorithm against every test case,
/// and reports pass/fail aggregates.
/// </summary>
/// <remarks>
/// <para>
/// <b>BidiTest.txt format:</b> a sequence of class names per line (e.g., <c>L EN ES B</c>)
/// followed by <c>; bitmask</c>. The bitmask has bits for which paragraph directions
/// are tested: bit 0 = auto, bit 1 = LTR, bit 2 = RTL. Sections are introduced by
/// <c>@Levels:</c> directives that set the expected per-character level output for the
/// following test cases. Levels of <c>x</c> are X9-removed positions where the
/// implementation may report any value.
/// </para>
/// <para>
/// <b>BidiCharacterTest.txt format:</b> per line —
/// <c>codepoints ; paragraph_dir ; resolved_paragraph_level ; resolved_levels ; resolved_order</c>
/// where <c>codepoints</c> is space-separated 4–6 hex digits, <c>paragraph_dir</c>
/// is 0 (LTR), 1 (RTL), or 2 (auto), and <c>resolved_order</c> is the visual reorder
/// (UAX #9 L2–L4 — out of scope for the current implementation; <c>resolved_order</c>
/// is parsed but not asserted).
/// </para>
/// </remarks>
internal static class BidiUcdConformanceHarness
{
    public sealed record HarnessResult(int Passed, int Failed, IReadOnlyList<string> FirstFailures)
    {
        public int Total => Passed + Failed;
        public double PassRate => Total == 0 ? 0.0 : (double)Passed / Total;
    }

    public static HarnessResult RunBidiTest(int sampleFailureLimit = 20)
    {
        using var stream = OpenEmbeddedResource("BidiTest.txt.gz");
        return RunBidiTestStream(stream, sampleFailureLimit);
    }

    public static HarnessResult RunBidiCharacterTest(int sampleFailureLimit = 20)
    {
        using var stream = OpenEmbeddedResource("BidiCharacterTest.txt.gz");
        return RunBidiCharacterTestStream(stream, sampleFailureLimit);
    }

    // ───── BidiTest.txt parser + runner ───────────────────────────────────────

    public static HarnessResult RunBidiTestStream(Stream gzippedStream, int sampleFailureLimit)
    {
        using var gz = new GZipStream(gzippedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        byte[] expectedLevels = [];
        byte[] expectedHasValue = []; // 0 = "x" (skip); 1 = compare against expectedLevels[i]
        var passed = 0;
        var failed = 0;
        var failures = new List<string>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Strip comment + trim (BidiTest.txt has no inline comments past line start, but
            // be defensive in case future versions add them).
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                ParseLevelsDirective(line.AsSpan("@Levels:".Length), out expectedLevels, out expectedHasValue);
                continue;
            }
            if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                // Reordering is L2–L4; not implemented yet.
                continue;
            }

            // Test line: "class class class ... ; bitmask"
            var semi = line.IndexOf(';');
            if (semi < 0) continue;
            var classSpan = line.AsSpan(0, semi).Trim();
            var bitmaskSpan = line.AsSpan(semi + 1).Trim();
            if (!int.TryParse(bitmaskSpan, out var bitmask)) continue;

            var classes = ParseClassSequence(classSpan);
            if (classes.Length != expectedLevels.Length) continue; // malformed

            // Run for each enabled paragraph direction in the bitmask.
            if ((bitmask & 1) != 0)
            {
                EvaluateOne(classes, ParagraphDirection.Auto, expectedLevels, expectedHasValue,
                    line, ref passed, ref failed, failures, sampleFailureLimit);
            }
            if ((bitmask & 2) != 0)
            {
                EvaluateOne(classes, ParagraphDirection.LeftToRight, expectedLevels, expectedHasValue,
                    line, ref passed, ref failed, failures, sampleFailureLimit);
            }
            if ((bitmask & 4) != 0)
            {
                EvaluateOne(classes, ParagraphDirection.RightToLeft, expectedLevels, expectedHasValue,
                    line, ref passed, ref failed, failures, sampleFailureLimit);
            }
        }
        return new HarnessResult(passed, failed, failures);
    }

    private static void ParseLevelsDirective(ReadOnlySpan<char> tail, out byte[] levels, out byte[] hasValue)
    {
        // tail is "0 1 x 2" etc. — count tokens, then build arrays.
        var tokens = SplitWhitespace(tail);
        levels = new byte[tokens.Count];
        hasValue = new byte[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Length == 1 && t[0] == 'x')
            {
                hasValue[i] = 0;
                levels[i] = 0;
            }
            else
            {
                hasValue[i] = 1;
                levels[i] = byte.Parse(t);
            }
        }
    }

    private static BidiClass[] ParseClassSequence(ReadOnlySpan<char> spec)
    {
        var tokens = SplitWhitespace(spec);
        var result = new BidiClass[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            result[i] = ParseBidiClassName(tokens[i]);
        }
        return result;
    }

    private static List<string> SplitWhitespace(ReadOnlySpan<char> span)
    {
        var list = new List<string>();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && (span[i] == ' ' || span[i] == '\t')) i++;
            var start = i;
            while (i < span.Length && span[i] != ' ' && span[i] != '\t') i++;
            if (i > start) list.Add(span[start..i].ToString());
        }
        return list;
    }

    private static BidiClass ParseBidiClassName(string name) => name switch
    {
        "L" => BidiClass.L,
        "R" => BidiClass.R,
        "AL" => BidiClass.AL,
        "EN" => BidiClass.EN,
        "ES" => BidiClass.ES,
        "ET" => BidiClass.ET,
        "AN" => BidiClass.AN,
        "CS" => BidiClass.CS,
        "NSM" => BidiClass.NSM,
        "BN" => BidiClass.BN,
        "B" => BidiClass.B,
        "S" => BidiClass.S,
        "WS" => BidiClass.WS,
        "ON" => BidiClass.ON,
        "LRE" => BidiClass.LRE,
        "LRO" => BidiClass.LRO,
        "RLE" => BidiClass.RLE,
        "RLO" => BidiClass.RLO,
        "PDF" => BidiClass.PDF,
        "LRI" => BidiClass.LRI,
        "RLI" => BidiClass.RLI,
        "FSI" => BidiClass.FSI,
        "PDI" => BidiClass.PDI,
        _ => throw new InvalidDataException($"Unknown bidi class name in test data: '{name}'"),
    };

    private static void EvaluateOne(
        BidiClass[] classes,
        ParagraphDirection requested,
        byte[] expectedLevels,
        byte[] expectedHasValue,
        string sourceLine,
        ref int passed,
        ref int failed,
        List<string> failures,
        int sampleLimit)
    {
        var infos = new BidiCharInfo[classes.Length];
        for (var i = 0; i < classes.Length; i++)
        {
            infos[i] = new BidiCharInfo
            {
                OriginalClass = classes[i],
                ResolvedClass = classes[i],
                Level = 0,
                IsRemovedByX9 = false,
                Utf16Index = i,
                Utf16Length = 1,
                Codepoint = 0,
            };
        }
        var paragraphLevel = ResolveSyntheticParagraphLevel(infos, requested);
        var actualLevels = BidiPipeline.ResolveLevelsForUtf16(infos.AsSpan(), paragraphLevel, classes.Length);

        var match = true;
        for (var i = 0; i < classes.Length; i++)
        {
            if (expectedHasValue[i] == 0) continue;
            if (actualLevels[i] != expectedLevels[i]) { match = false; break; }
        }
        if (match)
        {
            passed++;
        }
        else
        {
            failed++;
            if (failures.Count < sampleLimit)
            {
                failures.Add($"{sourceLine} [dir={requested}] expected=[{Stringify(expectedLevels, expectedHasValue)}] actual=[{string.Join(' ', actualLevels)}]");
            }
        }
    }

    private static string Stringify(byte[] levels, byte[] hasValue)
    {
        var parts = new string[levels.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            parts[i] = hasValue[i] == 0 ? "x" : levels[i].ToString();
        }
        return string.Join(' ', parts);
    }

    private static byte ResolveSyntheticParagraphLevel(ReadOnlySpan<BidiCharInfo> infos, ParagraphDirection requested)
    {
        if (requested == ParagraphDirection.LeftToRight) return 0;
        if (requested == ParagraphDirection.RightToLeft) return 1;
        // Auto P2/P3 over synthetic class data.
        var isolate = 0;
        foreach (var info in infos)
        {
            switch (info.OriginalClass)
            {
                case BidiClass.LRI:
                case BidiClass.RLI:
                case BidiClass.FSI:
                    isolate++;
                    continue;
                case BidiClass.PDI:
                    if (isolate > 0) isolate--;
                    continue;
                case BidiClass.B:
                    return 0;
            }
            if (isolate > 0) continue;
            if (info.OriginalClass == BidiClass.L) return 0;
            if (info.OriginalClass == BidiClass.R || info.OriginalClass == BidiClass.AL) return 1;
        }
        return 0;
    }

    // ───── BidiCharacterTest.txt parser + runner ──────────────────────────────

    public static HarnessResult RunBidiCharacterTestStream(Stream gzippedStream, int sampleFailureLimit)
    {
        using var gz = new GZipStream(gzippedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        var passed = 0;
        var failed = 0;
        var failures = new List<string>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            // Format: codepoints; paragraph_dir; resolved_paragraph_level; resolved_levels; resolved_order
            var fields = line.Split(';');
            if (fields.Length < 4) continue;
            var codepointsStr = fields[0].Trim();
            var paragraphDirStr = fields[1].Trim();
            var resolvedParaLevelStr = fields[2].Trim();
            var resolvedLevelsStr = fields[3].Trim();
            // fields[4] resolved_order — L2-L4, not implemented.

            var codepoints = ParseCodepoints(codepointsStr);
            if (codepoints.Length == 0) continue;
            var paragraphDir = byte.Parse(paragraphDirStr);
            var expectedParaLevel = byte.Parse(resolvedParaLevelStr);
            ParseLevelsDirective(resolvedLevelsStr.AsSpan(), out var expectedLevels, out var expectedHasValue);

            // Convert codepoints to UTF-16 string.
            var utf16 = CodepointsToUtf16(codepoints);

            // Run via the public API (this also exercises P1 paragraph splitting, but
            // BidiCharacterTest.txt cases are single-paragraph by construction).
            var requested = paragraphDir switch
            {
                0 => ParagraphDirection.LeftToRight,
                1 => ParagraphDirection.RightToLeft,
                _ => ParagraphDirection.Auto,
            };
            var actualParaLevel = ParagraphLevelResolver.Resolve(utf16.AsSpan(), requested);
            if (actualParaLevel != expectedParaLevel)
            {
                failed++;
                if (failures.Count < sampleFailureLimit)
                {
                    failures.Add($"{line} [paragraph level mismatch: expected {expectedParaLevel}, actual {actualParaLevel}]");
                }
                continue;
            }

            var actualLevels = BidiAlgorithm.ResolveLevels(utf16.AsSpan(), requested);

            // Compare per-codepoint expected vs per-UTF16-code-unit actual. Walk codepoints
            // and compare against actualLevels at the corresponding UTF-16 offset.
            var matched = true;
            var idx = 0;
            for (var i = 0; i < codepoints.Length; i++)
            {
                var unitLen = codepoints[i] >= 0x10000 ? 2 : 1;
                if (expectedHasValue[i] == 1)
                {
                    if (actualLevels[idx] != expectedLevels[i]) { matched = false; break; }
                }
                idx += unitLen;
            }
            if (matched)
            {
                passed++;
            }
            else
            {
                failed++;
                if (failures.Count < sampleFailureLimit)
                {
                    failures.Add($"{line} [levels mismatch]");
                }
            }
        }
        return new HarnessResult(passed, failed, failures);
    }

    private static int[] ParseCodepoints(string spec)
    {
        var tokens = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            result[i] = int.Parse(tokens[i], System.Globalization.NumberStyles.HexNumber);
        }
        return result;
    }

    private static string CodepointsToUtf16(int[] codepoints)
    {
        var sb = new System.Text.StringBuilder(codepoints.Length);
        foreach (var cp in codepoints)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    // ───── Embedded-resource loading ──────────────────────────────────────────

    private static Stream OpenEmbeddedResource(string filename)
    {
        var assembly = typeof(BidiUcdConformanceHarness).Assembly;
        // Embedded resources use dot-separated paths derived from the project structure.
        var resourceName = $"NetPdf.UnitTests.Resources.Ucd.{filename}";
        var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Embedded resource '{resourceName}' not found. Confirm it's listed as <EmbeddedResource> in NetPdf.UnitTests.csproj.");
        return stream;
    }
}
