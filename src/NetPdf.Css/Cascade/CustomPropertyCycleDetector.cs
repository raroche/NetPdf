// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Pre-substitution cycle detector for custom-property dependency graphs. Implements
/// CSS Custom Properties L1 §3.5: "If there's a cycle of dependencies between custom
/// properties, the properties involved in the cycle are <i>invalid at computed value
/// time</i>." Marks every cycle member invalid in the supplied
/// <see cref="CustomPropertyTable"/>; <see cref="VarSubstitution"/> then treats invalid
/// names as missing, falling back to the referencing var()'s fallback.
/// </summary>
/// <remarks>
/// <para>
/// Uses Tarjan's strongly connected components algorithm — O(V + E) over the dependency
/// graph. For typical documents (≤ 20 custom properties) this is trivial. For each SCC
/// of size &gt; 1 OR a singleton with a self-loop, every name in the SCC becomes invalid.
/// </para>
/// <para>
/// <b>Why pre-detect rather than guard during substitution?</b> The earlier
/// substitution-time visited-set guard correctly stopped infinite recursion BUT only
/// invalidated the chain that hit the cycle first; the OTHER cycle members still
/// resolved to "the value of the chain that won the race". Per spec they should ALL be
/// invalid — references from outside the cycle (e.g., a non-cyclic <c>color: var(--a)</c>
/// where --a is in a cycle) should fall through to the fallback. Pre-detection makes
/// every cycle member invalid before any substitution runs.
/// </para>
/// </remarks>
internal static class CustomPropertyCycleDetector
{
    /// <summary>Detect cycles across all custom properties reachable via the table's
    /// chain (own + every ancestor) and mark every cycle member as invalid in
    /// <paramref name="table"/>. Subsequent <see cref="CustomPropertyTable.TryGetValue"/>
    /// calls return false for invalid names so var() resolution falls through to fallback.
    /// Emits one <see cref="CssDiagnosticCodes.CssVarCircular001"/> per detected cycle
    /// (deduplicated across cycle members so a 3-name cycle emits one warning, not three).</summary>
    public static void DetectAndMarkInvalid(
        CustomPropertyTable table,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default)
    {
        ArgumentNullException.ThrowIfNull(table);

        // Build the dependency graph: name → set of names referenced by that name's value.
        // Names referenced via var() that AREN'T themselves declared anywhere on the
        // chain don't participate in cycles — they'd just be "missing fallback" cases at
        // substitution time, not cycle members.
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in table.AllReachableNames())
        {
            if (!table.TryGetValue(name, out var value)) continue;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            ExtractVarReferences(value, refs);
            deps[name] = refs;
        }

        // Tarjan's SCC — O(V + E).
        var sccs = TarjanScc(deps);
        foreach (var scc in sccs)
        {
            // Cycle members: SCC of size > 1, OR a singleton that self-references.
            var isCycle = scc.Count > 1
                || (scc.Count == 1 && deps.TryGetValue(scc[0], out var d) && d.Contains(scc[0]));
            if (!isCycle) continue;
            foreach (var name in scc)
            {
                table.MarkInvalid(name);
            }
            // One diagnostic per cycle, listing the members for traceability.
            scc.Sort(StringComparer.Ordinal); // stable ordering for the message
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssVarCircular001,
                $"Custom property cycle detected: {string.Join(" → ", scc)}. All cycle members are invalid; references resolve to fallback or unset.",
                CssDiagnosticSeverity.Warning,
                location));
        }
    }

    /// <summary>Tarjan's strongly connected components algorithm. Returns each SCC as a
    /// list of node names. Nodes not in <paramref name="graph"/> are ignored — they're
    /// references to undeclared properties, which are handled at substitution time
    /// (fall back to the var()'s fallback).</summary>
    private static List<List<string>> TarjanScc(Dictionary<string, HashSet<string>> graph)
    {
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var sccs = new List<List<string>>();

        foreach (var node in graph.Keys)
        {
            if (!indices.ContainsKey(node))
                StrongConnect(node, graph, ref index, stack, onStack, indices, lowlinks, sccs);
        }
        return sccs;
    }

    /// <summary>Iterative variant of Tarjan's recursive <c>strongconnect</c> step.
    /// Iterative form avoids stack-overflow risk on deep dependency chains.</summary>
    private static void StrongConnect(
        string root,
        Dictionary<string, HashSet<string>> graph,
        ref int index,
        Stack<string> stack,
        HashSet<string> onStack,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowlinks,
        List<List<string>> sccs)
    {
        // Each frame on this iterative stack represents the recursive call's local state.
        // `Enter` is intentionally NOT a local function — local functions can't capture
        // a `ref` parameter via closure, but we need `index` to be a single counter
        // shared across the whole traversal. Inlined directly into the loop body below.
        var callStack = new Stack<(string Node, IEnumerator<string> Iter)>();

        // Initial Enter for `root` — duplicated here + on the path-extension branch below.
        indices[root] = index;
        lowlinks[root] = index;
        index++;
        stack.Push(root);
        onStack.Add(root);
        {
            var deps = graph.TryGetValue(root, out var d) ? d : (IEnumerable<string>)Array.Empty<string>();
            callStack.Push((root, deps.GetEnumerator()));
        }

        while (callStack.Count > 0)
        {
            var (node, iter) = callStack.Peek();
            if (iter.MoveNext())
            {
                var neighbor = iter.Current;
                if (!graph.ContainsKey(neighbor))
                    continue; // reference to undeclared name — not part of cycle graph
                if (!indices.ContainsKey(neighbor))
                {
                    // Inlined Enter(neighbor) — see comment above the initial Enter for why.
                    indices[neighbor] = index;
                    lowlinks[neighbor] = index;
                    index++;
                    stack.Push(neighbor);
                    onStack.Add(neighbor);
                    var deps = graph.TryGetValue(neighbor, out var d) ? d : (IEnumerable<string>)Array.Empty<string>();
                    callStack.Push((neighbor, deps.GetEnumerator()));
                }
                else if (onStack.Contains(neighbor))
                {
                    lowlinks[node] = Math.Min(lowlinks[node], indices[neighbor]);
                }
                continue;
            }
            // All neighbors processed for `node`. Pop frame.
            callStack.Pop();
            // If `node` is a root of an SCC, pop it off the SCC stack.
            if (lowlinks[node] == indices[node])
            {
                var scc = new List<string>();
                while (true)
                {
                    var w = stack.Pop();
                    onStack.Remove(w);
                    scc.Add(w);
                    if (w == node) break;
                }
                sccs.Add(scc);
            }
            // Propagate lowlink up to caller.
            if (callStack.Count > 0)
            {
                var (parent, _) = callStack.Peek();
                lowlinks[parent] = Math.Min(lowlinks[parent], lowlinks[node]);
            }
        }
    }

    /// <summary>Walk a raw value and add every <c>var(--name, ...)</c>'s name to
    /// <paramref name="output"/>. Quote-aware so var()-looking text inside string
    /// literals is ignored. Recurses into fallback contents so dependencies through
    /// fallbacks are also tracked — if --a is in a cycle through --b's fallback, the
    /// cycle still gets caught.</summary>
    private static void ExtractVarReferences(string value, HashSet<string> output)
    {
        if (string.IsNullOrEmpty(value)) return;
        var pos = 0;
        while (pos < value.Length)
        {
            var c = value[pos];
            if (c == '"' || c == '\'')
            {
                pos = SkipString(value, pos);
                continue;
            }
            if (c == 'v' && pos + 4 <= value.Length
                && value[pos + 1] == 'a' && value[pos + 2] == 'r' && value[pos + 3] == '(')
            {
                var bodyStart = pos + 4;
                var bodyEnd = FindMatchingCloseParen(value, bodyStart);
                if (bodyEnd < 0) return;
                var body = value[bodyStart..bodyEnd];
                // Read the name (everything before the first top-level comma).
                var (name, _) = SplitOnTopLevelComma(body);
                var trimmedName = name.Trim();
                if (trimmedName.StartsWith("--", StringComparison.Ordinal))
                    output.Add(trimmedName);
                // Continue scanning the body so nested var() in fallbacks are also
                // captured as dependencies. The body already contains the full
                // fallback text including nested var()s.
                ExtractVarReferences(body, output);
                pos = bodyEnd + 1;
                continue;
            }
            pos++;
        }
    }

    private static int FindMatchingCloseParen(string text, int start)
    {
        int depth = 1;
        var pos = start;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '"' || c == '\'') { pos = SkipString(text, pos); continue; }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return pos; }
            pos++;
        }
        return -1;
    }

    private static (string Name, string? Fallback) SplitOnTopLevelComma(string body)
    {
        int depth = 0;
        var pos = 0;
        while (pos < body.Length)
        {
            var c = body[pos];
            if (c == '"' || c == '\'') { pos = SkipString(body, pos); continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return (body[..pos], body[(pos + 1)..]);
            pos++;
        }
        return (body, null);
    }

    private static int SkipString(string text, int start)
    {
        var quote = text[start];
        var pos = start + 1;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '\\' && pos + 1 < text.Length) { pos += 2; continue; }
            pos++;
            if (c == quote) return pos;
        }
        return pos;
    }
}
