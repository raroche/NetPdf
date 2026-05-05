// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
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
/// <b>Forgiving selector lists (Selectors L4 §3.7).</b> <c>:is()</c> and <c>:where()</c>
/// accept "forgiving selector lists" — invalid alternatives are dropped silently rather than
/// failing the entire selector. Top-level selector lists, <c>:not()</c>, and <c>:has()</c>
/// stay non-forgiving per the same section. Empty argument lists in any of these functional
/// pseudo-classes are rejected explicitly: an empty <c>:not()</c> would otherwise match
/// every element (vacuous "no alternative matched").
/// </para>
/// <para>
/// <b>CSS identifier escapes.</b> The identifier reader decodes both forms from CSS Syntax
/// L3 §4.3.7: <c>\&lt;non-newline, non-hex-digit&gt;</c> (literal character) and
/// <c>\&lt;hex&gt;{1,6}</c> + optional whitespace (Unicode code point). Critical for modern
/// CSS — Tailwind's responsive utility classes (<c>.sm\:block</c>, <c>.w-1\/2</c>) and
/// auto-generated IDs use them heavily.
/// </para>
/// <para>
/// <b>Required-token soundness for bloom-filter pre-filter.</b> Tokens are added to
/// <see cref="SelectorBytecode.RequiredTags"/> / <c>RequiredClasses</c> / <c>RequiredIds</c>
/// only when the compound is reachable from the candidate element via descendant / child
/// combinators only. Once a sibling combinator (<c>+</c> / <c>~</c>) appears in the chain,
/// the surviving compounds describe siblings — not ancestors — and including them in the
/// bloom test would unsoundly false-reject. Tag names are lowercased before insertion so
/// they hash identically to the cascade's element-side tokens (which are normalized via
/// <c>IElement.LocalName</c> already lowercased by AngleSharp's HTML parser).
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
        return parser.ParseSelectorList(SelectorContext.TopLevel);
    }

    /// <summary>Parsing context — controls the closing delimiter, whether the list is
    /// forgiving (per Selectors L4 §3.7), and whether leading combinators are accepted
    /// (per <see cref="SelectorOpcode.MatchHas"/>'s relative-selector grammar).</summary>
    private enum SelectorContext : byte
    {
        /// <summary>Top-level: closes at end of input, comma is alternative separator,
        /// strict (any parse error fails the whole list).</summary>
        TopLevel,
        /// <summary>Inside <c>:not()</c> / <c>:has()</c>: closes at <c>)</c>, strict.</summary>
        StrictSubGroup,
        /// <summary>Inside <c>:is()</c> / <c>:where()</c>: closes at <c>)</c>, individual
        /// invalid alternatives are dropped instead of failing the whole list.</summary>
        ForgivingSubGroup,
        /// <summary>Inside <c>:has()</c>: closes at <c>)</c>, strict, AND a leading
        /// combinator (<c>&gt;</c> / <c>+</c> / <c>~</c>) implies an <c>:scope</c>-anchored
        /// "relative selector" per Selectors L4 §16. The compiler emits an implicit universal
        /// compound at the front so the bytecode parses cleanly; matching is unreachable
        /// because <see cref="SelectorBytecode.ContainsHas"/> shorts the matcher.</summary>
        HasSubGroup,
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

        /// <summary>Parse <c>SelectorList := ComplexSelector ("," ComplexSelector)*</c>.</summary>
        public SelectorList ParseSelectorList(SelectorContext ctx)
        {
            var alts = ImmutableArray.CreateBuilder<SelectorBytecode>();
            SkipWhitespace();
            if (IsEnd || (ctx != SelectorContext.TopLevel && Peek() == ')'))
                return SelectorList.Empty;

            while (true)
            {
                if (ctx == SelectorContext.ForgivingSubGroup)
                {
                    // Forgiving mode: catch per-alternative parse errors and skip the
                    // alternative, advancing past the next comma or closing paren.
                    var savedPos = _pos;
                    try
                    {
                        alts.Add(ParseComplexSelector(ctx));
                    }
                    catch (SelectorParseException)
                    {
                        _pos = savedPos;
                        // Skip until next "," or ")" at top level (paren-aware).
                        SkipToNextAlternativeOrEnd();
                    }
                }
                else
                {
                    alts.Add(ParseComplexSelector(ctx));
                }
                SkipWhitespace();
                if (IsEnd || (ctx != SelectorContext.TopLevel && Peek() == ')')) break;
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
        private SelectorBytecode ParseComplexSelector(SelectorContext ctx)
        {
            var startPos = _pos;
            var compounds = new List<CompoundSelector>();
            var combinators = new List<SelectorOpcode>();

            // :has() is the only context that allows a leading combinator (relative selector).
            // The implicit universal compound preceding it gives the matcher a left anchor;
            // since ContainsHas is set on this branch, the matcher won't actually run it.
            if (ctx == SelectorContext.HasSubGroup && !IsEnd && IsLeadingCombinator(Peek()))
            {
                compounds.Add(CompoundSelector.SingletonUniversal());
                var leadingCombinator = Peek() switch
                {
                    '>' => SelectorOpcode.Child,
                    '+' => SelectorOpcode.AdjacentSibling,
                    '~' => SelectorOpcode.GeneralSibling,
                    _ => SelectorOpcode.Descendant, // unreachable given IsLeadingCombinator filter
                };
                _pos++;
                combinators.Add(leadingCombinator);
                SkipWhitespace();
            }

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

        /// <summary>Skip ahead to the next "," or ")" at top level, paren-aware. Used by
        /// <see cref="SelectorContext.ForgivingSubGroup"/> to drop a malformed alternative
        /// without aborting the whole list.</summary>
        private void SkipToNextAlternativeOrEnd()
        {
            int parenDepth = 0;
            while (!IsEnd)
            {
                var c = Peek();
                if (parenDepth == 0 && (c == ',' || c == ')')) return;
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                else if (c == '"' || c == '\'') { ReadString(c); continue; }
                _pos++;
            }
        }

        /// <summary>Build the bytecode for one alternative. Tracks an "in-ancestor-chain"
        /// flag so required tokens are only collected for compounds anchored to the candidate
        /// element's ancestor lineage (Rec 3 — bloom-filter soundness).</summary>
        private SelectorBytecode EmitBytecode(
            List<CompoundSelector> compounds,
            List<SelectorOpcode> combinators,
            int startPos)
        {
            var ctx = new EmitContext();
            string? pseudoElement = null;

            // Right-to-left emission. The candidate (rightmost) compound is always anchored
            // to itself so its tokens contribute to required sets. Ancestor chain breaks the
            // first time a sibling combinator is encountered while walking left.
            var inAncestorChain = true;
            for (var i = compounds.Count - 1; i >= 0; i--)
            {
                var compound = compounds[i];
                var isKeyCompound = i == compounds.Count - 1;
                var compoundPseudo = ctx.EmitCompound(compound, addToRequired: inAncestorChain);
                if (isKeyCompound && compoundPseudo is not null)
                    pseudoElement = compoundPseudo;
                else if (compoundPseudo is not null)
                    throw Error("pseudo-element must be the rightmost component of the rightmost compound");

                if (i > 0)
                {
                    var combinator = combinators[i - 1];
                    ctx.EmitOpcode(combinator);
                    if (combinator == SelectorOpcode.AdjacentSibling
                        || combinator == SelectorOpcode.GeneralSibling)
                        inAncestorChain = false;
                }
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
                sourceText: sourceText,
                pseudoElement: pseudoElement);
        }

        /// <summary>Parse one compound selector — a sequence of simple selectors with no
        /// whitespace or combinator between them.</summary>
        private CompoundSelector ParseCompoundSelector()
        {
            var compound = new CompoundSelector();
            if (IsEnd) throw Error("expected compound selector");
            var c = Peek();
            if (c == '*') { compound.Add(SimpleSelector.Universal()); _pos++; }
            else if (IsIdentStart(c) || c == '\\') { compound.Add(SimpleSelector.Type(ReadIdent())); }
            else if (c != '.' && c != '#' && c != '[' && c != ':')
                throw Error($"unexpected character '{c}'");

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

            // Case sensitivity flag: 'i' (case-insensitive) or 's' (case-sensitive, the default).
            // Per Selectors L4 §6.3.2.
            byte caseFlag = 0;
            if (!IsEnd && (Peek() == 'i' || Peek() == 'I'))
            {
                caseFlag = 1;
                _pos++;
                SkipWhitespace();
            }
            else if (!IsEnd && (Peek() == 's' || Peek() == 'S'))
            {
                _pos++;
                SkipWhitespace();
            }
            Expect(']');
            return SimpleSelector.Attr(op, name, value, caseFlag);
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
            if (!IsEnd && Peek() == ':')
            {
                _pos++;
                var pseudoElementName = ReadIdent();
                if (string.IsNullOrEmpty(pseudoElementName))
                    throw Error("expected pseudo-element name");
                if (!IsEnd && Peek() == '(') SkipParens();
                return SimpleSelector.PseudoElement(pseudoElementName);
            }

            var name = ReadIdent();
            if (string.IsNullOrEmpty(name)) throw Error("expected pseudo-class name");

            if (!IsEnd && Peek() == '(')
                return ParseFunctionalPseudo(name);

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
                case "not": return ParseSubGroup(SelectorOpcode.MatchNot, SelectorContext.StrictSubGroup);
                case "is": return ParseSubGroup(SelectorOpcode.MatchIs, SelectorContext.ForgivingSubGroup);
                case "where": return ParseSubGroup(SelectorOpcode.MatchWhere, SelectorContext.ForgivingSubGroup);
                case "has": return ParseSubGroup(SelectorOpcode.MatchHas, SelectorContext.HasSubGroup);
                default: throw Error($"unsupported functional pseudo-class ':{name}()'");
            }
        }

        private SimpleSelector ParseNth(SelectorOpcode op)
        {
            var (a, b) = ReadAnPlusB();
            SkipWhitespace();
            Expect(')');
            return SimpleSelector.Nth(op, a, b);
        }

        private SimpleSelector ParseSubGroup(SelectorOpcode op, SelectorContext ctx)
        {
            var inner = ParseSelectorList(ctx);
            SkipWhitespace();
            Expect(')');
            // Empty argument list is invalid for all four functional pseudo-classes per
            // Selectors L4. Without this, :not() / :is() / :where() / :has() would all
            // become vacuous match-all (or always-false) — silent miscascade.
            if (inner.Alternatives.IsDefaultOrEmpty)
                throw Error($"functional pseudo-class requires at least one selector argument");
            return SimpleSelector.SubGroup(op, inner);
        }

        private (int a, int b) ReadAnPlusB()
        {
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

            if (!IsEnd && (Peek() == 'n' || Peek() == 'N'))
            {
                _pos++;
                int a = checked(sign * (leadingNumber ?? 1));
                int b = 0;
                SkipWhitespace();
                if (!IsEnd && (Peek() == '+' || Peek() == '-'))
                {
                    var bsign = Peek() == '-' ? -1 : 1;
                    _pos++;
                    SkipWhitespace();
                    if (IsEnd || !IsAsciiDigit(Peek()))
                        throw Error("expected digit after sign in An+B formula");
                    b = checked(bsign * ReadInteger());
                }
                return (a, b);
            }

            if (leadingNumber is null)
                throw Error("expected An+B formula or 'even'/'odd'");
            return (0, checked(sign * leadingNumber.Value));
        }

        private bool TryReadKeyword(string keyword)
        {
            if (_pos + keyword.Length > _text.Length) return false;
            for (var i = 0; i < keyword.Length; i++)
            {
                if (char.ToLowerInvariant(_text[_pos + i]) != keyword[i]) return false;
            }
            var next = _pos + keyword.Length;
            if (next < _text.Length)
            {
                var c = _text[next];
                if (IsIdentContinue(c) || c == '\\') return false;
            }
            _pos += keyword.Length;
            return true;
        }

        /// <summary>Read a base-10 integer. Wraps <c>int.TryParse</c> so values that
        /// don't fit in <see cref="int"/> surface as <see cref="SelectorParseException"/>
        /// instead of leaking <see cref="OverflowException"/> to the cascade.</summary>
        private int ReadInteger()
        {
            var start = _pos;
            while (!IsEnd && IsAsciiDigit(Peek())) _pos++;
            var s = _text[start.._pos];
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw Error($"integer '{s}' does not fit in 32 bits");
            return value;
        }

        /// <summary>Read a CSS identifier with full escape decoding per CSS Syntax L3 §4.3.7.
        /// Handles both literal-character escapes (<c>\:</c>, <c>\/</c>) and hex escapes
        /// (<c>\E9</c>, <c>\41 6</c>) — critical for Tailwind's responsive utility classes
        /// like <c>.sm\:block</c> and <c>.w-1\/2</c>.</summary>
        private string ReadIdent()
        {
            var start = _pos;
            var sb = new StringBuilder();

            // Optional leading hyphen — CSS idents may start with - (custom names) or --
            // (custom properties; we don't accept these in selectors). Single - is fine.
            if (!IsEnd && Peek() == '-')
            {
                sb.Append('-');
                _pos++;
            }
            // Identifier must start with letter/underscore/non-ASCII OR backslash escape.
            if (IsEnd || (!IsIdentStart(Peek()) && Peek() != '\\'))
            {
                _pos = start;
                return string.Empty;
            }
            while (!IsEnd)
            {
                var c = Peek();
                if (c == '\\')
                {
                    sb.Append(DecodeEscape());
                    continue;
                }
                if (!IsIdentContinue(c)) break;
                sb.Append(c);
                _pos++;
            }
            return sb.Length == 0 ? string.Empty : sb.ToString();
        }

        /// <summary>Decode one CSS escape sequence starting at the current <c>\</c> position.
        /// Per CSS Syntax L3 §4.3.7: hex escape is 1–6 hex digits + optional whitespace
        /// terminator; literal escape is the character following the backslash (newlines
        /// are not valid escape continuations but we accept them silently for tolerance).</summary>
        private string DecodeEscape()
        {
            // Position is on the backslash. Consume it.
            _pos++;
            if (IsEnd)
                throw Error("dangling backslash in identifier escape");
            var c = Peek();
            if (IsHexDigit(c))
            {
                // Read up to 6 hex digits.
                var hexStart = _pos;
                var hexEnd = _pos;
                while (hexEnd < _text.Length && hexEnd - hexStart < 6 && IsHexDigit(_text[hexEnd]))
                    hexEnd++;
                _pos = hexEnd;
                // Optional single whitespace terminator.
                if (!IsEnd && (Peek() == ' ' || Peek() == '\t' || Peek() == '\n'
                    || Peek() == '\r' || Peek() == '\f'))
                    _pos++;
                var hex = _text[hexStart..hexEnd];
                var codePoint = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                // Out-of-range / NUL / surrogate replacement per CSS Syntax L3 §4.3.7.
                if (codePoint == 0 || codePoint > 0x10FFFF
                    || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                    return "�";
                return char.ConvertFromUtf32(codePoint);
            }
            // Literal-character escape — the next char is taken verbatim.
            _pos++;
            return c.ToString();
        }

        private string ReadString(char quote)
        {
            Expect(quote);
            var sb = new StringBuilder();
            while (!IsEnd)
            {
                var c = Peek();
                if (c == quote) { _pos++; return sb.ToString(); }
                if (c == '\\' && _pos + 1 < _text.Length)
                {
                    sb.Append(DecodeEscape());
                    continue;
                }
                sb.Append(c);
                _pos++;
            }
            throw Error("unterminated string in attribute selector");
        }

        private void SkipParens()
        {
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

        private SelectorParseException Error(string reason) => new(_text, _pos, reason);

        private readonly bool IsEnd => _pos >= _text.Length;
        private readonly char Peek() => _text[_pos];

        private static bool IsIdentStart(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c >= 0x80;

        private static bool IsIdentContinue(char c) =>
            IsIdentStart(c) || (c >= '0' && c <= '9') || c == '-';

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsHexDigit(char c) =>
            IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static bool IsCompoundStart(char c) =>
            IsIdentStart(c) || c == '*' || c == '.' || c == '#' || c == '[' || c == ':' || c == '\\';

        private static bool IsLeadingCombinator(char c) => c == '>' || c == '+' || c == '~';
    }

    // ------------------------------------------------------------------------
    // Intermediate types — built during parsing, then folded into the bytecode.
    // ------------------------------------------------------------------------

    private readonly struct SimpleSelector
    {
        public readonly SimpleSelectorKind Kind;
        public readonly SelectorOpcode Opcode;
        public readonly string? Name;
        public readonly string? Value;
        public readonly int IntA;
        public readonly int IntB;
        public readonly SelectorList? SubList;
        public readonly byte CaseFlag;

        private SimpleSelector(SimpleSelectorKind kind, SelectorOpcode op, string? name, string? value, int a, int b, SelectorList? sub, byte caseFlag)
        {
            Kind = kind; Opcode = op; Name = name; Value = value; IntA = a; IntB = b; SubList = sub; CaseFlag = caseFlag;
        }

        public static SimpleSelector Universal() =>
            new(SimpleSelectorKind.Universal, SelectorOpcode.MatchUniversal, null, null, 0, 0, null, 0);
        public static SimpleSelector Type(string name) =>
            new(SimpleSelectorKind.Type, SelectorOpcode.MatchTag, name, null, 0, 0, null, 0);
        public static SimpleSelector Class(string name) =>
            new(SimpleSelectorKind.Class, SelectorOpcode.MatchClass, name, null, 0, 0, null, 0);
        public static SimpleSelector Id(string name) =>
            new(SimpleSelectorKind.Id, SelectorOpcode.MatchId, name, null, 0, 0, null, 0);
        public static SimpleSelector AttrExists(string name) =>
            new(SimpleSelectorKind.Attribute, SelectorOpcode.MatchAttrExists, name, null, 0, 0, null, 0);
        public static SimpleSelector Attr(SelectorOpcode op, string name, string value, byte caseFlag) =>
            new(SimpleSelectorKind.Attribute, op, name, value, 0, 0, null, caseFlag);
        public static SimpleSelector Pseudo(SelectorOpcode op) =>
            new(SimpleSelectorKind.Pseudo, op, null, null, 0, 0, null, 0);
        public static SimpleSelector Nth(SelectorOpcode op, int a, int b) =>
            new(SimpleSelectorKind.Nth, op, null, null, a, b, null, 0);
        public static SimpleSelector SubGroup(SelectorOpcode op, SelectorList list) =>
            new(SimpleSelectorKind.SubGroup, op, null, null, 0, 0, list, 0);
        public static SimpleSelector PseudoElement(string name) =>
            new(SimpleSelectorKind.PseudoElement, SelectorOpcode.End, name, null, 0, 0, null, 0);
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

        /// <summary>Synthetic universal-selector compound used as the implicit anchor for
        /// <c>:has()</c>'s relative-selector grammar (Selectors L4 §16).</summary>
        public static CompoundSelector SingletonUniversal()
        {
            var c = new CompoundSelector();
            c.Add(SimpleSelector.Universal());
            return c;
        }
    }

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

        public void EmitByte(byte b) => Code.Add(b);

        public ushort InternSymbol(string s)
        {
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

        /// <summary>Emit one compound's match opcodes. Returns the pseudo-element name when
        /// the compound includes one (Task 14 reads it for materialization). Pseudo-element
        /// validation: at most one per compound, must be the rightmost simple in the compound.
        /// </summary>
        public string? EmitCompound(CompoundSelector compound, bool addToRequired)
        {
            string? pseudoElement = null;
            for (var i = 0; i < compound.Parts.Count; i++)
            {
                var part = compound.Parts[i];
                if (part.Kind == SimpleSelectorKind.PseudoElement)
                {
                    if (pseudoElement is not null)
                        throw new SelectorParseException(
                            string.Empty, 0, "at most one pseudo-element per compound selector");
                    pseudoElement = part.Name;
                    Specificity += new Specificity(0, 0, 1);
                    continue;
                }
                // Per Selectors L4 §3.5: only certain pseudo-classes are valid after a
                // pseudo-element. v1 validation: no further simple selector after a
                // pseudo-element except specific tail pseudo-classes (we accept all
                // pseudo-classes as a relaxed check — strict spec compliance lands when
                // the pseudo-element subset stabilizes).
                if (pseudoElement is not null && part.Kind != SimpleSelectorKind.Pseudo)
                    throw new SelectorParseException(
                        string.Empty, 0,
                        $"simple selector of kind {part.Kind} cannot follow a pseudo-element");

                EmitPart(part, addToRequired);
            }
            return pseudoElement;
        }

        private void EmitPart(SimpleSelector part, bool addToRequired)
        {
            switch (part.Kind)
            {
                case SimpleSelectorKind.Universal:
                    EmitOpcode(SelectorOpcode.MatchUniversal);
                    break;
                case SimpleSelectorKind.Type:
                    EmitOpcode(SelectorOpcode.MatchTag);
                    EmitUInt16(InternSymbol(part.Name!));
                    Specificity += new Specificity(0, 0, 1);
                    if (addToRequired) RequiredTags.Add(part.Name!.ToLowerInvariant());
                    break;
                case SimpleSelectorKind.Class:
                    EmitOpcode(SelectorOpcode.MatchClass);
                    EmitUInt16(InternSymbol(part.Name!));
                    Specificity += new Specificity(0, 1, 0);
                    if (addToRequired) RequiredClasses.Add(part.Name!);
                    break;
                case SimpleSelectorKind.Id:
                    EmitOpcode(SelectorOpcode.MatchId);
                    EmitUInt16(InternSymbol(part.Name!));
                    Specificity += new Specificity(1, 0, 0);
                    if (addToRequired) RequiredIds.Add(part.Name!);
                    break;
                case SimpleSelectorKind.Attribute:
                    EmitOpcode(part.Opcode);
                    EmitUInt16(InternSymbol(part.Name!));
                    if (part.Opcode != SelectorOpcode.MatchAttrExists)
                    {
                        EmitUInt16(InternSymbol(part.Value!));
                        EmitByte(part.CaseFlag);
                    }
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
            }
        }

        private void EmitSubGroup(SimpleSelector part)
        {
            var inner = part.SubList!;
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

            switch (part.Opcode)
            {
                case SelectorOpcode.MatchIs:
                case SelectorOpcode.MatchNot:
                case SelectorOpcode.MatchHas:
                    Specificity += inner.MaxSpecificity;
                    break;
                case SelectorOpcode.MatchWhere:
                    break;
            }

            if (part.Opcode == SelectorOpcode.MatchHas) ContainsHas = true;
            else if (inner.ContainsHas) ContainsHas = true;
        }
    }
}
