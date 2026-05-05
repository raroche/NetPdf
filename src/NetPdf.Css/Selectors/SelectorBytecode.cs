// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace NetPdf.Css.Selectors;

/// <summary>
/// A compiled CSS selector — the rightmost simple selector first ("key selector"), followed
/// by combinators and ancestor / sibling matches. <see cref="SelectorMatcher"/> evaluates
/// this against a candidate element. <see cref="SelectorCompiler"/> produces instances.
/// </summary>
/// <remarks>
/// <para>
/// One instance per comma-separated alternative of the original selector text. A rule like
/// <c>.a, .b > .c { … }</c> compiles to two <see cref="SelectorBytecode"/> instances grouped
/// in a <see cref="SelectorList"/>. The cascade resolver (Task 7) holds the <see cref="SelectorList"/>
/// for the rule and walks each alternative independently.
/// </para>
/// <para>
/// <b>Pre-filter tokens</b> — <see cref="RequiredTags"/>, <see cref="RequiredClasses"/>, and
/// <see cref="RequiredIds"/> enumerate the tag / class / id tokens that this selector requires
/// somewhere in its match chain. They feed the per-stylesheet bloom filter
/// (<see cref="SelectorBloomFilter"/>) so the cascade can reject selectors against an element's
/// (tag, class, id) bloom set without invoking the matcher. Tokens inside
/// <c>:not()</c> are excluded — a selector like <c>.a:not(.foo)</c> still matches when the
/// element doesn't have <c>.foo</c>, so requiring <c>.foo</c> in the bloom test would be
/// unsound. Tokens inside <c>:is()</c> / <c>:where()</c> / <c>:has()</c> are also excluded
/// because each of those evaluates a sub-selector list that doesn't anchor the candidate
/// element to a specific token.
/// </para>
/// </remarks>
internal sealed class SelectorBytecode
{
    public SelectorBytecode(
        ImmutableArray<byte> code,
        ImmutableArray<string> symbols,
        ImmutableArray<SelectorBytecode> subGroups,
        Specificity specificity,
        FrozenSet<string> requiredTags,
        FrozenSet<string> requiredClasses,
        FrozenSet<string> requiredIds,
        bool containsHas,
        string sourceText)
    {
        Code = code;
        Symbols = symbols;
        SubGroups = subGroups;
        Specificity = specificity;
        RequiredTags = requiredTags;
        RequiredClasses = requiredClasses;
        RequiredIds = requiredIds;
        ContainsHas = containsHas;
        SourceText = sourceText;
    }

    /// <summary>The opcode + operand byte stream. Walk it via <see cref="OpenReader"/>.</summary>
    public ImmutableArray<byte> Code { get; }

    /// <summary>Interned strings referenced by 2-byte indices in <see cref="Code"/>.</summary>
    public ImmutableArray<string> Symbols { get; }

    /// <summary>Sub-selector lists referenced by <see cref="SelectorOpcode.MatchNot"/> /
    /// <see cref="SelectorOpcode.MatchIs"/> / <see cref="SelectorOpcode.MatchWhere"/> /
    /// <see cref="SelectorOpcode.MatchHas"/>. Each entry is a <see cref="SelectorBytecode"/>
    /// because the operands are themselves selector lists. (Comma-separated alternatives
    /// inside <c>:not(...)</c> are joined as a single sub-selector list — the matcher evaluates
    /// each alternative against the same element.)</summary>
    public ImmutableArray<SelectorBytecode> SubGroups { get; }

    /// <summary>CSS Selectors L4 §17 specificity, computed at compile time.</summary>
    public Specificity Specificity { get; }

    /// <summary>Tags this selector requires somewhere in its match chain (excluding
    /// <c>:not</c>/<c>:is</c>/<c>:where</c>/<c>:has</c> sub-groups). Used by the bloom-filter
    /// pre-filter; never includes the universal selector <c>*</c>.</summary>
    public FrozenSet<string> RequiredTags { get; }

    /// <summary>Classes this selector requires somewhere in its match chain.</summary>
    public FrozenSet<string> RequiredClasses { get; }

    /// <summary>Element ids this selector requires somewhere in its match chain.</summary>
    public FrozenSet<string> RequiredIds { get; }

    /// <summary><see langword="true"/> when the selector contains a <c>:has()</c>
    /// pseudo-class. The matcher returns <c>false</c> for any <see cref="SelectorOpcode.MatchHas"/>
    /// in v1; the cascade resolver (Task 7) emits
    /// <c>CSS-HAS-RENDERING-NOT-IMPLEMENTED-001</c> the first time it encounters a flagged
    /// selector for a given stylesheet.</summary>
    public bool ContainsHas { get; }

    /// <summary>The original (canonicalized) selector text for diagnostics + debugging.</summary>
    public string SourceText { get; }

    /// <summary>Allocate a forward reader over <see cref="Code"/>. Each
    /// <see cref="SelectorMatcher"/> evaluation calls <see cref="OpenReader"/> once per
    /// alternative (or recursive sub-group).</summary>
    public Reader OpenReader() => new(this);

    /// <summary>
    /// Forward-only iterator over <see cref="Code"/>. Reads opcodes one at a time and exposes
    /// strongly-typed accessors for the operand shapes used by each opcode. Designed as a
    /// <see langword="ref struct"/> so it cannot escape to the heap and the matcher allocates
    /// nothing per evaluation.
    /// </summary>
    public ref struct Reader
    {
        private readonly SelectorBytecode _owner;
        private int _pos;

        internal Reader(SelectorBytecode owner)
        {
            _owner = owner;
            _pos = 0;
        }

        /// <summary><see langword="true"/> when no further opcodes remain to read.</summary>
        public readonly bool IsEnd => _pos >= _owner.Code.Length;

        /// <summary>Read and consume the next opcode byte.</summary>
        public SelectorOpcode ReadOpcode() => (SelectorOpcode)_owner.Code[_pos++];

        /// <summary>Read a 2-byte ushort symbol-index operand and return the symbol it points to.</summary>
        public string ReadSymbol()
        {
            var index = ReadUInt16();
            return _owner.Symbols[index];
        }

        /// <summary>Read a 2-byte ushort sub-group index operand and return the referenced
        /// <see cref="SelectorBytecode"/>.</summary>
        public SelectorBytecode ReadSubGroup()
        {
            var index = ReadUInt16();
            return _owner.SubGroups[index];
        }

        /// <summary>Read a signed 32-bit integer operand (used for <c>An+B</c> formulas).</summary>
        public int ReadInt32()
        {
            var value = (int)_owner.Code[_pos]
                | ((int)_owner.Code[_pos + 1] << 8)
                | ((int)_owner.Code[_pos + 2] << 16)
                | ((int)_owner.Code[_pos + 3] << 24);
            _pos += 4;
            return value;
        }

        private ushort ReadUInt16()
        {
            var value = (ushort)((int)_owner.Code[_pos] | ((int)_owner.Code[_pos + 1] << 8));
            _pos += 2;
            return value;
        }
    }
}

/// <summary>
/// The result of compiling a comma-separated selector list (e.g., <c>.a, .b &gt; .c</c>).
/// Each alternative is one <see cref="SelectorBytecode"/>; the cascade resolver tries each
/// independently and uses the highest-specificity successful match.
/// </summary>
internal sealed record SelectorList(
    ImmutableArray<SelectorBytecode> Alternatives,
    string SourceText)
{
    public static SelectorList Empty { get; } = new(
        ImmutableArray<SelectorBytecode>.Empty, string.Empty);

    /// <summary>Maximum specificity across alternatives — used by <c>:is()</c> /
    /// <c>:not()</c> / <c>:has()</c>'s specificity rule.</summary>
    public Specificity MaxSpecificity
    {
        get
        {
            if (Alternatives.IsDefaultOrEmpty) return Specificity.Zero;
            var max = Alternatives[0].Specificity;
            for (var i = 1; i < Alternatives.Length; i++)
            {
                if (Alternatives[i].Specificity > max) max = Alternatives[i].Specificity;
            }
            return max;
        }
    }

    /// <summary><see langword="true"/> when any alternative contains <c>:has()</c>.</summary>
    public bool ContainsHas
    {
        get
        {
            if (Alternatives.IsDefaultOrEmpty) return false;
            foreach (var alt in Alternatives)
            {
                if (alt.ContainsHas) return true;
            }
            return false;
        }
    }
}
