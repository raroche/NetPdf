// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;

namespace NetPdf.UnitTests;

/// <summary>
/// Phase 0 smoke test. Confirms the test infrastructure builds and a single
/// xUnit assertion runs. Real test cases land per-phase as algorithms ship.
/// </summary>
public sealed class PhaseZeroSmoke
{
    [Fact]
    public void Solution_Compiles_And_Tests_Run()
    {
        Assert.True(true);
    }
}
