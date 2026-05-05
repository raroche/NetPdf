// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Cascade;

/// <summary>
/// Outcome of a <see cref="VarSubstitution.Substitute"/> call. Distinguishes a
/// successful substitution (every <c>var()</c> resolved or successfully fell back
/// — <see cref="IsInvalid"/> = <see langword="false"/>) from one that hit "invalid at
/// computed value time" semantics per CSS Custom Properties L1 §3.5 — a missing
/// reference with no fallback, a depth/output limit, or a member of a dependency
/// cycle. The caller decides how to react: for non-custom declarations the
/// <see cref="Value"/> already carries the <c>unset</c> sentinel that Tasks 9–10
/// typed-value parsers will resolve to the property's initial value; for
/// custom-property values the caller (<see cref="VarResolver"/>) marks the source
/// name invalid in the table so external <c>var()</c> references fall through to
/// THEIR fallback rather than picking up a stale "unset" string.
/// </summary>
/// <param name="Value">The substituted text. When <see cref="IsInvalid"/> is true,
/// equals <see cref="VarSubstitution.UnsetSentinel"/> (the <c>unset</c> keyword string).</param>
/// <param name="IsInvalid"><see langword="true"/> when the substitution couldn't
/// produce a valid value — used by callers that need to propagate "invalid at
/// computed value time" up the cascade (custom-property invalidation).</param>
internal readonly record struct SubstitutionResult(string Value, bool IsInvalid)
{
    /// <summary>Build a successful result.</summary>
    public static SubstitutionResult Valid(string value) => new(value, false);

    /// <summary>Build an "invalid at computed value time" result. Caller chooses what
    /// sentinel to surface to downstream stages — typically <see cref="VarSubstitution.UnsetSentinel"/>.</summary>
    public static SubstitutionResult Invalid(string sentinel) => new(sentinel, true);
}
