// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>What the diff runner should do for one invoice.</summary>
public enum VisualGateAction
{
    /// <summary>No reference committed and the gate isn't forced — stay inert (logged skip).</summary>
    Skip,

    /// <summary>The gate is active (a reference exists, or it's globally required) but it cannot run —
    /// a hard FAILURE, so CI cannot go green by silently not rasterizing.</summary>
    Fail,

    /// <summary>Rasterize + diff against the committed reference(s).</summary>
    Diff,
}

public readonly record struct VisualGateDecision(VisualGateAction Action, string Reason);

/// <summary>The activation policy for the visual gate (PR-242 review [P1]): the gate is INERT only while
/// there are no references AND it isn't forced. Once a reference exists for an invoice — or the build sets
/// <see cref="RequiredEnvVar"/> — a missing / unconfigured PDF rasterizer is a hard FAILURE, so a green CI
/// can never silently mean "the visual backend was never wired". Pure + table-tested.</summary>
public static class VisualGatePolicy
{
    /// <summary>Set this env var (to <c>1</c> / <c>true</c>) to FORCE the gate active even before any
    /// reference is committed — a missing rasterizer or reference then fails the build.</summary>
    public const string RequiredEnvVar = "NETPDF_VISUAL_REGRESSION_REQUIRED";

    public static bool IsRequiredByEnv()
    {
        var v = Environment.GetEnvironmentVariable(RequiredEnvVar)?.Trim();
        return v is not null && (v.Equals("1", StringComparison.Ordinal)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Decide the action for one invoice given whether a reference is committed, whether a PDF
    /// rasterizer is configured, and whether the gate is globally required.</summary>
    public static VisualGateDecision Decide(bool referenceExists, bool rasterizerAvailable, bool required)
    {
        if (!referenceExists && !required)
            return new VisualGateDecision(VisualGateAction.Skip, "no committed reference yet (gate inert)");
        // Active: a reference exists for this invoice, or the gate is forced required.
        if (!rasterizerAvailable)
            return new VisualGateDecision(VisualGateAction.Fail,
                "a reference exists (or the gate is required) but no PDF rasterizer is configured — "
                + "install + wire PDFium (see docker/README.md)");
        if (!referenceExists)
            return new VisualGateDecision(VisualGateAction.Fail,
                $"{RequiredEnvVar} is set but no reference is committed for this invoice");
        return new VisualGateDecision(VisualGateAction.Diff, "reference present and rasterizer available");
    }
}
