// C# integers are bounded newtypes: overflow, division by zero and int.MinValue / -1 are proof obligations.
// verify: 4
namespace Arith;

public static class Ops
{
    // Fine: the guards keep the sum in range.
    public static int SafeAdd(int a, int b) =>
        a > 0 && b > int.MaxValue - a ? int.MaxValue : a < 0 && b < int.MinValue - a ? int.MinValue : a + b;

    // Bug: may overflow.
    public static int Square(int x) => x * x;

    // Bugs: divisor may be zero, and int.MinValue / -1 overflows.
    public static int Ratio(int a, int b) => a / b;

    // Bug: int.MinValue / -1 overflows even though the divisor is non-zero.
    public static int Quotient(int a, int b) => b == 0 ? 0 : a / b;

    // C# truncates toward zero (Dafny's own / and % are Euclidean). Dafny must prove these asserts,
    // which checks the translation models C# semantics rather than Dafny's.
    public static void DivisionSemantics(int seven, int two)
    {
        System.Diagnostics.Debug.Assert(seven == 7 && two == 2);
        var minusSeven = -seven;
        System.Diagnostics.Debug.Assert(minusSeven / two == -3);
        System.Diagnostics.Debug.Assert(minusSeven % two == -1);
        System.Diagnostics.Debug.Assert(seven / -two == -3);
        System.Diagnostics.Debug.Assert(seven % -two == 1);
    }

    public static long Widen(int x) => x;

    public static uint Halve(uint x) => x / 2;
}
