// A sealed record hierarchy becomes one datatype; abstract members dispatch with match.
// verify: 0
namespace Shapes;

public abstract record Shape
{
    public abstract int Corners();
    public virtual string Describe() => "shape";
}

public sealed record Circle(int Radius) : Shape
{
    public override int Corners() => 0;
    public override string Describe() => "circle";
}

public sealed record Rect(int W, int H) : Shape
{
    public override int Corners() => 4;
}

public sealed record Triangle(int A, int B, int C) : Shape
{
    public override int Corners() => 3;
    public bool IsEquilateral => A == B && B == C;
}

public static class Geometry
{
    public static string Name(Shape s) => s switch
    {
        Circle { Radius: 0 } => "point",
        Circle c when c.Radius > 100 => "big circle",
        Circle => "circle",
        Rect(var w, var h) when w == h => "square",
        Rect => "rectangle",
        Triangle t => t.IsEquilateral ? "equilateral" : "triangle",
        _ => "unknown",
    };

    public static int Width(Shape s) => s switch
    {
        Circle c => c.Radius < 1000 && c.Radius > -1000 ? 2 * c.Radius : 2000,
        Rect r => r.W,
        Triangle(var a, _, _) => a,
    };

    public static bool IsRound(Shape s) => s is Circle;

    public static int RadiusOrZero(Shape s) => s is Circle c ? c.Radius : 0;

    // Linear arithmetic only: non-linear facts (like W * H < 2^31) are not proved reliably by the solver.
    public static int Perimeter(Rect r) => r.W < 1000 && r.H < 1000 && r.W >= 0 && r.H >= 0 ? 2 * (r.W + r.H) : 0;
}
