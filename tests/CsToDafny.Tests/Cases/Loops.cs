// Imperative code becomes Dafny methods: loops, mutation and compound assignment.
using System.Collections.Generic;

namespace Algorithms;

public static class Loops
{
    public static long SumTo(int n)
    {
        long total = 0;
        for (var i = 0; i < n; i++)
            total += i;
        return total;
    }

    public static int CountDown(int n)
    {
        var steps = 0;
        while (n > 0)
        {
            n--;
            steps++;
        }
        return steps;
    }

    public static int IndexOf(IReadOnlyList<int> xs, int target)
    {
        for (var i = 0; i < xs.Count; i++)
        {
            if (xs[i] == target) return i;
        }
        return -1;
    }

    public static List<int> Evens(IEnumerable<int> xs)
    {
        var result = new List<int>();
        foreach (var x in xs)
        {
            if (x % 2 != 0) continue;
            result.Add(x);
        }
        return result;
    }

    public static int[] Reverse(int[] xs)
    {
        var copy = new int[xs.Length];
        for (var i = xs.Length - 1; i >= 0; i--)
            copy[xs.Length - 1 - i] = xs[i];
        return copy;
    }

    public static (int Min, int Max) Bounds(IReadOnlyList<int> xs)
    {
        var (lo, hi) = (xs[0], xs[0]);
        foreach (var x in xs)
        {
            if (x < lo) lo = x;
            if (x > hi) hi = x;
        }
        return (lo, hi);
    }
}
