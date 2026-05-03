// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;
using System.Text;
using NetPdf.Text.LineBreaking;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.LineBreaking;

/// <summary>
/// Stage 13.3 — UCD <c>LineBreakTest.txt</c> conformance validation. Loads the gzipped
/// test corpus from embedded resources, parses each case (sequence of <c>×</c> / <c>÷</c>
/// symbols separated by codepoint hex values), and runs every case through
/// <see cref="LineBreakAlgorithm.FindBreaks"/>. The pass-rate baseline ratchets up as
/// the algorithm hardens.
/// </summary>
public sealed class LineBreakUcdConformanceTests
{
    /// <summary>
    /// Total cases in <c>LineBreakTest.txt</c> 16.0.
    /// </summary>
    private const int LineBreakTestTotalCases = 16_672;

    /// <summary>
    /// Exact expected pass count. Pinning this — instead of just a percentage threshold —
    /// gates against silent "fix one regress one" behavior: any rule change that flips
    /// even a single case requires updating either this constant or the algorithm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Currently 16,664 / 16,672 = 99.952%. <b>All 8 remaining failures are non-Latin-script
    /// edge cases that don't affect English / Spanish / European text rendering:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>5 cases — East-Asian-quotation rules (LB19a/b with CJK ideographs adjacent
    ///         to Western curly quotes). Pure CJK publishing context.</item>
    ///   <item>3 cases — Brahmic Indic-script conjunct edge cases (Sundanese/Batak,
    ///         Balinese, Javanese). Codepoints in ranges <c>U+1B00–U+1BFF</c> and
    ///         <c>U+A980–U+A9DF</c>.</item>
    /// </list>
    /// <para>
    /// The Latin-script subset (<see cref="LineBreakTest_txt_Latin_subset_passes_at_100_percent"/>)
    /// asserts 100% for all cases whose codepoints are entirely in Latin-1 / Latin Extended
    /// / General Punctuation / standard ASCII ranges — the actual production envelope for
    /// English and Spanish layout.
    /// </para>
    /// </remarks>
    private const int LineBreakTestExpectedPassCount = 16_664;

    private readonly ITestOutputHelper _output;

    public LineBreakUcdConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LineBreakTest_txt_conformance_pins_exact_pass_count()
    {
        var (passed, failed, samples) = RunLineBreakTest(sampleFailureLimit: 20, latinSubsetOnly: false);

        var total = passed + failed;
        var passRate = total == 0 ? 0.0 : (double)passed / total;
        _output.WriteLine($"LineBreakTest.txt: {passed} / {total} passed ({passRate:P3}); {failed} failed.");
        if (samples.Count > 0)
        {
            _output.WriteLine($"First {samples.Count} failures:");
            foreach (var f in samples)
            {
                _output.WriteLine($"  {f}");
            }
        }
        Assert.Equal(LineBreakTestTotalCases, total);
        // Exact-count pin: any drop OR rise needs an explicit constant update so silent
        // "fix one, regress one" stays caught.
        Assert.Equal(LineBreakTestExpectedPassCount, passed);
    }

    /// <summary>
    /// Latin-script-subset conformance gate. Filters the corpus to test cases whose
    /// codepoints fall entirely in the production envelope for English / Spanish / standard
    /// European text (ASCII + Latin-1 + Latin Extended-A/B + General Punctuation +
    /// Currency Symbols + Mathematical Operators + Number Forms). Asserts 100% pass on
    /// that subset — the 8 known failures involve CJK or Brahmic Indic codepoints
    /// outside this envelope.
    /// </summary>
    [Fact]
    public void LineBreakTest_txt_Latin_subset_passes_at_100_percent()
    {
        var (passed, failed, samples) = RunLineBreakTest(sampleFailureLimit: 20, latinSubsetOnly: true);
        var total = passed + failed;
        _output.WriteLine($"LineBreakTest.txt (Latin subset): {passed} / {total} passed; {failed} failed.");
        if (samples.Count > 0)
        {
            _output.WriteLine($"First {samples.Count} failures:");
            foreach (var f in samples)
            {
                _output.WriteLine($"  {f}");
            }
        }
        Assert.True(total > 0, "Latin subset filter eliminated all cases.");
        Assert.Equal(0, failed);
    }

    /// <summary>
    /// Returns true if the codepoint is within the "Latin-script production envelope"
    /// — codepoints English / Spanish / general European users actually feed through
    /// the line-break engine. Includes ASCII, Latin-1 supplement, Latin Extended A/B,
    /// IPA, Spacing Modifiers, Combining Diacritical Marks, Greek + Cyrillic (since
    /// many European technical documents include them), General Punctuation, Currency,
    /// Number Forms, basic Mathematical operators, plus structural codes (CR, LF, etc.).
    /// </summary>
    private static bool IsLatinScriptCodepoint(int codepoint) => codepoint switch
    {
        // ASCII + Latin-1 + Latin Extended A/B + IPA + spacing/combining + Greek + Cyrillic
        <= 0x024F => true,                          // Basic Latin / Latin-1 / Latin Extended-A/B
        >= 0x0250 and <= 0x02FF => true,            // IPA / Spacing Modifier Letters
        >= 0x0300 and <= 0x036F => true,            // Combining Diacritical Marks
        >= 0x0370 and <= 0x03FF => true,            // Greek
        >= 0x0400 and <= 0x04FF => true,            // Cyrillic
        // General Punctuation (includes ZWSP, LRM/RLM, em-dash, quotation marks etc.)
        >= 0x2000 and <= 0x206F => true,
        // Currency Symbols
        >= 0x20A0 and <= 0x20CF => true,
        // Combining Diacritical Marks for Symbols
        >= 0x20D0 and <= 0x20FF => true,
        // Number Forms (fractions, Roman numerals)
        >= 0x2150 and <= 0x218F => true,
        // Mathematical Operators
        >= 0x2200 and <= 0x22FF => true,
        // Miscellaneous Technical
        >= 0x2300 and <= 0x23FF => true,
        // Box Drawing / Block Elements / Geometric Shapes
        >= 0x2500 and <= 0x25FF => true,
        // Miscellaneous Symbols (includes some emoji)
        >= 0x2600 and <= 0x26FF => true,
        // Dingbats
        >= 0x2700 and <= 0x27BF => true,
        // Supplementary Mathematical Operators / various
        >= 0x2A00 and <= 0x2AFF => true,
        // Latin Extended Additional
        >= 0x1E00 and <= 0x1EFF => true,
        // Greek Extended
        >= 0x1F00 and <= 0x1FFF => true,
        _ => false,
    };

    private static (int Passed, int Failed, IReadOnlyList<string> Failures) RunLineBreakTest(
        int sampleFailureLimit,
        bool latinSubsetOnly)
    {
        var assembly = typeof(LineBreakUcdConformanceTests).Assembly;
        var resourceName = "NetPdf.UnitTests.Resources.Ucd.LineBreakTest.txt.gz";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' missing.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        var passed = 0;
        var failed = 0;
        var failures = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Strip the trailing comment (after '#').
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            // Parse: alternating symbols (× ÷) and codepoint hex tokens.
            // Format: "× HEX × HEX ÷ HEX ÷"
            var tokens = SplitWhitespace(line);
            var (codepoints, expectedSymbols) = ParseTokens(tokens);
            if (codepoints.Length == 0) continue;

            // Apply the Latin-script subset filter if requested. Skip cases where ANY
            // codepoint falls outside the Latin-script production envelope.
            if (latinSubsetOnly)
            {
                var allLatin = true;
                foreach (var cp in codepoints)
                {
                    if (!IsLatinScriptCodepoint(cp)) { allLatin = false; break; }
                }
                if (!allLatin) continue;
            }

            // Build the UTF-16 input from codepoints.
            var sb = new StringBuilder();
            foreach (var cp in codepoints)
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
            var actual = LineBreakAlgorithm.FindBreaks(sb.ToString().AsSpan());

            // Compare expected vs actual at each position. The expectedSymbols array has
            // length codepoints.Length + 1 (one symbol before each codepoint plus one after
            // the last). We compare the AFTER-each-codepoint position, mapped to the last
            // UTF-16 code unit of that codepoint.
            //   expectedSymbols[0]: before codepoint 0 — corresponds to "start of text",
            //     always × per LB2.
            //   expectedSymbols[k+1]: between codepoint k and k+1 — corresponds to the
            //     opportunity AFTER codepoint k.
            //   expectedSymbols[N]: after the last codepoint — corresponds to "end of text",
            //     always ÷ per LB3.
            var match = true;
            string? failDetails = null;
            var utf16Pos = 0;
            for (var i = 0; i < codepoints.Length; i++)
            {
                var unitLen = codepoints[i] >= 0x10000 ? 2 : 1;
                var lastUnitOfCp = utf16Pos + unitLen - 1;
                var expected = expectedSymbols[i + 1]; // symbol AFTER codepoint i
                var expectedOpp = expected switch
                {
                    '×' => LineBreakOpportunity.Prohibited, // ×
                    '÷' => LineBreakOpportunity.Allowed,    // ÷
                    _ => LineBreakOpportunity.Prohibited,
                };
                // For the last codepoint, the "after" symbol is end-of-text — always ÷, and
                // our impl returns Mandatory there per LB3. Treat both Allowed and Mandatory
                // as "÷" for comparison.
                var actualOpp = actual[lastUnitOfCp];
                bool agrees;
                if (i == codepoints.Length - 1)
                {
                    // Final position: spec says ÷, we report Mandatory. Either Allowed or
                    // Mandatory satisfies the spec ÷.
                    agrees = expectedOpp == LineBreakOpportunity.Allowed
                        ? actualOpp is LineBreakOpportunity.Allowed or LineBreakOpportunity.Mandatory
                        : actualOpp == expectedOpp;
                }
                else
                {
                    // Mid-text mandatory (after BK/CR/LF/NL): expected ÷, our Mandatory is
                    // also acceptable since it implies break-allowed.
                    agrees = expectedOpp == LineBreakOpportunity.Allowed
                        ? actualOpp is LineBreakOpportunity.Allowed or LineBreakOpportunity.Mandatory
                        : actualOpp == LineBreakOpportunity.Prohibited;
                }
                if (!agrees)
                {
                    match = false;
                    failDetails = $"position {i} (after U+{codepoints[i]:X4}): expected {expectedOpp}, actual {actualOpp}";
                    break;
                }
                utf16Pos += unitLen;
            }
            if (match) { passed++; continue; }
            failed++;
            if (failures.Count < sampleFailureLimit)
            {
                failures.Add($"{line} | {failDetails}");
            }
        }
        return (passed, failed, failures);
    }

    private static List<string> SplitWhitespace(string s)
    {
        var list = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            if (i > start) list.Add(s[start..i]);
        }
        return list;
    }

    private static (int[] Codepoints, char[] Symbols) ParseTokens(List<string> tokens)
    {
        // Tokens alternate symbol-codepoint-symbol-codepoint-...-symbol.
        // Symbols are × (×) or ÷ (÷).
        var symbols = new List<char>();
        var codepoints = new List<int>();
        foreach (var t in tokens)
        {
            if (t.Length == 1 && (t[0] == '×' || t[0] == '÷'))
            {
                symbols.Add(t[0]);
            }
            else
            {
                if (int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                {
                    codepoints.Add(cp);
                }
            }
        }
        return (codepoints.ToArray(), symbols.ToArray());
    }
}
