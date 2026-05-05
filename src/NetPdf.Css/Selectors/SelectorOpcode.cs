// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Selectors;

/// <summary>
/// One-byte opcodes for <see cref="SelectorBytecode"/>. The bytecode is read by
/// <see cref="SelectorMatcher"/> in right-to-left order: the first opcode operates on the
/// candidate "key" element, combinators advance the cursor up the DOM, and subsequent match
/// opcodes test the new cursor position. Matching succeeds when the <see cref="End"/> opcode
/// is reached without any match opcode rejecting the cursor.
/// </summary>
/// <remarks>
/// <para>
/// Opcodes that take operands consume them inline. Symbol operands (tag names, class names,
/// id names, attribute names, attribute values) are 2-byte ushort indices into
/// <see cref="SelectorBytecode.Symbols"/>. Sub-selector group operands (used by
/// <c>:not()</c> / <c>:is()</c> / <c>:where()</c> / <c>:has()</c>) are 2-byte ushort indices
/// into <see cref="SelectorBytecode.SubGroups"/>. Numeric operands for <c>:nth-*</c> are
/// two consecutive 4-byte signed ints (a, then b in <c>An+B</c>).
/// </para>
/// <para>
/// Combinator opcodes (<see cref="Descendant"/>, <see cref="Child"/>,
/// <see cref="AdjacentSibling"/>, <see cref="GeneralSibling"/>) advance the cursor before
/// the next match opcode runs. The matcher fails (returns <c>false</c>) when a combinator
/// cannot find an ancestor / sibling that satisfies the remaining match opcodes. Backtracking
/// is needed only for <see cref="Descendant"/> and <see cref="GeneralSibling"/> — child and
/// adjacent-sibling are deterministic single-step moves.
/// </para>
/// </remarks>
internal enum SelectorOpcode : byte
{
    /// <summary>Bytecode terminator. Matcher returns <c>true</c> when reached.</summary>
    End = 0,

    // --- match opcodes (operate on the current cursor element) ---

    /// <summary>Universal selector <c>*</c>. Always matches.</summary>
    MatchUniversal = 1,

    /// <summary>Type selector <c>div</c>. Operand: 2-byte symbol index for the tag name.</summary>
    MatchTag = 2,

    /// <summary>Class selector <c>.foo</c>. Operand: 2-byte symbol index for the class name.</summary>
    MatchClass = 3,

    /// <summary>ID selector <c>#bar</c>. Operand: 2-byte symbol index for the element id.</summary>
    MatchId = 4,

    /// <summary>Attribute presence <c>[attr]</c>. Operand: 2-byte symbol index for the attribute name.</summary>
    MatchAttrExists = 5,

    /// <summary>Attribute value equality <c>[attr=value]</c>. Operands: name index, value index.</summary>
    MatchAttrEquals = 6,

    /// <summary>Whitespace-separated word match <c>[attr~=value]</c>.</summary>
    MatchAttrIncludes = 7,

    /// <summary>Dash match <c>[attr|=value]</c>: equal to <c>value</c> or starts with <c>value-</c>.</summary>
    MatchAttrDashMatch = 8,

    /// <summary>Prefix match <c>[attr^=value]</c>.</summary>
    MatchAttrPrefix = 9,

    /// <summary>Suffix match <c>[attr$=value]</c>.</summary>
    MatchAttrSuffix = 10,

    /// <summary>Substring match <c>[attr*=value]</c>.</summary>
    MatchAttrSubstring = 11,

    // --- structural pseudo-classes (no operands) ---

    /// <summary><c>:first-child</c>.</summary>
    MatchFirstChild = 20,

    /// <summary><c>:last-child</c>.</summary>
    MatchLastChild = 21,

    /// <summary><c>:only-child</c>.</summary>
    MatchOnlyChild = 22,

    /// <summary><c>:first-of-type</c>.</summary>
    MatchFirstOfType = 23,

    /// <summary><c>:last-of-type</c>.</summary>
    MatchLastOfType = 24,

    /// <summary><c>:only-of-type</c>.</summary>
    MatchOnlyOfType = 25,

    /// <summary><c>:empty</c>.</summary>
    MatchEmpty = 26,

    /// <summary><c>:root</c> — the document root element.</summary>
    MatchRoot = 27,

    // --- :nth-* (operands: 4-byte a, 4-byte b in An+B) ---

    /// <summary><c>:nth-child(An+B)</c>. Operands: 4-byte a, 4-byte b.</summary>
    MatchNthChild = 30,

    /// <summary><c>:nth-last-child(An+B)</c>. Operands: 4-byte a, 4-byte b.</summary>
    MatchNthLastChild = 31,

    /// <summary><c>:nth-of-type(An+B)</c>. Operands: 4-byte a, 4-byte b.</summary>
    MatchNthOfType = 32,

    /// <summary><c>:nth-last-of-type(An+B)</c>. Operands: 4-byte a, 4-byte b.</summary>
    MatchNthLastOfType = 33,

    // --- functional pseudo-classes with sub-selectors (operand: 2-byte SubGroups index) ---

    /// <summary><c>:not(X)</c>. Operand: 2-byte sub-group index.</summary>
    MatchNot = 40,

    /// <summary><c>:is(X)</c>. Operand: 2-byte sub-group index.</summary>
    MatchIs = 41,

    /// <summary><c>:where(X)</c>. Operand: 2-byte sub-group index. Specificity always zero
    /// regardless of arguments.</summary>
    MatchWhere = 42,

    /// <summary><c>:has(X)</c>. Operand: 2-byte sub-group index. Parsed but always returns
    /// <c>false</c> at runtime in v1 — diagnostic <c>CSS-HAS-RENDERING-NOT-IMPLEMENTED-001</c>
    /// emitted at cascade time. Roadmap v1.4.</summary>
    MatchHas = 43,

    // --- always-false dynamic state pseudo-classes ---

    /// <summary><c>:hover</c> — always <c>false</c> in v1 (static PDF, no cursor).</summary>
    MatchHover = 50,

    /// <summary><c>:focus</c> — always <c>false</c> in v1.</summary>
    MatchFocus = 51,

    /// <summary><c>:active</c> — always <c>false</c> in v1.</summary>
    MatchActive = 52,

    /// <summary><c>:visited</c> — always <c>false</c> in v1 (privacy/static PDF).</summary>
    MatchVisited = 53,

    // --- link pseudo-classes (true for <a href> / <area href>) ---

    /// <summary><c>:link</c> — true for <c>&lt;a&gt;</c> / <c>&lt;area&gt;</c> elements with
    /// an <c>href</c> attribute. (PDF treats all links as unvisited.)</summary>
    MatchLink = 54,

    /// <summary><c>:any-link</c> — same as <see cref="MatchLink"/> in v1.</summary>
    MatchAnyLink = 55,

    // --- combinators (advance the cursor to the next element to match) ---

    /// <summary>Descendant combinator. Matcher walks ancestors looking for one that
    /// satisfies the remainder of the bytecode; backtracks on failure.</summary>
    Descendant = 64,

    /// <summary>Child combinator <c>&gt;</c>. Cursor moves to the immediate parent;
    /// match fails if no parent or parent doesn't satisfy the remainder.</summary>
    Child = 65,

    /// <summary>Adjacent-sibling combinator <c>+</c>. Cursor moves to the immediately
    /// preceding sibling.</summary>
    AdjacentSibling = 66,

    /// <summary>General-sibling combinator <c>~</c>. Cursor walks earlier siblings looking
    /// for one that satisfies the remainder; backtracks on failure.</summary>
    GeneralSibling = 67,
}
