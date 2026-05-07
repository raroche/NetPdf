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
    /// <summary>HTML attributes that are matched ASCII case-insensitively by default per
    /// CSS Selectors L4 §6.3.2 (citing the HTML 5 spec's set of "case-insensitive"
    /// attributes). When a selector targets one of these AND no explicit case flag is
    /// supplied, the matcher uses ASCII-case-insensitive comparison automatically.
    /// The <c>s</c> flag overrides back to case-sensitive; <c>i</c> is a no-op redundancy.
    /// Names compared case-insensitively because attribute names themselves are
    /// case-insensitive in HTML.</summary>
    private static readonly FrozenSet<string> HtmlCaseInsensitiveAttributes =
        new[]
        {
            "accept", "accept-charset", "align", "alink", "axis", "bgcolor", "charset",
            "checked", "clear", "codetype", "color", "compact", "declare", "defer",
            "dir", "direction", "disabled", "enctype", "face", "frame", "frameborder",
            "hreflang", "http-equiv", "lang", "language", "link", "media", "method",
            "multiple", "nohref", "noresize", "noshade", "nowrap", "readonly", "rel",
            "rev", "rules", "scope", "scrolling", "selected", "shape", "target", "text",
            "type", "valign", "valuetype", "vlink",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-action pseudo-classes permitted to follow a pseudo-element per CSS
    /// Selectors L4 §3.5. Other pseudo-classes (structural like <c>:first-child</c>,
    /// <c>:nth-child</c>, etc.) are rejected at compile time so a typo in a selector
    /// like <c>p::before:first-child</c> surfaces as <c>CSS-PARSE-WARNING-001</c>
    /// instead of silently never matching. Functional pseudo-classes
    /// <c>:not</c>/<c>:is</c>/<c>:where</c>/<c>:has</c> are also allowed.</summary>
    private static readonly FrozenSet<string> PseudoElementTailAllowlist =
        new[] { "hover", "focus", "active", "focus-visible", "focus-within" }
        .ToFrozenSet(StringComparer.Ordinal);

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

        /// <summary>Tracks parser recursion depth across the <c>ParseSelectorList</c>
        /// + <see cref="ParseSubGroup"/> chains so an attacker selector with thousands
        /// of nested <c>:is()</c> / <c>:not()</c> / <c>:has()</c> invocations can't
        /// escalate into <c>StackOverflowException</c> (uncatchable). Mirror of
        /// <c>VarSubstitution.MaxRecursionDepth</c>'s defense from Phase 2 deep review.
        /// Limit picked to comfortably accommodate hand-authored CSS (real selectors
        /// rarely exceed 5 levels of pseudo-class nesting) while bounding adversarial
        /// inputs to well under the .NET 1 MiB default stack budget.</summary>
        private const int MaxRecursionDepth = 64;

        private int _depth;

        public Parser(string text)
        {
            _text = text;
            _pos = 0;
            _depth = 0;
        }

        /// <summary>Parse <c>SelectorList := ComplexSelector ("," ComplexSelector)*</c>.
        /// Convenience overload that discards the "any alternative attempted" flag — used
        /// at top-level where the distinction is irrelevant (top-level lists are not
        /// forgiving).</summary>
        public SelectorList ParseSelectorList(SelectorContext ctx) =>
            ParseSelectorList(ctx, out _);

        /// <summary>Parse a selector list and report whether at least one alternative was
        /// attempted (regardless of whether it succeeded). Distinguishes "authored empty
        /// parens" (forgiving wrapper rejects) from "all alternatives dropped via forgiving
        /// mode" (forgiving wrapper accepts as a match-nothing pseudo-class per Selectors
        /// L4 §3.7).</summary>

        public SelectorList ParseSelectorList(SelectorContext ctx, out bool anyAlternativeAttempted)
        {
            // Depth guard per Phase 2 deep review C-1 — bound recursion so a
            // pathological `:is(:is(:is(...)))` payload can't crash the host.
            if (_depth >= MaxRecursionDepth)
                throw new SelectorParseException(_text, _pos, "selector nesting exceeds depth limit");
            _depth++;
            try
            {
                return ParseSelectorListCore(ctx, out anyAlternativeAttempted);
            }
            finally
            {
                _depth--;
            }
        }

        private SelectorList ParseSelectorListCore(SelectorContext ctx, out bool anyAlternativeAttempted)
        {
            anyAlternativeAttempted = false;
            var alts = ImmutableArray.CreateBuilder<SelectorBytecode>();
            SkipWhitespace();
            if (IsEnd || (ctx != SelectorContext.TopLevel && Peek() == ')'))
                return SelectorList.Empty;

            while (true)
            {
                anyAlternativeAttempted = true;
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

            var inSubGroup = ctx != SelectorContext.TopLevel;
            compounds.Add(ParseCompoundSelector(inSubGroup));

            while (true)
            {
                // Track whether actual whitespace was consumed: descendant combinator is
                // implied by whitespace, NOT by simple adjacency. Without this guard,
                // selectors like `div*` or `.foo*` would be wrongly interpreted as
                // descendant selectors instead of failing at parse time.
                var hadWhitespace = SkipWhitespaceReturningCount() > 0;
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
                    if (!hadWhitespace)
                        throw Error($"unexpected '{c}' — descendant combinator requires whitespace");
                    combinator = SelectorOpcode.Descendant;
                }

                SkipWhitespace();
                if (IsEnd) throw Error("expected compound selector after combinator");
                combinators.Add(combinator);
                compounds.Add(ParseCompoundSelector(inSubGroup));
            }

            return EmitBytecode(compounds, combinators, startPos);
        }

        /// <summary>Skip CSS whitespace; return the number of characters consumed so the
        /// caller can distinguish "compound followed by whitespace then identifier"
        /// (descendant combinator) from "compound directly adjacent to invalid char"
        /// (parse error) — see CSS Selectors L4 §15.</summary>
        private int SkipWhitespaceReturningCount()
        {
            var start = _pos;
            while (!IsEnd)
            {
                var c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f') _pos++;
                else break;
            }
            return _pos - start;
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
        /// whitespace or combinator between them. The <paramref name="inSubGroup"/> flag
        /// makes pseudo-elements (<c>::name</c> + legacy <c>:before</c>/<c>:after</c>/
        /// <c>:first-line</c>/<c>:first-letter</c>) invalid per CSS Selectors L4 §3.5
        /// (pseudo-elements are not elements, so they cannot appear inside <c>:is()</c>,
        /// <c>:where()</c>, <c>:not()</c>, or <c>:has()</c>).</summary>
        private CompoundSelector ParseCompoundSelector(bool inSubGroup)
        {
            var compound = new CompoundSelector();
            if (IsEnd) throw Error("expected compound selector");
            var c = Peek();
            if (c == '*') { compound.Add(SimpleSelector.Universal()); _pos++; }
            else if (IsIdentStart(c) || c == '\\' || c == '-')
            {
                var ident = ReadIdent();
                if (string.IsNullOrEmpty(ident)) throw Error("expected type selector");
                compound.Add(SimpleSelector.Type(ident));
            }
            else if (c != '.' && c != '#' && c != '[' && c != ':')
                throw Error($"unexpected character '{c}'");

            while (!IsEnd)
            {
                c = Peek();
                if (c == '.')
                {
                    _pos++;
                    var name = ReadIdent();
                    if (string.IsNullOrEmpty(name)) throw Error("expected class name after '.'");
                    compound.Add(SimpleSelector.Class(name));
                }
                else if (c == '#')
                {
                    _pos++;
                    var name = ReadIdent();
                    if (string.IsNullOrEmpty(name)) throw Error("expected id after '#'");
                    compound.Add(SimpleSelector.Id(name));
                }
                else if (c == '[') compound.Add(ParseAttributeSelector());
                else if (c == ':') compound.Add(ParsePseudo(inSubGroup));
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

            // Case sensitivity flag per Selectors L4 §6.3.2:
            //   - explicit `i` → caseFlag = 1 (ASCII case-insensitive)
            //   - explicit `s` → caseFlag = 0 (case-sensitive)
            //   - no flag, attribute is on the HTML CI list → caseFlag = 1 (default CI)
            //   - no flag, otherwise → caseFlag = 0
            // Computing at compile time keeps the matcher's hot path branch-free.
            byte caseFlag;
            if (!IsEnd && (Peek() == 'i' || Peek() == 'I'))
            {
                caseFlag = 1;
                _pos++;
                SkipWhitespace();
            }
            else if (!IsEnd && (Peek() == 's' || Peek() == 'S'))
            {
                caseFlag = 0;
                _pos++;
                SkipWhitespace();
            }
            else
            {
                caseFlag = HtmlCaseInsensitiveAttributes.Contains(name) ? (byte)1 : (byte)0;
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

        private SimpleSelector ParsePseudo(bool inSubGroup)
        {
            Expect(':');
            if (!IsEnd && Peek() == ':')
            {
                _pos++;
                var pseudoElementName = ReadIdent();
                if (string.IsNullOrEmpty(pseudoElementName))
                    throw Error("expected pseudo-element name");
                // Functional pseudo-elements (`::part(name)`, `::slotted(...)`,
                // `::cue(...)`, `::highlight(name)`, etc.) are not supported in v1 — none
                // of them have a rendering pipeline before Phase 4+. Rejecting at parse
                // time prevents silent miscascade from accepting the bare-pseudo branch
                // for selectors like `::part(foo)` (would have matched every element of
                // the named tag) and from accepting malformed input like `::before(`.
                if (!IsEnd && Peek() == '(')
                    throw Error($"functional pseudo-element ::{pseudoElementName}(...) not supported");
                if (inSubGroup)
                    throw Error("pseudo-elements are not valid inside :is()/:where()/:not()/:has()");
                return SimpleSelector.PseudoElement(pseudoElementName.ToLowerInvariant());
            }

            var name = ReadIdent();
            if (string.IsNullOrEmpty(name)) throw Error("expected pseudo-class name");

            if (!IsEnd && Peek() == '(')
                return ParseFunctionalPseudo(name);

            // Legacy CSS2 single-colon pseudo-elements still appear in the wild (older
            // invoice templates, email-style CSS). Per CSS Pseudo-Elements L4 §6, the
            // four named here MUST be accepted with single-colon syntax for backward
            // compatibility — even though double-colon is preferred for new code.
            var lower = name.ToLowerInvariant();
            if (lower is "before" or "after" or "first-line" or "first-letter")
            {
                if (inSubGroup)
                    throw Error("pseudo-elements are not valid inside :is()/:where()/:not()/:has()");
                return SimpleSelector.PseudoElement(lower);
            }

            return lower switch
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
                "focus-visible" => SimpleSelector.Pseudo(SelectorOpcode.MatchFocus), // alias for static PDFs
                "focus-within" => SimpleSelector.Pseudo(SelectorOpcode.MatchFocus),
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
            // CSS Selectors L4 §6.6.5.2: `:nth-child(An+B of S)` and `:nth-last-child(An+B of S)`.
            // `:nth-of-type` / `:nth-last-of-type` do NOT accept the `of` clause per spec.
            if (TryReadKeyword("of"))
            {
                if (op == SelectorOpcode.MatchNthChild) op = SelectorOpcode.MatchNthChildOf;
                else if (op == SelectorOpcode.MatchNthLastChild) op = SelectorOpcode.MatchNthLastChildOf;
                else throw Error(":nth-of-type and :nth-last-of-type do not accept an 'of S' clause");
                SkipWhitespace();
                var filter = ParseSelectorList(SelectorContext.StrictSubGroup);
                if (filter.Alternatives.IsDefaultOrEmpty)
                    throw Error("expected selector list after 'of'");
                SkipWhitespace();
                Expect(')');
                return SimpleSelector.Nth(op, a, b, filter);
            }
            Expect(')');
            return SimpleSelector.Nth(op, a, b);
        }

        private SimpleSelector ParseSubGroup(SelectorOpcode op, SelectorContext ctx)
        {
            var inner = ParseSelectorList(ctx, out var anyAttempted);
            SkipWhitespace();
            Expect(')');
            // Empty argument list:
            //   - authored empty `:not()` / `:is()` / etc. (no alternatives attempted) →
            //     invalid per Selectors L4. Empty :not() would be vacuous match-all
            //     (silent miscascade); empty :is() / :where() are also explicitly invalid.
            //   - forgiving list where every alternative was dropped (anyAttempted = true,
            //     alternatives empty) → valid match-nothing per L4 §3.7. The matcher's
            //     iteration over zero alternatives in MatchIs / MatchWhere returns false
            //     correctly. :not() / :has() are non-forgiving so they never reach this
            //     branch (a parse error in any alternative aborts).
            if (inner.Alternatives.IsDefaultOrEmpty && !anyAttempted)
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

        /// <summary>Read a CSS identifier with full escape decoding per CSS Syntax L3 §4.3.7
        /// + identifier-start grammar from §4.3.11. Handles both escape forms (literal
        /// <c>\:</c>, hex <c>\41</c>) and the <c>--</c> "custom-property style" prefix
        /// that's valid for class / id / attribute-value identifiers. Critical for
        /// Tailwind responsive utilities (<c>.sm\:block</c>) and modern frameworks that
        /// emit <c>.--my-class</c> / <c>#--my-id</c>.</summary>
        private string ReadIdent()
        {
            var start = _pos;
            var sb = new StringBuilder();

            // Per CSS Syntax L3 §4.3.11: an identifier may start with a single hyphen
            // followed by ident-start (letter/_/non-ASCII/escape/another hyphen), OR with
            // double hyphen (custom-property syntax). After the leading hyphens, the next
            // character must be a valid ident-start (or, for double-hyphen, the bare `--`
            // is acceptable per spec but we still require at least one continuation char
            // to avoid ambiguity with `--` operator artifacts the parser shouldn't see).
            var hyphens = 0;
            while (!IsEnd && Peek() == '-' && hyphens < 2)
            {
                sb.Append('-');
                _pos++;
                hyphens++;
            }

            // After 0 or 1 leading hyphens, the next char must be a regular ident-start
            // or escape. After 2 hyphens (`--` custom-property prefix), any ident-continue
            // char qualifies (digits OK, more hyphens OK, escape OK).
            if (hyphens < 2)
            {
                if (IsEnd || (!IsIdentStart(Peek()) && Peek() != '\\'))
                {
                    _pos = start;
                    return string.Empty;
                }
            }
            else
            {
                // `--` prefix — require at least one continuation char OR no continuation
                // at all is OK only when this `--` will be followed by something the
                // surrounding parser handles. To stay conservative and avoid greedy
                // misparses (e.g., `--`+nothing in a class slot), we accept the `--`
                // alone but only return non-empty when the next char isn't a parser-
                // significant boundary char. In practice the calling sites validate
                // non-empty themselves, so emitting "--" here is fine.
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
        public static SimpleSelector Nth(SelectorOpcode op, int a, int b, SelectorList? filter = null) =>
            new(SimpleSelectorKind.Nth, op, null, null, a, b, filter, 0);
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
        /// the compound includes one (Task 14 reads it for materialization).</summary>
        /// <remarks>
        /// Per CSS Selectors L4 §3.5, pseudo-elements have strict placement rules:
        /// (a) at most one pseudo-element per compound; (b) must be the last simple selector
        /// in the compound (no class / id / attribute / structural pseudo-class after it);
        /// (c) only specific user-action pseudo-classes (<c>:hover</c>, <c>:focus</c>,
        /// <c>:active</c>, <c>:focus-visible</c>, <c>:focus-within</c>) and functional
        /// pseudo-classes (<c>:not</c>/<c>:is</c>/<c>:where</c>/<c>:has</c>) may follow.
        /// Selectors like <c>p::before:first-child</c> are rejected at parse time so a typo
        /// surfaces as <c>CSS-PARSE-WARNING-001</c> instead of silently never matching.
        /// </remarks>
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
                // Validate tail-after-pseudo-element placement.
                if (pseudoElement is not null)
                {
                    if (!IsAllowedAfterPseudoElement(part))
                        throw new SelectorParseException(
                            string.Empty, 0,
                            $"simple selector of kind {part.Kind} ('{part.Name ?? part.Opcode.ToString()}') cannot follow a pseudo-element");
                }

                EmitPart(part, addToRequired);
            }
            return pseudoElement;
        }

        /// <summary>Returns <see langword="true"/> when the simple selector is permitted to
        /// follow a pseudo-element in the same compound (per the allowlist documented on
        /// <see cref="EmitCompound"/>).</summary>
        private static bool IsAllowedAfterPseudoElement(SimpleSelector part)
        {
            // Only :hover, :focus, :active, :focus-visible, :focus-within (mapped to
            // MatchHover / MatchFocus / MatchActive in our opcode set) plus functional
            // pseudo-classes (:not / :is / :where / :has).
            if (part.Kind == SimpleSelectorKind.Pseudo)
            {
                return part.Opcode is SelectorOpcode.MatchHover
                    or SelectorOpcode.MatchFocus
                    or SelectorOpcode.MatchActive;
            }
            if (part.Kind == SimpleSelectorKind.SubGroup)
            {
                return part.Opcode is SelectorOpcode.MatchNot
                    or SelectorOpcode.MatchIs
                    or SelectorOpcode.MatchWhere
                    or SelectorOpcode.MatchHas;
            }
            return false;
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
                    // :nth-child(of S) / :nth-last-child(of S) attach a filter sub-group +
                    // contribute the filter's max specificity per CSS Selectors L4 §17.
                    if (part.SubList is not null)
                    {
                        var aggregate = BuildAggregate(part.SubList);
                        EmitUInt16(AddSubGroup(aggregate));
                        Specificity += part.SubList.MaxSpecificity;
                        if (part.SubList.ContainsHas) ContainsHas = true;
                    }
                    Specificity += new Specificity(0, 1, 0);
                    break;
                case SimpleSelectorKind.SubGroup:
                    EmitSubGroup(part);
                    break;
            }
        }

        /// <summary>Wrap a parsed <see cref="SelectorList"/> into a single aggregate
        /// <see cref="SelectorBytecode"/> whose own <see cref="SelectorBytecode.SubGroups"/>
        /// holds the alternatives. The matcher reads this aggregate via
        /// <c>:not</c>/<c>:is</c>/<c>:where</c>/<c>:has</c>/<c>:nth-child(of)</c> opcodes
        /// and iterates the alternatives directly.</summary>
        public static SelectorBytecode BuildAggregate(SelectorList inner) =>
            new(code: ImmutableArray<byte>.Empty,
                symbols: ImmutableArray<string>.Empty,
                subGroups: inner.Alternatives,
                specificity: inner.MaxSpecificity,
                requiredTags: FrozenSet<string>.Empty,
                requiredClasses: FrozenSet<string>.Empty,
                requiredIds: FrozenSet<string>.Empty,
                containsHas: inner.ContainsHas,
                sourceText: inner.SourceText);

        private void EmitSubGroup(SimpleSelector part)
        {
            var inner = part.SubList!;
            var aggregate = BuildAggregate(inner);

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
