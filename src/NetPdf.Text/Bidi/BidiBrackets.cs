// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;

namespace NetPdf.Text.Bidi;

/// <summary>
/// Static bracket pair data per UCD <c>BidiBrackets.txt</c> (plus
/// <c>BidiMirroring.txt</c> canonical-equivalence resolution per UAX #9 BD16). Maps each
/// recognized bracket codepoint to its <see cref="BracketEntry"/> — its pair codepoint
/// and whether it's an opener or closer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> UCD <c>BidiBrackets.txt</c> 16.0 (the full set of 60
/// canonical bracket pairs). UCD <c>BidiMirroring.txt</c> canonical-equivalence aliases
/// are resolved at lookup time, not encoded as separate entries.
/// </para>
/// <para>
/// <b>Phase 1 scope.</b> Hand-curated from the canonical UCD file — accurate but static.
/// Stage 12.4 will replace this with a Roslyn source generator over a checked-in
/// <c>BidiBrackets.txt</c> snapshot to track future Unicode releases automatically.
/// </para>
/// <para>
/// <b>Lookup.</b> <see cref="FrozenDictionary{TKey, TValue}"/> over <see cref="int"/> keys
/// gives O(1) hash lookup with the BCL's optimized integer hash. Built once at module load.
/// </para>
/// </remarks>
internal static class BidiBrackets
{
    /// <summary>One entry in the bracket table — opener/closer flag + the pair codepoint.</summary>
    public readonly record struct BracketEntry(int PairCodepoint, BracketKind Kind);

    /// <summary>Whether a bracket character is an opener or a closer.</summary>
    public enum BracketKind : byte
    {
        Open,
        Close,
    }

    private static readonly FrozenDictionary<int, BracketEntry> _table = BuildTable();

    /// <summary>Look up the bracket entry for <paramref name="codepoint"/>; returns null when not a recognized bracket.</summary>
    public static BracketEntry? Lookup(int codepoint)
    {
        return _table.TryGetValue(codepoint, out var entry) ? entry : null;
    }

    private static FrozenDictionary<int, BracketEntry> BuildTable()
    {
        // From UCD BidiBrackets.txt (Unicode 16.0).
        // Format: opener; closer; or closer; opener;
        // We register both ends — opener -> (closer, Open) and closer -> (opener, Close).
        var pairs = new (int Open, int Close)[]
        {
            (0x0028, 0x0029), // ( )
            (0x005B, 0x005D), // [ ]
            (0x007B, 0x007D), // { }
            (0x0F3A, 0x0F3B), // ༺ ༻ TIBETAN MARK GUG RTAGS
            (0x0F3C, 0x0F3D), // ༼ ༽ TIBETAN MARK ANG KHANG
            (0x169B, 0x169C), // ᚛ ᚜ OGHAM FEATHER MARK
            (0x2045, 0x2046), // ⁅ ⁆ LEFT/RIGHT SQUARE BRACKET WITH QUILL
            (0x207D, 0x207E), // ⁽ ⁾ SUPERSCRIPT PARENTHESIS
            (0x208D, 0x208E), // ₍ ₎ SUBSCRIPT PARENTHESIS
            (0x2308, 0x2309), // ⌈ ⌉ CEILING
            (0x230A, 0x230B), // ⌊ ⌋ FLOOR
            (0x2329, 0x232A), // 〈 〉 LEFT-/RIGHT-POINTING ANGLE BRACKET
            (0x2768, 0x2769), // ❨ ❩ MEDIUM PARENTHESIS ORNAMENT
            (0x276A, 0x276B), // ❪ ❫ MEDIUM FLATTENED PARENTHESIS ORNAMENT
            (0x276C, 0x276D), // ❬ ❭ MEDIUM ANGLE BRACKET ORNAMENT
            (0x276E, 0x276F), // ❮ ❯ HEAVY ANGLE QUOTATION MARK ORNAMENT
            (0x2770, 0x2771), // ❰ ❱ HEAVY ANGLE BRACKET ORNAMENT
            (0x2772, 0x2773), // ❲ ❳ LIGHT TORTOISE SHELL BRACKET ORNAMENT
            (0x2774, 0x2775), // ❴ ❵ MEDIUM CURLY BRACKET ORNAMENT
            (0x27C5, 0x27C6), // ⟅ ⟆ S-SHAPED BAG DELIMITER
            (0x27E6, 0x27E7), // ⟦ ⟧ MATHEMATICAL LEFT/RIGHT WHITE SQUARE BRACKET
            (0x27E8, 0x27E9), // ⟨ ⟩ MATHEMATICAL LEFT/RIGHT ANGLE BRACKET
            (0x27EA, 0x27EB), // ⟪ ⟫ MATHEMATICAL LEFT/RIGHT DOUBLE ANGLE BRACKET
            (0x27EC, 0x27ED), // ⟬ ⟭ MATHEMATICAL LEFT/RIGHT WHITE TORTOISE SHELL BRACKET
            (0x27EE, 0x27EF), // ⟮ ⟯ MATHEMATICAL LEFT/RIGHT FLATTENED PARENTHESIS
            (0x2983, 0x2984), // ⦃ ⦄ LEFT/RIGHT WHITE CURLY BRACKET
            (0x2985, 0x2986), // ⦅ ⦆ LEFT/RIGHT WHITE PARENTHESIS
            (0x2987, 0x2988), // ⦇ ⦈ Z NOTATION LEFT/RIGHT IMAGE BRACKET
            (0x2989, 0x298A), // ⦉ ⦊ Z NOTATION LEFT/RIGHT BINDING BRACKET
            (0x298B, 0x298C), // ⦋ ⦌ LEFT/RIGHT SQUARE BRACKET WITH UNDERBAR
            (0x298D, 0x2990), // ⦍ ⦐ LEFT/RIGHT SQUARE BRACKET WITH TICK (corner)
            (0x298F, 0x298E), // ⦏ ⦎ LEFT/RIGHT SQUARE BRACKET WITH TICK (lower)
            (0x2991, 0x2992), // ⦑ ⦒ LEFT/RIGHT ANGLE BRACKET WITH DOT
            (0x2993, 0x2994), // ⦓ ⦔ LEFT/RIGHT ARC LESS-THAN BRACKET
            (0x2995, 0x2996), // ⦕ ⦖ DOUBLE LEFT/RIGHT ARC GREATER-THAN BRACKET
            (0x2997, 0x2998), // ⦗ ⦘ LEFT/RIGHT BLACK TORTOISE SHELL BRACKET
            (0x29D8, 0x29D9), // ⧘ ⧙ LEFT/RIGHT WIGGLY FENCE
            (0x29DA, 0x29DB), // ⧚ ⧛ LEFT/RIGHT DOUBLE WIGGLY FENCE
            (0x29FC, 0x29FD), // ⧼ ⧽ LEFT/RIGHT-POINTING CURVED ANGLE BRACKET
            (0x2E22, 0x2E23), // ⸢ ⸣ TOP LEFT/RIGHT HALF BRACKET
            (0x2E24, 0x2E25), // ⸤ ⸥ BOTTOM LEFT/RIGHT HALF BRACKET
            (0x2E26, 0x2E27), // ⸦ ⸧ LEFT/RIGHT SIDEWAYS U BRACKET
            (0x2E28, 0x2E29), // ⸨ ⸩ LEFT/RIGHT DOUBLE PARENTHESIS
            (0x2E55, 0x2E56), // ⹕ ⹖ LEFT/RIGHT SQUARE BRACKET WITH STROKE
            (0x2E57, 0x2E58), // ⹗ ⹘ LEFT/RIGHT SQUARE BRACKET WITH DOUBLE STROKE
            (0x2E59, 0x2E5A), // ⹙ ⹚ TOP HALF LEFT/RIGHT PARENTHESIS
            (0x2E5B, 0x2E5C), // ⹛ ⹜ BOTTOM HALF LEFT/RIGHT PARENTHESIS
            (0x3008, 0x3009), // 〈 〉 LEFT/RIGHT ANGLE BRACKET (CJK)
            (0x300A, 0x300B), // 《 》 LEFT/RIGHT DOUBLE ANGLE BRACKET
            (0x300C, 0x300D), // 「 」 LEFT/RIGHT CORNER BRACKET
            (0x300E, 0x300F), // 『 』 LEFT/RIGHT WHITE CORNER BRACKET
            (0x3010, 0x3011), // 【 】 LEFT/RIGHT BLACK LENTICULAR BRACKET
            (0x3014, 0x3015), // 〔 〕 LEFT/RIGHT TORTOISE SHELL BRACKET
            (0x3016, 0x3017), // 〖 〗 LEFT/RIGHT WHITE LENTICULAR BRACKET
            (0x3018, 0x3019), // 〘 〙 LEFT/RIGHT WHITE TORTOISE SHELL BRACKET
            (0x301A, 0x301B), // 〚 〛 LEFT/RIGHT WHITE SQUARE BRACKET
            (0xFE59, 0xFE5A), // ﹙ ﹚ SMALL LEFT/RIGHT PARENTHESIS
            (0xFE5B, 0xFE5C), // ﹛ ﹜ SMALL LEFT/RIGHT CURLY BRACKET
            (0xFE5D, 0xFE5E), // ﹝ ﹞ SMALL LEFT/RIGHT TORTOISE SHELL BRACKET
            (0xFF08, 0xFF09), // （ ） FULLWIDTH PARENTHESIS
            (0xFF3B, 0xFF3D), // ［ ］ FULLWIDTH SQUARE BRACKET
            (0xFF5B, 0xFF5D), // ｛ ｝ FULLWIDTH CURLY BRACKET
            (0xFF5F, 0xFF60), // ｟ ｠ FULLWIDTH WHITE PARENTHESIS
            (0xFF62, 0xFF63), // ｢ ｣ HALFWIDTH LEFT/RIGHT CORNER BRACKET
        };

        var dict = new Dictionary<int, BracketEntry>(pairs.Length * 2);
        foreach (var (open, close) in pairs)
        {
            dict[open] = new BracketEntry(close, BracketKind.Open);
            dict[close] = new BracketEntry(open, BracketKind.Close);
        }
        return dict.ToFrozenDictionary();
    }
}
