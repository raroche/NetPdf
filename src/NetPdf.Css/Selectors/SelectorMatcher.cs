// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using AngleSharp.Dom;

namespace NetPdf.Css.Selectors;

/// <summary>
/// Evaluates a compiled <see cref="SelectorBytecode"/> against a candidate
/// <see cref="IElement"/> using right-to-left matching with backtracking on the
/// descendant / general-sibling combinators. Returns <see langword="true"/> when the
/// element is in the selector's match set.
/// </summary>
/// <remarks>
/// <para>
/// AngleSharp's <see cref="IElement"/> stays inside <c>NetPdf.Css</c> per CLAUDE.md's
/// "AngleSharp types must not leak past <c>NetPdf.Css</c>" rule. The cascade resolver
/// (Task 7) is the consumer; it walks the DOM and invokes <see cref="Match"/> per
/// (rule, element) pair.
/// </para>
/// <para>
/// <b>Combinator semantics.</b>
/// </para>
/// <list type="bullet">
///   <item><description><see cref="SelectorOpcode.Descendant"/> — backtracks across all
///   ancestors until one satisfies the remaining bytecode.</description></item>
///   <item><description><see cref="SelectorOpcode.Child"/> — single step to the parent
///   element; fails if absent.</description></item>
///   <item><description><see cref="SelectorOpcode.AdjacentSibling"/> — single step to the
///   immediately preceding element sibling.</description></item>
///   <item><description><see cref="SelectorOpcode.GeneralSibling"/> — backtracks across all
///   earlier siblings until one satisfies the remaining bytecode.</description></item>
/// </list>
/// <para>
/// <b><c>:has()</c> in v1.</b> The matcher always returns <see langword="false"/> for
/// <see cref="SelectorOpcode.MatchHas"/>. This implements the v1 contract: <c>:has()</c>
/// parses but never matches; the cascade resolver emits
/// <c>CSS-HAS-RENDERING-NOT-IMPLEMENTED-001</c> the first time it touches a flagged selector.
/// Roadmap v1.4 will plug a real implementation in here.
/// </para>
/// </remarks>
internal static class SelectorMatcher
{
    /// <summary>Try every alternative of <paramref name="list"/>; return the highest
    /// matching specificity wrapped in <paramref name="matchedSpecificity"/>, or
    /// <see langword="false"/> when no alternative matches.</summary>
    public static bool MatchList(SelectorList list, IElement element, out Specificity matchedSpecificity)
    {
        matchedSpecificity = Specificity.Zero;
        var anyMatch = false;
        foreach (var alt in list.Alternatives)
        {
            if (Match(alt, element))
            {
                if (!anyMatch || alt.Specificity > matchedSpecificity)
                    matchedSpecificity = alt.Specificity;
                anyMatch = true;
            }
        }
        return anyMatch;
    }

    /// <summary>Match a single compiled selector against an element.</summary>
    public static bool Match(SelectorBytecode bytecode, IElement element)
    {
        ArgumentNullException.ThrowIfNull(bytecode);
        ArgumentNullException.ThrowIfNull(element);
        if (bytecode.Code.IsDefaultOrEmpty) return false;
        return Evaluate(bytecode, 0, element);
    }

    /// <summary>Recursively evaluate <paramref name="bytecode"/> starting at byte position
    /// <paramref name="pc"/> with the cursor at <paramref name="cursor"/>. Returns
    /// <see langword="true"/> when the remaining bytecode is satisfied.</summary>
    private static bool Evaluate(SelectorBytecode bytecode, int pc, IElement cursor)
    {
        var code = bytecode.Code;
        while (pc < code.Length)
        {
            var op = (SelectorOpcode)code[pc++];
            switch (op)
            {
                case SelectorOpcode.End:
                    return true;

                case SelectorOpcode.MatchUniversal:
                    break;

                case SelectorOpcode.MatchTag:
                    {
                        var tag = bytecode.Symbols[ReadUInt16(code, ref pc)];
                        if (!string.Equals(cursor.LocalName, tag, StringComparison.OrdinalIgnoreCase))
                            return false;
                        break;
                    }

                case SelectorOpcode.MatchClass:
                    {
                        var cls = bytecode.Symbols[ReadUInt16(code, ref pc)];
                        if (cursor.ClassList is null || !cursor.ClassList.Contains(cls))
                            return false;
                        break;
                    }

                case SelectorOpcode.MatchId:
                    {
                        var id = bytecode.Symbols[ReadUInt16(code, ref pc)];
                        if (!string.Equals(cursor.Id, id, StringComparison.Ordinal))
                            return false;
                        break;
                    }

                case SelectorOpcode.MatchAttrExists:
                    {
                        var name = bytecode.Symbols[ReadUInt16(code, ref pc)];
                        if (!cursor.HasAttribute(name)) return false;
                        break;
                    }

                case SelectorOpcode.MatchAttrEquals:
                case SelectorOpcode.MatchAttrIncludes:
                case SelectorOpcode.MatchAttrDashMatch:
                case SelectorOpcode.MatchAttrPrefix:
                case SelectorOpcode.MatchAttrSuffix:
                case SelectorOpcode.MatchAttrSubstring:
                    {
                        var name = bytecode.Symbols[ReadUInt16(code, ref pc)];
                        var value = bytecode.Symbols[ReadUInt16(code, ref pc)];
                        var actual = cursor.GetAttribute(name);
                        if (actual is null) return false;
                        if (!AttributeMatches(op, actual, value)) return false;
                        break;
                    }

                case SelectorOpcode.MatchFirstChild:
                    if (cursor.PreviousElementSibling is not null) return false;
                    break;

                case SelectorOpcode.MatchLastChild:
                    if (cursor.NextElementSibling is not null) return false;
                    break;

                case SelectorOpcode.MatchOnlyChild:
                    if (cursor.PreviousElementSibling is not null
                        || cursor.NextElementSibling is not null) return false;
                    break;

                case SelectorOpcode.MatchFirstOfType:
                    if (HasEarlierSiblingOfSameType(cursor)) return false;
                    break;

                case SelectorOpcode.MatchLastOfType:
                    if (HasLaterSiblingOfSameType(cursor)) return false;
                    break;

                case SelectorOpcode.MatchOnlyOfType:
                    if (HasEarlierSiblingOfSameType(cursor)
                        || HasLaterSiblingOfSameType(cursor)) return false;
                    break;

                case SelectorOpcode.MatchEmpty:
                    if (!IsEmpty(cursor)) return false;
                    break;

                case SelectorOpcode.MatchRoot:
                    if (cursor.ParentElement is not null) return false;
                    break;

                case SelectorOpcode.MatchNthChild:
                case SelectorOpcode.MatchNthLastChild:
                case SelectorOpcode.MatchNthOfType:
                case SelectorOpcode.MatchNthLastOfType:
                    {
                        var a = ReadInt32(code, ref pc);
                        var b = ReadInt32(code, ref pc);
                        if (!NthMatches(op, cursor, a, b)) return false;
                        break;
                    }

                case SelectorOpcode.MatchNot:
                    {
                        var sub = bytecode.SubGroups[ReadUInt16(code, ref pc)];
                        // :not(A, B, C) means "matches none of A, B, C": fail if ANY matches.
                        foreach (var alt in sub.SubGroups)
                        {
                            if (Evaluate(alt, 0, cursor)) return false;
                        }
                        break;
                    }

                case SelectorOpcode.MatchIs:
                case SelectorOpcode.MatchWhere:
                    {
                        var sub = bytecode.SubGroups[ReadUInt16(code, ref pc)];
                        // :is(A, B) / :where(A, B) match if ANY alternative matches.
                        var any = false;
                        foreach (var alt in sub.SubGroups)
                        {
                            if (Evaluate(alt, 0, cursor)) { any = true; break; }
                        }
                        if (!any) return false;
                        break;
                    }

                case SelectorOpcode.MatchHas:
                    // v1: always false. SubGroup index consumed but not evaluated.
                    _ = ReadUInt16(code, ref pc);
                    return false;

                case SelectorOpcode.MatchHover:
                case SelectorOpcode.MatchFocus:
                case SelectorOpcode.MatchActive:
                case SelectorOpcode.MatchVisited:
                    return false;

                case SelectorOpcode.MatchLink:
                case SelectorOpcode.MatchAnyLink:
                    if (!IsLink(cursor)) return false;
                    break;

                case SelectorOpcode.Descendant:
                    {
                        var ancestor = cursor.ParentElement;
                        while (ancestor is not null)
                        {
                            if (Evaluate(bytecode, pc, ancestor)) return true;
                            ancestor = ancestor.ParentElement;
                        }
                        return false;
                    }

                case SelectorOpcode.Child:
                    {
                        var parent = cursor.ParentElement;
                        if (parent is null) return false;
                        cursor = parent;
                        break;
                    }

                case SelectorOpcode.AdjacentSibling:
                    {
                        var prev = cursor.PreviousElementSibling;
                        if (prev is null) return false;
                        cursor = prev;
                        break;
                    }

                case SelectorOpcode.GeneralSibling:
                    {
                        var prev = cursor.PreviousElementSibling;
                        while (prev is not null)
                        {
                            if (Evaluate(bytecode, pc, prev)) return true;
                            prev = prev.PreviousElementSibling;
                        }
                        return false;
                    }

                default:
                    // Unknown opcode — defensive return false rather than throw, so the
                    // cascade keeps loading the rest of the rules.
                    return false;
            }
        }
        return true;
    }

    // ----- attribute operator helpers -----

    private static bool AttributeMatches(SelectorOpcode op, string actual, string expected) => op switch
    {
        SelectorOpcode.MatchAttrEquals => string.Equals(actual, expected, StringComparison.Ordinal),
        SelectorOpcode.MatchAttrIncludes => IncludesWord(actual, expected),
        SelectorOpcode.MatchAttrDashMatch =>
            string.Equals(actual, expected, StringComparison.Ordinal)
            || actual.StartsWith(expected + "-", StringComparison.Ordinal),
        SelectorOpcode.MatchAttrPrefix =>
            expected.Length > 0 && actual.StartsWith(expected, StringComparison.Ordinal),
        SelectorOpcode.MatchAttrSuffix =>
            expected.Length > 0 && actual.EndsWith(expected, StringComparison.Ordinal),
        SelectorOpcode.MatchAttrSubstring =>
            expected.Length > 0 && actual.Contains(expected, StringComparison.Ordinal),
        _ => false,
    };

    private static bool IncludesWord(string actual, string expected)
    {
        // [attr~=value] — value is a whitespace-separated word in actual. Empty / whitespace-
        // containing expected never matches per spec.
        if (string.IsNullOrEmpty(expected)) return false;
        foreach (var c in expected)
        {
            if (char.IsWhiteSpace(c)) return false;
        }
        var start = 0;
        for (var i = 0; i <= actual.Length; i++)
        {
            if (i == actual.Length || char.IsWhiteSpace(actual[i]))
            {
                var len = i - start;
                if (len == expected.Length
                    && string.CompareOrdinal(actual, start, expected, 0, len) == 0)
                    return true;
                start = i + 1;
            }
        }
        return false;
    }

    // ----- structural helpers -----

    private static bool HasEarlierSiblingOfSameType(IElement cursor)
    {
        var prev = cursor.PreviousElementSibling;
        while (prev is not null)
        {
            if (string.Equals(prev.LocalName, cursor.LocalName, StringComparison.OrdinalIgnoreCase))
                return true;
            prev = prev.PreviousElementSibling;
        }
        return false;
    }

    private static bool HasLaterSiblingOfSameType(IElement cursor)
    {
        var next = cursor.NextElementSibling;
        while (next is not null)
        {
            if (string.Equals(next.LocalName, cursor.LocalName, StringComparison.OrdinalIgnoreCase))
                return true;
            next = next.NextElementSibling;
        }
        return false;
    }

    private static bool IsEmpty(IElement cursor)
    {
        // CSS Selectors L4 §6.6.4: empty means no children, where children is text nodes
        // (other than whitespace... actually no, ANY text node counts) and element nodes.
        // Per spec: "no children at all (excluding... well, no — including text)."
        // The L4 definition: matches when the element has no children. Comments, processing
        // instructions, and whitespace-only text DO NOT disqualify. But any non-whitespace
        // text node or any element child does.
        foreach (var node in cursor.ChildNodes)
        {
            switch (node.NodeType)
            {
                case NodeType.Element:
                    return false;
                case NodeType.Text:
                    if (!string.IsNullOrWhiteSpace(node.TextContent)) return false;
                    break;
            }
        }
        return true;
    }

    private static bool IsLink(IElement cursor)
    {
        // :link / :any-link: <a> or <area> with an href attribute.
        if (!string.Equals(cursor.LocalName, "a", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cursor.LocalName, "area", StringComparison.OrdinalIgnoreCase))
            return false;
        return cursor.HasAttribute("href");
    }

    // ----- :nth-* helpers -----

    private static bool NthMatches(SelectorOpcode op, IElement cursor, int a, int b)
    {
        var index = op switch
        {
            SelectorOpcode.MatchNthChild => IndexAmongSiblings(cursor, fromEnd: false, ofType: false),
            SelectorOpcode.MatchNthLastChild => IndexAmongSiblings(cursor, fromEnd: true, ofType: false),
            SelectorOpcode.MatchNthOfType => IndexAmongSiblings(cursor, fromEnd: false, ofType: true),
            SelectorOpcode.MatchNthLastOfType => IndexAmongSiblings(cursor, fromEnd: true, ofType: true),
            _ => 0,
        };
        if (index <= 0) return false;
        // An+B: matches when (index - b) / a is a non-negative integer. Spec language:
        //   if a > 0: index - b >= 0 and (index - b) % a == 0
        //   if a == 0: index == b
        //   if a < 0: index - b <= 0 and (b - index) % (-a) == 0
        if (a == 0) return index == b;
        var diff = index - b;
        if (a > 0) return diff >= 0 && diff % a == 0;
        // a < 0
        return diff <= 0 && (-diff) % (-a) == 0;
    }

    private static int IndexAmongSiblings(IElement cursor, bool fromEnd, bool ofType)
    {
        var parent = cursor.ParentElement;
        if (parent is null) return 1; // CSS Selectors L4 §6.6.5.2: the root counts at index 1.

        var index = 1;
        var sibling = fromEnd ? cursor.NextElementSibling : cursor.PreviousElementSibling;
        while (sibling is not null)
        {
            if (!ofType
                || string.Equals(sibling.LocalName, cursor.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }
            sibling = fromEnd ? sibling.NextElementSibling : sibling.PreviousElementSibling;
        }
        return index;
    }

    // ----- byte readers -----

    private static ushort ReadUInt16(System.Collections.Immutable.ImmutableArray<byte> code, ref int pc)
    {
        var value = (ushort)((int)code[pc] | ((int)code[pc + 1] << 8));
        pc += 2;
        return value;
    }

    private static int ReadInt32(System.Collections.Immutable.ImmutableArray<byte> code, ref int pc)
    {
        var value = (int)code[pc]
            | ((int)code[pc + 1] << 8)
            | ((int)code[pc + 2] << 16)
            | ((int)code[pc + 3] << 24);
        pc += 4;
        return value;
    }
}
