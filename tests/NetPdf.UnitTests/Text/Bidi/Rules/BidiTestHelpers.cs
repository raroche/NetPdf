// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;

namespace NetPdf.UnitTests.Text.Bidi.Rules;

/// <summary>
/// Shared helpers for the bidi rule-pass tests. Many tests want to feed a synthetic
/// sequence of <see cref="BidiClass"/> values to the rule passes without depending on
/// real Unicode codepoints — that lets each rule be exercised in isolation against
/// inputs that the spec's own test vectors use.
/// </summary>
internal static class BidiTestHelpers
{
    /// <summary>
    /// Build a <see cref="BidiCharInfo"/>[] from a sequence of bidi classes. Each entry
    /// stores the given class as both <see cref="BidiCharInfo.OriginalClass"/> and
    /// <see cref="BidiCharInfo.ResolvedClass"/>; <see cref="BidiCharInfo.Utf16Index"/>
    /// is set to the entry's source position with <see cref="BidiCharInfo.Utf16Length"/> = 1.
    /// </summary>
    public static BidiCharInfo[] FromClasses(params BidiClass[] classes)
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
        return infos;
    }

    /// <summary>
    /// Build BidiCharInfo[] from explicit (class, codepoint) pairs — useful for N0 bracket
    /// resolution tests where the codepoint identity matters as well as the class.
    /// </summary>
    public static BidiCharInfo[] FromClassesAndCodepoints(params (BidiClass Class, int Codepoint)[] pairs)
    {
        var infos = new BidiCharInfo[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            infos[i] = new BidiCharInfo
            {
                OriginalClass = pairs[i].Class,
                ResolvedClass = pairs[i].Class,
                Level = 0,
                IsRemovedByX9 = false,
                Utf16Index = i,
                Utf16Length = 1,
                Codepoint = pairs[i].Codepoint,
            };
        }
        return infos;
    }
}
