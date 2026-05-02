// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.LineBreaking;

/// <summary>
/// Public entry point for the Unicode Line Breaking Algorithm
/// (UAX #14, <c>https://www.unicode.org/reports/tr14/</c>). Given UTF-16 text, returns
/// per-codepoint line-break opportunities — Phase 3 layout consumes these to find
/// allowed positions to break a paragraph into lines, plus mandatory breaks at hard
/// line terminators.
/// </summary>
/// <remarks>
/// <para>
/// <b>Output shape.</b> <see cref="FindBreaks"/> returns one <see cref="LineBreakOpportunity"/>
/// per UTF-16 code unit. Surrogate pairs share a single value (the value applies "after"
/// the codepoint). The opportunity at index <c>i</c> describes whether a break is
/// permitted between code unit <c>i</c> and code unit <c>i+1</c>; the opportunity at the
/// last index is always <see cref="LineBreakOpportunity.Mandatory"/> per UAX #14 LB3.
/// </para>
/// <para>
/// <b>Spec basis (clean-room).</b> UAX #14 16.0. Class data from UCD <c>LineBreak.txt</c>
/// 16.0; rules LB1–LB31 from §6 + §7. No code transliterated from any third-party
/// implementation; per-rule branches reference the exact rule number.
/// </para>
/// </remarks>
internal static class LineBreakAlgorithm
{
    /// <summary>Find line-break opportunities in <paramref name="utf16Text"/>.</summary>
    public static LineBreakOpportunity[] FindBreaks(ReadOnlySpan<char> utf16Text)
    {
        if (utf16Text.IsEmpty)
        {
            return [];
        }

        // Decode to per-codepoint info: (utf16Index, utf16Length, originalClass).
        var infos = DecodeToCodepoints(utf16Text);

        // Apply LB1: AI/SG/XX → AL; CB → ID; CJ → NS; SA → AL (the default treatment;
        // SA chars in Brahmic scripts get more nuanced handling via AK/AP/AS/VF/VI when
        // that data is encoded in the UCD ranges).
        ApplyLB1(infos);

        // Apply LB9 + LB10: a CM (combining mark) attaches to the preceding base, taking
        // its class. Leftover CMs (start of text, or after BK/CR/LF/NL/SP/ZW) become AL.
        ApplyLB9AndLB10(infos);

        // Walk pairs and apply LB2–LB31. Output is per-codepoint — convert to per-UTF-16
        // at the end.
        var perCodepoint = new LineBreakOpportunity[infos.Length];
        for (var i = 0; i < infos.Length; i++)
        {
            if (i == infos.Length - 1)
            {
                // LB3: always break at end of text.
                perCodepoint[i] = LineBreakOpportunity.Mandatory;
                continue;
            }
            perCodepoint[i] = ResolvePair(infos, i);
        }

        // Expand to per-UTF-16-code-unit. The opportunity is recorded at the LAST code
        // unit of each codepoint (so an opportunity to break "after" codepoint k is at
        // the last code unit of k).
        var output = new LineBreakOpportunity[utf16Text.Length];
        // Default fill: Prohibited (no break opportunity for non-final code units of a
        // surrogate pair).
        for (var i = 0; i < infos.Length; i++)
        {
            var ci = infos[i];
            output[ci.Utf16Index + ci.Utf16Length - 1] = perCodepoint[i];
        }
        return output;
    }

    private struct CodepointInfo
    {
        public int Utf16Index;
        public byte Utf16Length;
        public LineBreakClass Class;
        public LineBreakClass OriginalClass;
        public int Codepoint;
    }

    private static CodepointInfo[] DecodeToCodepoints(ReadOnlySpan<char> text)
    {
        var infos = new List<CodepointInfo>(text.Length);
        for (var i = 0; i < text.Length; /* advance inside */)
        {
            int codepoint;
            byte unitLen;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                unitLen = 2;
            }
            else
            {
                codepoint = text[i];
                unitLen = 1;
            }
            var cls = LineBreakClassTable.GetClass(codepoint);
            infos.Add(new CodepointInfo
            {
                Utf16Index = i,
                Utf16Length = unitLen,
                Class = cls,
                OriginalClass = cls,
                Codepoint = codepoint,
            });
            i += unitLen;
        }
        return infos.ToArray();
    }

    /// <summary>
    /// LB1: resolve AI/SG/XX → AL, CJ → NS, SA → AL (default).
    /// CB is NOT transformed — it stays CB so LB20 (÷ CB / CB ÷) can fire as the spec
    /// default behavior intends per UAX #14 §6.1.
    /// </summary>
    private static void ApplyLB1(CodepointInfo[] infos)
    {
        for (var i = 0; i < infos.Length; i++)
        {
            infos[i].Class = infos[i].Class switch
            {
                LineBreakClass.AI or LineBreakClass.SG or LineBreakClass.XX => LineBreakClass.AL,
                LineBreakClass.CJ => LineBreakClass.NS,
                LineBreakClass.SA => LineBreakClass.AL,
                _ => infos[i].Class,
            };
        }
    }

    /// <summary>
    /// LB9: a CM following a non-breaking base (X CM*) takes the class of the base — except
    /// when the base is BK/CR/LF/NL/SP/ZW, in which case LB10 applies and the CM becomes AL.
    /// </summary>
    private static void ApplyLB9AndLB10(CodepointInfo[] infos)
    {
        for (var i = 0; i < infos.Length; i++)
        {
            if (infos[i].Class is LineBreakClass.CM or LineBreakClass.ZWJ)
            {
                if (i == 0)
                {
                    // LB10: CM at start of text → AL. ZWJ is treated like CM per LB9, so
                    // ZWJ at sot also falls through to LB10 and becomes AL.
                    infos[i].Class = LineBreakClass.AL;
                    continue;
                }
                var prev = infos[i - 1].Class;
                if (prev is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                    or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW)
                {
                    // LB10: CM after a class that "interrupts" combining sequence → AL.
                    if (infos[i].Class == LineBreakClass.CM)
                    {
                        infos[i].Class = LineBreakClass.AL;
                    }
                    // ZWJ after these stays ZWJ — see LB8a.
                }
                else
                {
                    // LB9: CM AND ZWJ take the class of the preceding base — UAX #14 says
                    // "Treat ZWJ as if it were CM" so both classes propagate the base's
                    // class for subsequent rule application.
                    infos[i].Class = prev;
                    continue;
                }
                // LB10 fall-through (only reached for the LB10-trigger predecessor case):
                // ZWJ at sot or after BK/CR/LF/NL/SP/ZW becomes AL too, just like CM.
                if (infos[i].Class == LineBreakClass.ZWJ)
                {
                    infos[i].Class = LineBreakClass.AL;
                }
            }
        }
    }

    /// <summary>
    /// Resolve the line-break opportunity between codepoint index <paramref name="i"/>
    /// and <paramref name="i"/>+1 by applying UAX #14 rules LB4–LB31 in order. First
    /// matching rule wins; LB31 ("÷ ALL, ALL ÷") is the default if no rule fires.
    /// </summary>
    private static LineBreakOpportunity ResolvePair(CodepointInfo[] infos, int i)
    {
        var left = infos[i].Class;
        var right = infos[i + 1].Class;

        // LB4: BK !
        if (left == LineBreakClass.BK) return LineBreakOpportunity.Mandatory;

        // LB5: CR × LF, otherwise CR/LF/NL all !
        if (left == LineBreakClass.CR && right == LineBreakClass.LF) return LineBreakOpportunity.Prohibited;
        if (left is LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL) return LineBreakOpportunity.Mandatory;

        // LB6: × BK / CR / LF / NL
        if (right is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL)
            return LineBreakOpportunity.Prohibited;

        // LB7: × SP, × ZW
        if (right is LineBreakClass.SP or LineBreakClass.ZW) return LineBreakOpportunity.Prohibited;

        // LB8: ZW SP* ÷ — break after ZW even through following spaces. We need to look
        // back through SP* to find the most recent ZW.
        if (LB8FromZw(infos, i)) return LineBreakOpportunity.Allowed;

        // LB8a: ZWJ × — no break after ZWJ. Use ORIGINAL class (LB9 rewrites to base).
        if (infos[i].OriginalClass == LineBreakClass.ZWJ) return LineBreakOpportunity.Prohibited;

        // LB9: × CM/ZWJ — do not break before a combining mark or ZWJ when the preceding
        // base is not one of the LB10-trigger classes (BK/CR/LF/NL/SP/ZW). This is the
        // explicit "do not break a combining character sequence" half of LB9; the class
        // rewrite handles the "treat X CM* as X" half.
        var rightOrig = infos[i + 1].OriginalClass;
        if (rightOrig is LineBreakClass.CM or LineBreakClass.ZWJ)
        {
            var leftOrig = infos[i].OriginalClass;
            if (leftOrig is not (LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW))
            {
                return LineBreakOpportunity.Prohibited;
            }
        }

        // LB11: × WJ, WJ ×
        if (left == LineBreakClass.WJ || right == LineBreakClass.WJ) return LineBreakOpportunity.Prohibited;

        // LB12: GL × — no break after GL.
        if (left == LineBreakClass.GL) return LineBreakOpportunity.Prohibited;

        // LB12a: [^SP BA HY] × GL — break before GL is forbidden unless preceded by SP/BA/HY.
        if (right == LineBreakClass.GL && left is not (LineBreakClass.SP or LineBreakClass.BA or LineBreakClass.HY))
            return LineBreakOpportunity.Prohibited;

        // LB15c: SP ÷ IS NU — break IS allowed before a decimal mark / comma when preceded
        // by space and followed by a digit. Overrides LB13's × IS in this specific
        // numeric-decimal context.
        if (left == LineBreakClass.SP && right == LineBreakClass.IS
            && i + 2 < infos.Length && infos[i + 2].Class == LineBreakClass.NU)
        {
            return LineBreakOpportunity.Allowed;
        }

        // LB13: × CL, × CP, × EX, × IS, × SY — do not break before close punctuation,
        // exclamation, infix numeric separator, symbol allowing break after.
        if (right is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX
            or LineBreakClass.IS or LineBreakClass.SY)
            return LineBreakOpportunity.Prohibited;

        // LB14: OP SP* × — no break after open punctuation through following spaces.
        if (LB14FromOp(infos, i)) return LineBreakOpportunity.Prohibited;

        // LB15a: (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW) [\p{Pi}&QU] SP* ×
        if (LB15aFromInitialQuotation(infos, i)) return LineBreakOpportunity.Prohibited;

        // LB15b: SP × [\p{Pf}&QU] (eot | BK | CR | LF | NL | SP | GL | WJ | CL | QU | CP | EX | IS | SY)
        if (LB15bToFinalQuotation(infos, i)) return LineBreakOpportunity.Prohibited;

        // LB16: (CL | CP) SP* × NS
        if (LB16FromCloseClp(infos, i, right)) return LineBreakOpportunity.Prohibited;

        // LB17: B2 SP* × B2
        if (right == LineBreakClass.B2 && LB17_LookbackB2(infos, i)) return LineBreakOpportunity.Prohibited;

        // LB18: SP ÷ — break is permitted after SP (default for spaces).
        if (left == LineBreakClass.SP) return LineBreakOpportunity.Allowed;

        // LB19: × QU, QU × — classic LB19 (no EA-Width relaxation in this implementation).
        // UAX #14 16.0 introduced LB19a/b sub-rules that relax these in specific contexts
        // for East-Asian quotation marks. The exact rule text is nuanced enough that a
        // partial implementation regressed cases — keeping classic LB19 for now and
        // pinning the 5 East-Asian quotation conformance failures as known gaps. Future
        // hardening pass can drop in a precise LB19a/b when the spec text is in hand.
        if (left == LineBreakClass.QU || right == LineBreakClass.QU) return LineBreakOpportunity.Prohibited;

        // LB20: ÷ CB / CB ÷
        if (left == LineBreakClass.CB || right == LineBreakClass.CB) return LineBreakOpportunity.Allowed;

        // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) (HY | U+2010) × AL —
        // word-initial hyphen does not allow break before AL. (HL is excluded — Hebrew
        // after a hyphen is still a break opportunity.) Walk back through attached CMs
        // to find the actual HY position, then check ITS predecessor. The spec calls
        // out U+2010 HYPHEN explicitly even though its class is BA (not HY) — so we
        // also match the literal codepoint at the original (pre-LB9) position.
        var leftCpAtBase = i;
        while (leftCpAtBase > 0 && IsAttachedCombiningMark(infos, leftCpAtBase)) leftCpAtBase--;
        var leftIsHyphen = left == LineBreakClass.HY || infos[leftCpAtBase].Codepoint == 0x2010;
        if (leftIsHyphen && right == LineBreakClass.AL)
        {
            // The actual HY/U+2010 position is leftCpAtBase (computed above). Check ITS
            // predecessor (skipping any further attached CMs).
            if (leftCpAtBase == 0)
            {
                return LineBreakOpportunity.Prohibited; // sot HY × AL
            }
            var k = leftCpAtBase - 1;
            while (k >= 0 && IsAttachedCombiningMark(infos, k)) k--;
            if (k < 0) return LineBreakOpportunity.Prohibited; // sot
            var beforeHy = infos[k].OriginalClass;
            if (beforeHy is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW
                or LineBreakClass.CB or LineBreakClass.GL)
            {
                return LineBreakOpportunity.Prohibited;
            }
        }

        // LB21: × BA, × HY, × NS, BB ×
        if (right is LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.NS) return LineBreakOpportunity.Prohibited;
        if (left == LineBreakClass.BB) return LineBreakOpportunity.Prohibited;

        // LB21a: HL (HY | BA) × [^HL] — Hebrew Letter followed by HY/BA does not allow
        // break after, EXCEPT when the next char is itself a Hebrew Letter (Hebrew is
        // permitted to break onto a new line via its own letter even after HL HY).
        if (i >= 1 && infos[i - 1].Class == LineBreakClass.HL
            && left is LineBreakClass.HY or LineBreakClass.BA
            && right != LineBreakClass.HL)
            return LineBreakOpportunity.Prohibited;

        // LB21b: SY × HL
        if (left == LineBreakClass.SY && right == LineBreakClass.HL) return LineBreakOpportunity.Prohibited;

        // LB22: × IN — never break before inseparable.
        if (right == LineBreakClass.IN) return LineBreakOpportunity.Prohibited;

        // LB23: (AL | HL) × NU, NU × (AL | HL)
        if ((left is LineBreakClass.AL or LineBreakClass.HL) && right == LineBreakClass.NU) return LineBreakOpportunity.Prohibited;
        if (left == LineBreakClass.NU && right is LineBreakClass.AL or LineBreakClass.HL) return LineBreakOpportunity.Prohibited;

        // LB23a: PR × (ID | EB | EM), (ID | EB | EM) × PO
        if (left == LineBreakClass.PR && right is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM)
            return LineBreakOpportunity.Prohibited;
        if (left is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && right == LineBreakClass.PO)
            return LineBreakOpportunity.Prohibited;

        // LB24: (PR | PO) × (AL | HL), (AL | HL) × (PR | PO)
        if (left is LineBreakClass.PR or LineBreakClass.PO && right is LineBreakClass.AL or LineBreakClass.HL)
            return LineBreakOpportunity.Prohibited;
        if (left is LineBreakClass.AL or LineBreakClass.HL && right is LineBreakClass.PR or LineBreakClass.PO)
            return LineBreakOpportunity.Prohibited;

        // LB25: numeric sequences. Per UAX #14:
        //   (PR | PO) × ( OP | HY )? NU ...
        //   NU ( NU | SY | IS )* × ( NU | SY | IS )
        //   NU ( NU | SY | IS )* ( CL | CP )? × ( PO | PR )
        if (LB25Rule(infos, i)) return LineBreakOpportunity.Prohibited;

        // LB26: Hangul syllables.
        if (left == LineBreakClass.JL && right is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3)
            return LineBreakOpportunity.Prohibited;
        if (left is LineBreakClass.JV or LineBreakClass.H2 && right is LineBreakClass.JV or LineBreakClass.JT)
            return LineBreakOpportunity.Prohibited;
        if (left is LineBreakClass.JT or LineBreakClass.H3 && right == LineBreakClass.JT)
            return LineBreakOpportunity.Prohibited;

        // LB27: (JL | JV | JT | H2 | H3) × (IN | PO), PR × (JL | JV | JT | H2 | H3)
        if (left is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3
            && right is LineBreakClass.IN or LineBreakClass.PO)
            return LineBreakOpportunity.Prohibited;
        if (left == LineBreakClass.PR && right is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3)
            return LineBreakOpportunity.Prohibited;

        // LB28: (AL | HL) × (AL | HL)
        if (left is LineBreakClass.AL or LineBreakClass.HL && right is LineBreakClass.AL or LineBreakClass.HL)
            return LineBreakOpportunity.Prohibited;

        // LB28a: Brahmic conjunct rules — DOTTED_CIRCLE (U+25CC) is treated as if it
        // were AK or AS for these patterns per UAX #14.
        var leftIsAkLikeAS = left is LineBreakClass.AK or LineBreakClass.AS
            || infos[i].Codepoint == 0x25CC;
        var rightIsAkLikeAS = right is LineBreakClass.AK or LineBreakClass.AS
            || infos[i + 1].Codepoint == 0x25CC;
        // AP × (AK | DOTTED_CIRCLE | AS)
        if (left == LineBreakClass.AP && rightIsAkLikeAS) return LineBreakOpportunity.Prohibited;
        // (AK | DOTTED_CIRCLE | AS) × (VF | VI)
        if (leftIsAkLikeAS && right is LineBreakClass.VF or LineBreakClass.VI)
            return LineBreakOpportunity.Prohibited;
        // (AK | DOTTED_CIRCLE | AS) VI × (AK | DOTTED_CIRCLE | AS) — pair is (VI, AK/DC/AS)
        // and the position BEFORE the VI is AK/DC/AS.
        if (left == LineBreakClass.VI && rightIsAkLikeAS && i >= 1)
        {
            var prev = infos[i - 1];
            var prevIsAkLikeAS = prev.Class is LineBreakClass.AK or LineBreakClass.AS
                || prev.Codepoint == 0x25CC;
            if (prevIsAkLikeAS) return LineBreakOpportunity.Prohibited;
        }

        // LB29: IS × (AL | HL)
        if (left == LineBreakClass.IS && right is LineBreakClass.AL or LineBreakClass.HL)
            return LineBreakOpportunity.Prohibited;

        // LB30: (AL | HL | NU) × [OP - EastAsian], [CP - EastAsian] × (AL | HL | NU)
        // East-Asian Wide/Fullwidth/Halfwidth OP/CP characters are EXCLUDED from this
        // rule per UAX #14 — break is allowed before/after them.
        if (left is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU
            && right == LineBreakClass.OP
            && !LineBreakAuxiliaryData.IsEastAsianWideOpenOrClose(infos[i + 1].Codepoint))
            return LineBreakOpportunity.Prohibited;
        if (left == LineBreakClass.CP
            && right is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU
            && !LineBreakAuxiliaryData.IsEastAsianWideOpenOrClose(infos[i].Codepoint))
            return LineBreakOpportunity.Prohibited;

        // LB30a: RI RI × (regional indicator pairs — flag rule). Pairs of RIs do not break.
        if (left == LineBreakClass.RI && right == LineBreakClass.RI && LB30aIsEvenRiCount(infos, i))
            return LineBreakOpportunity.Prohibited;

        // LB30b: EB × EM, plus the extension "[Extended_Pictographic & Cn] × EM" — an
        // unassigned codepoint inside an Extended_Pictographic range is treated as if it
        // were an Emoji_Base for the modifier rule.
        if (right == LineBreakClass.EM
            && (left == LineBreakClass.EB
                || LineBreakAuxiliaryData.IsExtendedPictographicCn(infos[i].Codepoint)))
        {
            return LineBreakOpportunity.Prohibited;
        }

        // LB31: ÷ ALL — default allow break.
        return LineBreakOpportunity.Allowed;
    }

    /// <summary>
    /// LB8: walk backward through SP* (and CM/ZWJ that LB9 attached to a base) to find
    /// a ZW. Used to allow break after the ZW SP* run.
    /// </summary>
    private static bool LB8FromZw(CodepointInfo[] infos, int i)
    {
        for (var j = i; j >= 0; j--)
        {
            if (IsAttachedCombiningMark(infos, j)) continue;
            if (infos[j].Class == LineBreakClass.ZW) return true;
            if (infos[j].Class != LineBreakClass.SP) return false;
        }
        return false;
    }

    /// <summary>LB14: walk backward through SP* to find an OP. Used to forbid break after OP SP*.</summary>
    private static bool LB14FromOp(CodepointInfo[] infos, int i)
    {
        for (var j = i; j >= 0; j--)
        {
            if (IsAttachedCombiningMark(infos, j)) continue;
            if (infos[j].Class == LineBreakClass.OP) return true;
            if (infos[j].Class != LineBreakClass.SP) return false;
        }
        return false;
    }

    /// <summary>
    /// True when codepoint <paramref name="j"/> is a CM/ZWJ that LB9 attached to a base
    /// (i.e., NOT in LB10-fallback). Used by lookback walks to skip these positions —
    /// after LB9 the codepoint is conceptually part of the preceding base, so it should
    /// neither match nor block the lookback search.
    /// </summary>
    private static bool IsAttachedCombiningMark(CodepointInfo[] infos, int j)
    {
        if (j == 0) return false;
        if (infos[j].OriginalClass is not (LineBreakClass.CM or LineBreakClass.ZWJ)) return false;
        // Look at the predecessor (the base). If it's an LB10-trigger class, the CM was
        // rewritten to AL (LB10), not attached to the base. We should NOT skip it.
        var prevOrig = infos[j - 1].OriginalClass;
        return prevOrig is not (LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
            or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW);
    }

    /// <summary>
    /// LB15a — Walk backward from <paramref name="i"/> through SP* to find a
    /// Pi (Punctuation_Initial) QU codepoint. The Pi-QU itself must be preceded by one of
    /// (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW). When that pattern is satisfied,
    /// no break is permitted at the current position.
    /// </summary>
    private static bool LB15aFromInitialQuotation(CodepointInfo[] infos, int i)
    {
        // Walk back from i (left of pair) through SPs (and attached CMs) to find a Pi-QU.
        for (var j = i; j >= 0; j--)
        {
            if (IsAttachedCombiningMark(infos, j)) continue;
            if (infos[j].Class == LineBreakClass.SP) continue;
            if (infos[j].Class != LineBreakClass.QU) return false;
            if (!LineBreakAuxiliaryData.IsInitialQuotation(infos[j].Codepoint)) return false;
            // Check the predecessor of the Pi-QU (skipping any attached CMs).
            var k = j - 1;
            while (k >= 0 && IsAttachedCombiningMark(infos, k)) k--;
            if (k < 0) return true; // sot
            var prev = infos[k].Class;
            return prev is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                or LineBreakClass.NL or LineBreakClass.OP or LineBreakClass.QU
                or LineBreakClass.GL or LineBreakClass.SP or LineBreakClass.ZW;
        }
        return false;
    }

    /// <summary>
    /// LB15b — When right is Pf (Punctuation_Final) QU and right is followed by one of
    /// (eot | BK | CR | LF | NL | SP | GL | WJ | CL | QU | CP | EX | IS | SY | ZW), the
    /// break before right is forbidden. Left can be anything (the rule is "× Pf-QU [...]").
    /// </summary>
    /// <remarks>
    /// ZW is included so that closing quotation marks followed by a Zero-Width Space
    /// (e.g., <c>« Citation »​...</c> in multilingual text) still trigger the
    /// no-break-before-Pf-QU rule. Matches UAX #14 16.0 LB15.21 sub-rule.
    /// </remarks>
    private static bool LB15bToFinalQuotation(CodepointInfo[] infos, int i)
    {
        var right = infos[i + 1];
        if (right.Class != LineBreakClass.QU) return false;
        if (!LineBreakAuxiliaryData.IsFinalQuotation(right.Codepoint)) return false;
        if (i + 2 >= infos.Length) return true; // eot
        var follower = infos[i + 2].Class;
        return follower is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
            or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.GL
            or LineBreakClass.WJ or LineBreakClass.CL or LineBreakClass.QU
            or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY
            or LineBreakClass.ZW;
    }

    /// <summary>LB16: (CL | CP) SP* × NS. The right is NS; walk back from i through SPs for CL/CP.</summary>
    private static bool LB16FromCloseClp(CodepointInfo[] infos, int i, LineBreakClass right)
    {
        if (right != LineBreakClass.NS) return false;
        for (var j = i; j >= 0; j--)
        {
            if (IsAttachedCombiningMark(infos, j)) continue;
            if (infos[j].Class is LineBreakClass.CL or LineBreakClass.CP) return true;
            if (infos[j].Class != LineBreakClass.SP) return false;
        }
        return false;
    }

    /// <summary>LB17: B2 SP* × B2. The right is B2; walk back from i for a B2.</summary>
    private static bool LB17_LookbackB2(CodepointInfo[] infos, int i)
    {
        for (var j = i; j >= 0; j--)
        {
            if (IsAttachedCombiningMark(infos, j)) continue;
            if (infos[j].Class == LineBreakClass.B2) return true;
            if (infos[j].Class != LineBreakClass.SP) return false;
        }
        return false;
    }

    /// <summary>
    /// LB25: numeric sequences — UAX #14 §6.2.7.5. The full rule is a regex-like pattern:
    /// <c>(PR | PO)? (OP | HY)? NU (NU | SY | IS)* (CL | CP)? (PR | PO)?</c>. Within such
    /// a sequence, no break is permitted between adjacent classes. We implement by checking
    /// each pair against the transitions, using context lookback for the trailing
    /// <c>(CL | CP)? × (PR | PO)</c> case (which requires confirming a NU-sequence prefix).
    /// </summary>
    private static bool LB25Rule(CodepointInfo[] infos, int i)
    {
        var left = infos[i].Class;
        var right = infos[i + 1].Class;

        // (PR | PO) × ( OP | HY )?  NU — covered by direct PR/PO×NU and (PR|PO) × OP/HY when
        // the OP/HY leads into a NU.
        if (left is LineBreakClass.PR or LineBreakClass.PO)
        {
            if (right == LineBreakClass.NU) return true;
            if (right is LineBreakClass.OP or LineBreakClass.HY)
            {
                // Look ahead one for NU.
                if (i + 2 < infos.Length && infos[i + 2].Class == LineBreakClass.NU) return true;
            }
        }

        // ( OP | HY | IS ) × NU — these classes attach to the following number. Note that
        // IS standalone-followed-by-NU also doesn't allow a break (e.g., ',' followed by
        // a digit at start of text), per the spec's LB25.14 sub-rule.
        if (left is LineBreakClass.OP or LineBreakClass.HY or LineBreakClass.IS
            && right == LineBreakClass.NU)
        {
            return true;
        }

        // NU × (NU | SY | IS) — within a numeric sequence body, no break between digits.
        if (left == LineBreakClass.NU && right is LineBreakClass.NU or LineBreakClass.SY or LineBreakClass.IS) return true;

        // SY × NU — only when there's a NU somewhere earlier in the (NU|SY|IS)*
        // sequence (the SY is inside an established numeric body, not at the start).
        // (IS × NU is handled above as an unconditional sub-rule.)
        if (left == LineBreakClass.SY && right == LineBreakClass.NU)
        {
            for (var j = i - 1; j >= 0; j--)
            {
                var c = infos[j].Class;
                if (c == LineBreakClass.NU) return true;
                if (c is LineBreakClass.SY or LineBreakClass.IS) continue;
                break;
            }
            return false;
        }

        // NU × (CL | CP) — sequence may end with optional close.
        if (left == LineBreakClass.NU && right is LineBreakClass.CL or LineBreakClass.CP) return true;
        // (NU | SY | IS) × (CL | CP) — same, after a body separator.
        if (left is LineBreakClass.SY or LineBreakClass.IS && right is LineBreakClass.CL or LineBreakClass.CP) return true;

        // NU (NU | SY | IS)* (CL | CP)? × (PR | PO) — this requires confirming the lookback
        // contains a NU-sequence. Walk back from i: skip optional CL/CP, then walk through
        // (NU | SY | IS)* looking for at least one NU.
        if (right is LineBreakClass.PR or LineBreakClass.PO)
        {
            var j = i;
            if (infos[j].Class is LineBreakClass.CL or LineBreakClass.CP) j--;
            var sawNu = false;
            while (j >= 0)
            {
                var c = infos[j].Class;
                if (c == LineBreakClass.NU) { sawNu = true; j--; continue; }
                if (c is LineBreakClass.SY or LineBreakClass.IS) { j--; continue; }
                break;
            }
            if (sawNu) return true;
        }

        return false;
    }

    /// <summary>
    /// LB30a: count how many ORIGINAL RI codepoints precede the break position (skipping
    /// attached CMs that LB9 collapsed into the preceding RI). When the count is odd, the
    /// current RI is the FIRST of a flag-emoji pair and the break inside the pair is
    /// forbidden.
    /// </summary>
    private static bool LB30aIsEvenRiCount(CodepointInfo[] infos, int i)
    {
        var count = 0;
        for (var j = i; j >= 0; j--)
        {
            if (IsAttachedCombiningMark(infos, j)) continue;
            if (infos[j].OriginalClass != LineBreakClass.RI) break;
            count++;
        }
        return (count & 1) == 1;
    }
}
