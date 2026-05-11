// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate.Diagnostics;

/// <summary>
/// Per Phase 3 Task 12 sub-cycle 2 hardening (Finding 5) — buffering
/// decorator around an <see cref="IPaginateDiagnosticsSink"/>. Captures
/// every <see cref="Emit"/> call into an internal list + exposes a
/// <see cref="FlushTo"/> method that drains the buffer to a target sink
/// in document order.
///
/// <para><b>Use case.</b> Speculative-measurement passes (e.g., the
/// table layouter's per-cell content layout via a nested block
/// layouter) emit diagnostics whose commit-status isn't known until
/// the outer break resolver decides whether to keep or discard the
/// work. Passing the live outer sink to the speculative pass leaks
/// diagnostics for UNCOMMITTED work (cell-internal feature-
/// unsupported codes from a cell whose table was rolled back by an
/// outer pagination decision). Wrapping with this decorator lets the
/// caller buffer cell-internal emits + flush only on commit.</para>
///
/// <para><b>Thread safety.</b> Not thread-safe. Designed for the
/// single-threaded measure+emit two-phase protocol used by the
/// table layouter. The sink the outer layout writes to is itself
/// thread-safe per <see cref="IPaginateDiagnosticsSink"/>'s contract,
/// but this decorator's internal list is single-writer.</para>
///
/// <para><b>No-throw contract.</b> Inherits the
/// <see cref="IPaginateDiagnosticsSink.Emit"/> "MUST NOT throw"
/// contract from the underlying interface. <see cref="Emit"/> only
/// appends to a <see cref="List{T}"/>; the buffer growth path can
/// throw <see cref="OutOfMemoryException"/>, which propagates per
/// the same rules <see cref="System.Collections.Generic.List{T}.Add"/>
/// follows.</para>
/// </summary>
internal sealed class BufferingDiagnosticsSink : IPaginateDiagnosticsSink
{
    private readonly List<PaginateDiagnostic> _buffer = new();

    /// <summary>Append a diagnostic to the internal buffer. The
    /// buffer is unbounded — callers are expected to flush or
    /// discard before it grows pathologically (e.g., the per-cell
    /// measure phase in <c>TableLayouter</c> only runs a single
    /// cell's worth of work between flush points).</summary>
    public void Emit(PaginateDiagnostic diagnostic)
    {
        _buffer.Add(diagnostic);
    }

    /// <summary>Drain every buffered diagnostic to
    /// <paramref name="target"/> in document order, then clear the
    /// buffer. A null target is a no-op (still clears the buffer —
    /// the caller has signaled "discard"). Honors the no-throw
    /// contract: if the target's <see cref="IPaginateDiagnosticsSink.Emit"/>
    /// throws (violating its own contract), the exception is swallowed
    /// + the remaining buffered diagnostics are still drained.</summary>
    public void FlushTo(IPaginateDiagnosticsSink? target)
    {
        if (target is null)
        {
            _buffer.Clear();
            return;
        }
        for (var i = 0; i < _buffer.Count; i++)
        {
            try
            {
                target.Emit(_buffer[i]);
            }
            catch
            {
                // Swallow per the no-throw contract — a misbehaving
                // sink can't break our own callers' flush flow.
            }
        }
        _buffer.Clear();
    }

    /// <summary>Read-only view of the buffered diagnostics (for tests
    /// + diagnostic-aware callers). The buffer is mutated by
    /// <see cref="Emit"/> + <see cref="FlushTo"/>; treat the snapshot
    /// as transient.</summary>
    public IReadOnlyList<PaginateDiagnostic> Buffered => _buffer;

    /// <summary>Discard every buffered diagnostic without forwarding
    /// to a target. Used when the caller's speculative work was
    /// rolled back + the outer pipeline shouldn't see the cell-level
    /// emissions.</summary>
    public void Discard()
    {
        _buffer.Clear();
    }
}
