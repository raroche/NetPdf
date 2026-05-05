// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace NetPdf.Css.Selectors;

/// <summary>
/// Compiles CSS selector text into <see cref="SelectorList"/> + <see cref="SelectorBytecode"/>
/// for evaluation by <see cref="SelectorMatcher"/>. The compiler is a hand-rolled recursive
/// descent parser over the canonicalized selector text we get out of AngleSharp.Css; it does
/// NOT consume AngleSharp's selector tree (clean-room separation: parsing the selector grammar
/// from the published CSS Selectors L4 spec).
/// </summary>
/// <remarks>
/// <para>
/// <b>Compilation order.</b> The parser walks the selector left-to-right (the natural
/// reading order) and accumulates the compound selectors and combinators into intermediate
/// lists. Bytecode is emitted in <i>right-to-left</i> order so the matcher can start at the
/// "key" (rightmost) compound and walk ancestors / siblings as combinators direct.
/// </para>
/// <para>
/// <b>Supported subset.</b> Every selector form listed in the Phase 2 doc:
/// type, universal, class, id, attribute (with all six operators
/// <c>=</c> / <c>~=</c> / <c>|=</c> / <c>^=</c> / <c>$=</c> / <c>*=</c>), descendant /
/// child / adjacent-sibling / general-sibling combinators, structural pseudo-classes
/// (<c>:first-child</c> / <c>:last-child</c> / <c>:only-child</c> / <c>:first-of-type</c> /
/// <c>:last-of-type</c> / <c>:only-of-type</c> / <c>:empty</c> / <c>:root</c>), <c>:nth-*</c>
/// with full <c>An+B</c> formulas plus <c>even</c> / <c>odd</c> shortcuts, <c>:not()</c> /
/// <c>:is()</c> / <c>:where()</c> / <c>:has()</c>, and dynamic-state pseudo-classes
/// (<c>:hover</c> / <c>:focus</c> / <c>:active</c> / <c>:visited</c> / <c>:link</c> /
/// <c>:any-link</c>). Pseudo-elements (<c>::before</c>, <c>::after</c>, …) are accepted at
/// the parser level (they advance the cursor but do not emit a match opcode) — the cascade
/// resolver in Task 7 / pseudo-element materialization in Task 14 use the original selector
/// text to decide which pseudo-element to target, so the matcher only needs to evaluate the
/// non-pseudo-element portion.
/// </para>
/// </remarks>
internal static class SelectorCompiler
{
    /// <summary>Parses <paramref name="selectorText"/> into a <see cref="SelectorList"/>.
    /// Throws <see cref="SelectorParseException"/> on malformed input — the cascade
    /// resolver catches this and emits <c>CSS-PARSE-WARNING-001</c>.</summary>
    public static SelectorList Compile(string selectorText)
    {
        ArgumentNullException.ThrowIfNull(selectorText);
        if (string.IsNullOrWhiteSpace(selectorText)) return SelectorList.Empty;

        var parser = new Parser(selectorText);
        return parser.ParseSelectorList(topLevel: true);
    }

    /// <summary>The recursive-descent parser. A struct so it composes via plain method
    /// calls without per-frame allocations.</summary>
    private struct Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text)
        {
            _text = text;
            _pos = 0;
        }

        internal readonly int Position => _pos;
        internal readonly string Text => _text;

        /// <summary>Parse <c>SelectorList := ComplexSelector ("," ComplexSelector)*</c>.</summary>
        public SelectorList ParseSelectorList(bool topLevel)
        {
            var alts = ImmutableArray.CreateBuilder<SelectorBytecode>();
            SkipWhitespace();
            // Empty selector list — typically when a sub-group has only whitespace inside parens.
            if (IsEnd || (!topLevel && Peek() == ')'))
                return SelectorList.Empty;

            while (true)
            {
                var alt = ParseComplexSelector();
                alts.Add(alt);
                SkipWhitespace();
                if (IsEnd || (!topLevel && Peek() == ')')) break;
                Expect(',');
                SkipWhitespace();
            }
            return new SelectorList(alts.ToImmutable(), _text);
        }

        /// <summary>
        /// Parse <c>ComplexSelector := CompoundSelector (Combinator CompoundSelector)*</c>.
        /// Emits bytecode in right-to-left order: parse left-to-right into an intermediate list,
        /// then reverse + emit. The compound at index 0 is the rightmost ("key") compound.
        /// </summary>
        private SelectorBytecode ParseComplexSelector()
        {
            var startPos = _pos;
            var compounds = new List<CompoundSelector>();
            var combinators = new List<SelectorOpcode>();

            compounds.Add(ParseCompoundSelector());

            while (true)
            {
                SkipWhitespace();
                if (IsEnd) break;
                var c = Peek();
                if (c == ',' || c == ')') break;

                SelectorOpcode combinator;
                if (c == '>') { combinator = SelectorOpcode.Child; _pos++; }
                else if (c == '+') { combinator = SelectorOpcode.AdjacentSibling; _pos++; }
                else if (c == '~') { combinator = SelectorOpcode.GeneralSibling; _pos++; }
                else
                {
                    // No explicit combinator — descendant if there was whitespace, otherwise
                    // we wouldn't have advanced past it. Re-inspect: if we just skipped
                    // whitespace and the next char is the start of a compound selector,
                    // it's a descendant combinator. Detect by checking we consumed any
                    // whitespace (the SkipWhitespace above would have consumed it).
                    if (!IsCompoundStart(c)) break;
                    combinator = SelectorOpcode.Descendant;
                }

                SkipWhitespace();
                if (IsEnd) throw Error("expected compound selector after combinator");
                combinators.Add(combinator);
                compounds.Add(ParseCompoundSelector());
            }

            return EmitBytecode(compounds, combinators, startPos);
        }

        /// <summary>Build the bytecode for one alternative (a chain of compound selectors
        /// separated by combinators). Reverses the parsed left-to-right order so the bytecode
        /// is right-to-left.</summary>
        private SelectorBytecode EmitBytecode(
            List<CompoundSelector> compounds,
            List<SelectorOpcode> combinators,
            int startPos)
        {
            var ctx = new EmitContext();
            // Right-to-left: emit the rightmost compound first.
            for (var i = compounds.Count - 1; i >= 0; i--)
            {
                ctx.EmitCompound(compounds[i]);
                if (i > 0) ctx.EmitOpcode(combinators[i - 1]);
            }
            ctx.EmitOpcode(SelectorOpcode.End);

            var sourceText = _text.Substring(startPos, _pos - startPos).Trim();
            return new SelectorBytecode(
                code: ctx.Code.ToImmutable(),
                symbols: ctx.Symbols.ToImmutable(),
                subGroups: ctx.SubGroups.ToImmutable(),
                specificity: ctx.Specificity,
                requiredTags: ctx.RequiredTags.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                requiredClasses: ctx.RequiredClasses.ToFrozenSet(StringComparer.Ordinal),
                requiredIds: ctx.RequiredIds.ToFrozenSet(StringComparer.Ordinal),
                containsHas: ctx.ContainsHas,
                sourceText: sourceText);
        }

        /// <summary>Parse one compound selector — a sequence of simple selectors with no
        /// whitespace or combinator between them.</summary>
        private CompoundSelector ParseCompoundSelector()
        {
            var compound = new CompoundSelector();
            if (IsEnd) throw Error("expected compound selector");
            // Optional type selector / universal at the start.
            var c = Peek();
            if (c == '*') { compound.Add(SimpleSelector.Universal()); _pos++; }
            else if (IsIdentStart(c)) { compound.Add(SimpleSelector.Type(ReadIdent())); }
            else if (c != '.' && c != '#' && c != '[' && c != ':')
                throw Error($"unexpected character '{c}'");

            // Followed by zero or more class / id / attribute / pseudo selectors.
            while (!IsEnd)
            {
                c = Peek();
                if (c == '.') { _pos++; compound.Add(SimpleSelector.Class(ReadIdent())); }
                else if (c == '#') { _pos++; compound.Add(SimpleSelector.Id(ReadIdent())); }
                else if (c == '[') compound.Add(ParseAttributeSelector());
                else if (c == ':') compound.Add(ParsePseudo());
                else break;
            }

            if (compound.IsEmpty)
                throw Error("compound selector cannot be empty");
            return compound;
        }

        private SimpleSelector ParseAttributeSelector()
        {
            Expect('[');
            SkipWhitespace();
            var name = ReadIdent();
            if (string.IsNullOrEmpty(name)) throw Error("expected attribute name");
            SkipWhitespace();
            if (IsEnd) throw Error("unterminated attribute selector");
            if (Peek() == ']') { _pos++; return SimpleSelector.AttrExists(name); }

            // Operator: =, ~=, |=, ^=, $=, *=
            var op = ReadAttrOperator();
            SkipWhitespace();
            if (IsEnd) throw Error("expected attribute value");
            string value;
            var qc = Peek();
            if (qc == '"' || qc == '\'') value = ReadString(qc);
            else value = ReadIdent();
            if (string.IsNullOrEmpty(value))
                throw Error("expected attribute value after operator");
            SkipWhitespace();
            // Skip case-sensitivity flag (i / s) — not supported in v1; matcher uses the
            // default per spec (case-sensitive on attribute values, except for HTML's
            // well-known case-insensitive attributes which we handle in the matcher itself).
            if (!IsEnd && (Peek() == 'i' || Peek() == 'I' || Peek() == 's' || Peek() == 'S'))
            {
                _pos++;
                SkipWhitespace();
            }
            Expect(']');
            return SimpleSelector.Attr(op, name, value);
        }

        private SelectorOpcode ReadAttrOperator()
        {
            var c = Peek();
            if (c == '=') { _pos++; return SelectorOpcode.MatchAttrEquals; }
            if (_pos + 1 >= _text.Length || _text[_pos + 1] != '=')
                throw Error($"unexpected character '{c}'");
            SelectorOpcode op = c switch
            {
                '~' => SelectorOpcode.MatchAttrIncludes,
                '|' => SelectorOpcode.MatchAttrDashMatch,
                '^' => SelectorOpcode.MatchAttrPrefix,
                '$' => SelectorOpcode.MatchAttrSuffix,
                '*' => SelectorOpcode.MatchAttrSubstring,
                _ => throw Error($"unsupported attribute operator '{c}='"),
            };
            _pos += 2;
            return op;
        }

        private SimpleSelector ParsePseudo()
        {
            Expect(':');
            // Pseudo-element ::name — accept and treat as no-op for matching purposes.
            // (The cascade resolver / pseudo-element materializer reads the source text to
            // decide which pseudo-element to target.)
            if (!IsEnd && Peek() == ':')
            {
                _pos++;
                var pseudoElementName = ReadIdent();
                if (string.IsNullOrEmpty(pseudoElementName))
                    throw Error("expected pseudo-element name");
                // Some pseudo-elements take a function arg (e.g., ::part(name), ::slotted(...));
                // skip the parenthesized body if present.
                if (!IsEnd && Peek() == '(') SkipParens();
                return SimpleSelector.PseudoElement(pseudoElementName);
            }

            var name = ReadIdent();
            if (string.IsNullOrEmpty(name)) throw Error("expected pseudo-class name");

            // Functional pseudo-classes — :nth-*(...), :not(...), :is(...), :where(...), :has(...).
            if (!IsEnd && Peek() == '(')
                return ParseFunctionalPseudo(name);

            // Non-functional pseudo-classes.
            return name.ToLowerInvariant() switch
            {
                "first-child" => SimpleSelector.Pseudo(SelectorOpcode.MatchFirstChild),
                "last-child" => SimpleSelector.Pseudo(SelectorOpcode.MatchLastChild),
                "only-child" => SimpleSelector.Pseudo(SelectorOpcode.MatchOnlyChild),
                "first-of-type" => SimpleSelector.Pseudo(SelectorOpcode.MatchFirstOfType),
                "last-of-type" => SimpleSelector.Pseudo(SelectorOpcode.MatchLastOfType),
                "only-of-type" => SimpleSelector.Pseudo(SelectorOpcode.MatchOnlyOfType),
                "empty" => SimpleSelector.Pseudo(SelectorOpcode.MatchEmpty),
                "root" => SimpleSelector.Pseudo(SelectorOpcode.MatchRoot),
                "hover" => SimpleSelector.Pseudo(SelectorOpcode.MatchHover),
                "focus" => SimpleSelector.Pseudo(SelectorOpcode.MatchFocus),
                "active" => SimpleSelector.Pseudo(SelectorOpcode.MatchActive),
                "visited" => SimpleSelector.Pseudo(SelectorOpcode.MatchVisited),
                "link" => SimpleSelector.Pseudo(SelectorOpcode.MatchLink),
                "any-link" => SimpleSelector.Pseudo(SelectorOpcode.MatchAnyLink),
                _ => throw Error($"unsupported pseudo-class ':{name}'"),
            };
        }

        private SimpleSelector ParseFunctionalPseudo(string name)
        {
            Expect('(');
            SkipWhitespace();
            var lower = name.ToLowerInvariant();
            switch (lower)
            {
                case "nth-child": return ParseNth(SelectorOpcode.MatchNthChild);
                case "nth-last-child": return ParseNth(SelectorOpcode.MatchNthLastChild);
                case "nth-of-type": return ParseNth(SelectorOpcode.MatchNthOfType);
                case "nth-last-of-type": return ParseNth(SelectorOpcode.MatchNthLastOfType);
                case "not": return ParseSubGroup(SelectorOpcode.MatchNot);
                case "is": return ParseSubGroup(SelectorOpcode.MatchIs);
                case "where": return ParseSubGroup(SelectorOpcode.MatchWhere);
                case "has": return ParseSubGroup(SelectorOpcode.MatchHas);
                default: throw Error($"unsupported functional pseudo-class ':{name}()'");
            }
        }

        private SimpleSelector ParseNth(SelectorOpcode op)
        {
            // an+b microsyntax + 'even' / 'odd' shortcuts.
            var (a, b) = ReadAnPlusB();
            SkipWhitespace();
            Expect(')');
            return SimpleSelector.Nth(op, a, b);
        }

        private SimpleSelector ParseSubGroup(SelectorOpcode op)
        {
            var inner = ParseSelectorList(topLevel: false);
            SkipWhitespace();
            Expect(')');
            return SimpleSelector.SubGroup(op, inner);
        }

        private (int a, int b) ReadAnPlusB()
        {
            // Forms: even | odd | <integer> | <An+B> with optional sign + whitespace.
            // Simplifications: we accept the canonical forms; whitespace tolerant per spec.
            SkipWhitespace();
            if (TryReadKeyword("even")) return (2, 0);
            if (TryReadKeyword("odd")) return (2, 1);

            int sign = 1;
            if (!IsEnd && (Peek() == '+' || Peek() == '-'))
            {
                if (Peek() == '-') sign = -1;
                _pos++;
                SkipWhitespace();
            }

            int? leadingNumber = null;
            if (!IsEnd && IsAsciiDigit(Peek()))
                leadingNumber = ReadInteger();

            // Is there an 'n' here? If so, this is the An part.
            if (!IsEnd && (Peek() == 'n' || Peek() == 'N'))
            {
                _pos++;
                int a = sign * (leadingNumber ?? 1);
                int b = 0;
                SkipWhitespace();
                if (!IsEnd && (Peek() == '+' || Peek() == '-'))
                {
                    var bsign = Peek() == '-' ? -1 : 1;
                    _pos++;
                    SkipWhitespace();
                    if (IsEnd || !IsAsciiDigit(Peek()))
                        throw Error("expected digit after sign in An+B formula");
                    b = bsign * ReadInteger();
                }
                return (a, b);
            }

            // No 'n' — it's just a B with no An component.
            if (leadingNumber is null)
                throw Error("expected An+B formula or 'even'/'odd'");
            return (0, sign * leadingNumber.Value);
        }

        private bool TryReadKeyword(string keyword)
        {
            if (_pos + keyword.Length > _text.Length) return false;
            for (var i = 0; i < keyword.Length; i++)
            {
                if (char.ToLowerInvariant(_text[_pos + i]) != keyword[i]) return false;
            }
            // Followed by whitespace / paren — guard against partial-prefix matches.
            var next = _pos + keyword.Length;
            if (next < _text.Length)
            {
                var c = _text[next];
                if (IsIdentContinue(c)) return false;
            }
            _pos += keyword.Length;
            return true;
        }

        private int ReadInteger()
        {
            var start = _pos;
            while (!IsEnd && IsAsciiDigit(Peek())) _pos++;
            var s = _text[start.._pos];
            return int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }

        private string ReadIdent()
        {
            var start = _pos;
            // CSS identifier: -? [_a-zA-Z -￿] [_a-zA-Z0-9 -￿-]*. We accept
            // a single leading hyphen but no double-hyphen escape syntax (which would be a
            // custom-property, not a selector identifier).
            if (!IsEnd && Peek() == '-') _pos++;
            if (IsEnd || !IsIdentStart(Peek())) { _pos = start; return string.Empty; }
            _pos++;
            while (!IsEnd && IsIdentContinue(Peek())) _pos++;
            return _text[start.._pos];
        }

        private string ReadString(char quote)
        {
            Expect(quote);
            var sb = new StringBuilder();
            while (!IsEnd)
            {
                var c = Peek();
                if (c == quote) { _pos++; return sb.ToString(); }
                // Minimal escape handling: \\ \' \" \n. Anything else is taken literally.
                if (c == '\\' && _pos + 1 < _text.Length)
                {
                    _pos++;
                    sb.Append(_text[_pos]);
                    _pos++;
                    continue;
                }
                sb.Append(c);
                _pos++;
            }
            throw Error("unterminated string in attribute selector");
        }

        private void SkipParens()
        {
            // Skip a balanced paren expression (used for unsupported pseudo-elements with
            // function arguments). Quote-aware so '(' inside strings doesn't unbalance us.
            Expect('(');
            int depth = 1;
            while (!IsEnd && depth > 0)
            {
                var c = Peek();
                if (c == '"' || c == '\'') { ReadString(c); continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                _pos++;
            }
        }

        private void SkipWhitespace()
        {
            while (!IsEnd)
            {
                var c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f') _pos++;
                else break;
            }
        }

        private void Expect(char expected)
        {
            if (IsEnd || _text[_pos] != expected)
                throw Error($"expected '{expected}'");
            _pos++;
        }

        private SelectorParseException Error(string reason) =>
            new(_text, _pos, reason);

        private readonly bool IsEnd => _pos >= _text.Length;
        private readonly char Peek() => _text[_pos];

        private static bool IsIdentStart(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c >= 0x80;

        private static bool IsIdentContinue(char c) =>
            IsIdentStart(c) || (c >= '0' && c <= '9') || c == '-';

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsCompoundStart(char c) =>
            IsIdentStart(c) || c == '*' || c == '.' || c == '#' || c == '[' || c == ':';
    }

    // ------------------------------------------------------------------------
    // Intermediate types — built during parsing, then folded into the bytecode.
    // ------------------------------------------------------------------------

    /// <summary>One simple selector — type, class, id, attribute, or pseudo.</summary>
    private readonly struct SimpleSelector
    {
        public readonly SimpleSelectorKind Kind;
        public readonly SelectorOpcode Opcode;
        public readonly string? Name;
        public readonly string? Value;
        public readonly int IntA;
        public readonly int IntB;
        public readonly SelectorList? SubList;

        private SimpleSelector(SimpleSelectorKind kind, SelectorOpcode op, string? name, string? value, int a, int b, SelectorList? sub)
        {
            Kind = kind; Opcode = op; Name = name; Value = value; IntA = a; IntB = b; SubList = sub;
        }

        public static SimpleSelector Universal() =>
            new(SimpleSelectorKind.Universal, SelectorOpcode.MatchUniversal, null, null, 0, 0, null);
        public static SimpleSelector Type(string name) =>
            new(SimpleSelectorKind.Type, SelectorOpcode.MatchTag, name, null, 0, 0, null);
        public static SimpleSelector Class(string name) =>
            new(SimpleSelectorKind.Class, SelectorOpcode.MatchClass, name, null, 0, 0, null);
        public static SimpleSelector Id(string name) =>
            new(SimpleSelectorKind.Id, SelectorOpcode.MatchId, name, null, 0, 0, null);
        public static SimpleSelector AttrExists(string name) =>
            new(SimpleSelectorKind.Attribute, SelectorOpcode.MatchAttrExists, name, null, 0, 0, null);
        public static SimpleSelector Attr(SelectorOpcode op, string name, string value) =>
            new(SimpleSelectorKind.Attribute, op, name, value, 0, 0, null);
        public static SimpleSelector Pseudo(SelectorOpcode op) =>
            new(SimpleSelectorKind.Pseudo, op, null, null, 0, 0, null);
        public static SimpleSelector Nth(SelectorOpcode op, int a, int b) =>
            new(SimpleSelectorKind.Nth, op, null, null, a, b, null);
        public static SimpleSelector SubGroup(SelectorOpcode op, SelectorList list) =>
            new(SimpleSelectorKind.SubGroup, op, null, null, 0, 0, list);
        public static SimpleSelector PseudoElement(string name) =>
            new(SimpleSelectorKind.PseudoElement, SelectorOpcode.End, name, null, 0, 0, null);
    }

    private enum SimpleSelectorKind : byte
    {
        Universal, Type, Class, Id, Attribute, Pseudo, Nth, SubGroup, PseudoElement,
    }

    private sealed class CompoundSelector
    {
        private readonly List<SimpleSelector> _parts = new();
        public bool IsEmpty => _parts.Count == 0;
        public IReadOnlyList<SimpleSelector> Parts => _parts;
        public void Add(SimpleSelector s) => _parts.Add(s);
    }

    /// <summary>Buffer + counters used while emitting a single alternative's bytecode.</summary>
    private sealed class EmitContext
    {
        public ImmutableArray<byte>.Builder Code { get; } = ImmutableArray.CreateBuilder<byte>();
        public ImmutableArray<string>.Builder Symbols { get; } = ImmutableArray.CreateBuilder<string>();
        public ImmutableArray<SelectorBytecode>.Builder SubGroups { get; } = ImmutableArray.CreateBuilder<SelectorBytecode>();
        public Specificity Specificity;
        public HashSet<string> RequiredTags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RequiredClasses { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RequiredIds { get; } = new(StringComparer.Ordinal);
        public bool ContainsHas;

        public void EmitOpcode(SelectorOpcode op) => Code.Add((byte)op);

        public void EmitUInt16(ushort value)
        {
            Code.Add((byte)(value & 0xFF));
            Code.Add((byte)((value >> 8) & 0xFF));
        }

        public void EmitInt32(int value)
        {
            Code.Add((byte)(value & 0xFF));
            Code.Add((byte)((value >> 8) & 0xFF));
            Code.Add((byte)((value >> 16) & 0xFF));
            Code.Add((byte)((value >> 24) & 0xFF));
        }

        public ushort InternSymbol(string s)
        {
            // Linear scan — selector symbol counts are tiny (typical < 8).
            for (var i = 0; i < Symbols.Count; i++)
                if (string.Equals(Symbols[i], s, StringComparison.Ordinal))
                    return (ushort)i;
            if (Symbols.Count >= ushort.MaxValue)
                throw new InvalidOperationException("selector symbol table overflowed");
            Symbols.Add(s);
            return (ushort)(Symbols.Count - 1);
        }

        public ushort AddSubGroup(SelectorBytecode sub)
        {
            if (SubGroups.Count >= ushort.MaxValue)
                throw new InvalidOperationException("selector sub-group table overflowed");
            SubGroups.Add(sub);
            return (ushort)(SubGroups.Count - 1);
        }

        public void EmitCompound(CompoundSelector compound)
        {
            // Emit the parts in source order; ordering doesn't matter for matching since
            // they're conjunctive on the same element. Specificity contributes per-part.
            foreach (var part in compound.Parts)
            {
                EmitPart(part);
            }
        }

        private void EmitPart(SimpleSelector part)
        {
            switch (part.Kind)
            {
                case SimpleSelectorKind.Universal:
                    EmitOpcode(SelectorOpcode.MatchUniversal);
                    // Universal has zero specificity; nothing to add.
                    break;
                case SimpleSelectorKind.Type:
                    EmitOpcode(SelectorOpcode.MatchTag);
                    EmitUInt16(InternSymbol(part.Name!));
                    Specificity += new Specificity(0, 0, 1);
                    RequiredTags.Add(part.Name!);
                    break;
                case SimpleSelectorKind.Class:
                    EmitOpcode(SelectorOpcode.MatchClass);
                    EmitUInt16(InternSymbol(part.Name!));
                    Specificity += new Specificity(0, 1, 0);
                    RequiredClasses.Add(part.Name!);
                    break;
                case SimpleSelectorKind.Id:
                    EmitOpcode(SelectorOpcode.MatchId);
                    EmitUInt16(InternSymbol(part.Name!));
                    Specificity += new Specificity(1, 0, 0);
                    RequiredIds.Add(part.Name!);
                    break;
                case SimpleSelectorKind.Attribute:
                    EmitOpcode(part.Opcode);
                    EmitUInt16(InternSymbol(part.Name!));
                    if (part.Opcode != SelectorOpcode.MatchAttrExists)
                        EmitUInt16(InternSymbol(part.Value!));
                    Specificity += new Specificity(0, 1, 0);
                    break;
                case SimpleSelectorKind.Pseudo:
                    EmitOpcode(part.Opcode);
                    Specificity += new Specificity(0, 1, 0);
                    break;
                case SimpleSelectorKind.Nth:
                    EmitOpcode(part.Opcode);
                    EmitInt32(part.IntA);
                    EmitInt32(part.IntB);
                    Specificity += new Specificity(0, 1, 0);
                    break;
                case SimpleSelectorKind.SubGroup:
                    EmitSubGroup(part);
                    break;
                case SimpleSelectorKind.PseudoElement:
                    // Pseudo-elements bump specificity by C but don't emit a match opcode.
                    Specificity += new Specificity(0, 0, 1);
                    break;
            }
        }

        private void EmitSubGroup(SimpleSelector part)
        {
            // Compile the sub-list into a single SelectorBytecode by concatenating the
            // alternatives' specificity and required-token data; the matcher walks each
            // alternative independently for :is/:where/:not/:has.
            // For storage we wrap the sub-list as one SelectorBytecode-like aggregate:
            // we synthesize a "container" SelectorBytecode whose Code is empty but whose
            // SubGroups are the alternatives. Matcher detects this via opcode dispatch
            // (MatchNot/Is/Where/Has).
            var inner = part.SubList!;
            // Build a fake SelectorBytecode whose alternatives live as our "SubGroups[i]"
            // entries — but the cleaner shape is: this single SubGroups entry IS itself a
            // SelectorBytecode that aggregates the alternatives via... no, that re-creates
            // the same problem. Use a different approach: each alternative becomes its own
            // SubGroups entry, and we emit one MatchXxx opcode per alternative. For an
            // OR-ish pseudo-class (:is/:where/:has) the matcher can short-circuit on first
            // hit. For :not we need ALL alternatives to fail — emit one MatchNot per alt;
            // they're conjunctive at the same opcode-list position so all must pass.
            //
            // Simplification: store a single aggregate SubGroups entry whose own SubGroups
            // list holds all alternatives. The matcher reads the aggregate, iterates, and
            // applies the OR/AND semantics per opcode.
            var aggregate = new SelectorBytecode(
                code: ImmutableArray<byte>.Empty,
                symbols: ImmutableArray<string>.Empty,
                subGroups: inner.Alternatives,
                specificity: inner.MaxSpecificity,
                requiredTags: FrozenSet<string>.Empty,
                requiredClasses: FrozenSet<string>.Empty,
                requiredIds: FrozenSet<string>.Empty,
                containsHas: inner.ContainsHas,
                sourceText: inner.SourceText);

            var idx = AddSubGroup(aggregate);
            EmitOpcode(part.Opcode);
            EmitUInt16(idx);

            // Specificity contribution per CSS Selectors L4 §17:
            //   :is(X), :not(X), :has(X) → max specificity among arguments
            //   :where(X) → 0
            switch (part.Opcode)
            {
                case SelectorOpcode.MatchIs:
                case SelectorOpcode.MatchNot:
                case SelectorOpcode.MatchHas:
                    Specificity += inner.MaxSpecificity;
                    break;
                case SelectorOpcode.MatchWhere:
                    // Always zero; no contribution.
                    break;
            }

            if (part.Opcode == SelectorOpcode.MatchHas) ContainsHas = true;
            else if (inner.ContainsHas) ContainsHas = true;
        }
    }
}
