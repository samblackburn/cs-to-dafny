// Argument validation at the start of a method becomes a precondition; other throws become assertions.
// verify: 1
using System;
using System.Diagnostics;

namespace Validation;

public static class Rules
{
    public static int Percent(int part, int whole)
    {
        if (whole <= 0) throw new ArgumentOutOfRangeException(nameof(whole));
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(part, whole);
        return (int)((long)part * 100 / whole);
    }

    public static int Clamp(int x, int lo, int hi)
    {
        if (lo > hi) throw new ArgumentException("empty range");
        return x < lo ? lo : x > hi ? hi : x;
    }

    public static string Grade(int score)
    {
        Debug.Assert(score >= 0);
        if (score >= 90) return "A";
        if (score >= 50) return "B";
        return "C";
    }

    // Callers satisfy the preconditions, so this verifies.
    public static int Half() => Percent(1, 2);

    // Bug: Percent requires whole > 0.
    public static int Broken(int whole) => Percent(0, whole);
}
