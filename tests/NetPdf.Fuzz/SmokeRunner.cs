// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using static NetPdf.Fuzz.FuzzTargets;

namespace NetPdf.Fuzz;

/// <summary>
/// The deterministic, no-instrumentation fuzz pass CI runs on every PR. It replays the
/// hand-authored <see cref="SeedCorpus"/> plus a fixed number of <b>reproducible</b>
/// mutations per seed (a seeded LCG — no <c>Math.Random</c>, so a green run stays green and a
/// finding reproduces exactly), asserting that no target throws an unexpected exception or
/// hangs. It is NOT a substitute for a real libFuzzer campaign (that needs
/// <c>sharpfuzz</c> instrumentation — see <c>docs/security/fuzzing.md</c>); it is the cheap,
/// always-on smoke gate that catches regressions in the security-critical entry points.
/// </summary>
internal static class SmokeRunner
{
    // A hung Convert cannot be cancelled from outside, so the watchdog is generous: every
    // target run is sub-second in practice (the DoS caps reject hostile input early), so a
    // breach of this bound is a genuine hang, not a slow-but-fine render.
    private const int PerRunTimeoutMs = 20_000;

    internal readonly record struct Finding(Target Target, string Label, int Mutation, string Kind, string Detail);

    /// <summary>Run the smoke pass. Returns 0 when every target survived every input, else 1.</summary>
    internal static int Run(int mutationsPerSeed, TextWriter log)
    {
        var seeds = SeedCorpus.All;
        var findings = new List<Finding>();
        var runs = 0;

        log.WriteLine($"[fuzz-smoke] {seeds.Count} seeds x (1 + {mutationsPerSeed} mutations) across {All.Length} targets");

        foreach (var seed in seeds)
        {
            // The pristine seed first, then deterministic mutations derived from it.
            Exercise(seed.Target, seed.Label, mutation: 0, seed.Payload, findings, ref runs);

            var rng = new Lcg(HashLabel(seed.Label)); // per-seed stream → stable, seed-independent
            for (var m = 1; m <= mutationsPerSeed; m++)
            {
                var mutated = Mutate(seed.Payload, rng);
                Exercise(seed.Target, seed.Label, m, mutated, findings, ref runs);
            }
        }

        log.WriteLine($"[fuzz-smoke] {runs} runs complete, {findings.Count} finding(s)");
        foreach (var f in findings)
        {
            log.WriteLine($"  FINDING {f.Target}/{f.Label}#{f.Mutation} [{f.Kind}] {f.Detail}");
        }

        return findings.Count == 0 ? 0 : 1;
    }

    private static void Exercise(Target target, string label, int mutation, byte[] payload, List<Finding> findings, ref int runs)
    {
        runs++;
        // Qualify: this class's own Run(int, TextWriter) would otherwise shadow the target dispatcher.
        var task = Task.Run(() => FuzzTargets.Run(target, payload.AsSpan()));
        try
        {
            if (!task.Wait(PerRunTimeoutMs))
            {
                // The task is abandoned; it dies with the process. A hang is still a finding.
                findings.Add(new Finding(target, label, mutation, "hang", $"exceeded {PerRunTimeoutMs} ms"));
            }
        }
        catch (AggregateException agg)
        {
            var inner = agg.InnerException ?? agg;
            findings.Add(new Finding(target, label, mutation, "throw", $"{inner.GetType().Name}: {Truncate(inner.Message)}"));
        }
    }

    // --- deterministic mutation ------------------------------------------------------

    // Cap mutated payloads so a runaway "extend" can't defeat the input-size caps by growing
    // without bound between runs (the engine caps still apply per run; this bounds our own work).
    private const int MaxMutatedBytes = 2 * 1024 * 1024;

    private static byte[] Mutate(byte[] seed, Lcg rng)
    {
        var buf = (byte[])seed.Clone();
        var op = rng.Next() % 5;

        switch (op)
        {
            case 0 when buf.Length > 0: // bit flip
                buf[rng.Next() % buf.Length] ^= (byte)(1 << (int)(rng.Next() % 8));
                return buf;

            case 1 when buf.Length > 0: // set a byte to a boundary value
                buf[rng.Next() % buf.Length] = BoundaryByte(rng);
                return buf;

            case 2 when buf.Length > 1: // truncate
                return buf[..(int)(rng.Next() % buf.Length)];

            case 3: // append boundary bytes
            {
                var extra = 1 + (int)(rng.Next() % 64);
                var grown = new byte[Math.Min(buf.Length + extra, MaxMutatedBytes)];
                buf.AsSpan(0, Math.Min(buf.Length, grown.Length)).CopyTo(grown);
                for (var i = buf.Length; i < grown.Length; i++)
                {
                    grown[i] = BoundaryByte(rng);
                }

                return grown;
            }

            default: // duplicate a prefix (structure-preserving amplification)
            {
                if (buf.Length == 0)
                {
                    return buf;
                }

                var take = 1 + (int)(rng.Next() % buf.Length);
                var grown = new byte[Math.Min(buf.Length + take, MaxMutatedBytes)];
                buf.CopyTo(grown, 0);
                buf.AsSpan(0, Math.Min(take, grown.Length - buf.Length)).CopyTo(grown.AsSpan(buf.Length));
                return grown;
            }
        }
    }

    private static byte BoundaryByte(Lcg rng) => (rng.Next() % 6) switch
    {
        0 => 0x00,
        1 => 0xFF,
        2 => 0x7F,
        3 => 0x80,
        4 => (byte)'<',
        _ => (byte)'&',
    };

    private static uint HashLabel(string label)
    {
        // FNV-1a — stable across runs/platforms (no string.GetHashCode randomization).
        uint hash = 2166136261;
        foreach (var ch in label)
        {
            hash = (hash ^ ch) * 16777619;
        }

        return hash | 1; // non-zero seed
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>A tiny deterministic PRNG (Numerical-Recipes LCG). Reproducible, no global state.</summary>
    private sealed class Lcg(uint seed)
    {
        private uint _state = seed;

        internal uint Next()
        {
            _state = (1664525 * _state) + 1013904223;
            return _state & 0x7FFFFFFF;
        }
    }
}
