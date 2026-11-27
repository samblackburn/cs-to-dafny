// Records become datatypes; computed properties become functions; 'with' becomes datatype update.
// verify: 0
namespace Geometry;

public record Point(int X, int Y)
{
    public static Point Origin => new(0, 0);
    public bool IsOrigin => X == 0 && Y == 0;
    public Point Mirror() => new(Y, X);
}

public record Size
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public bool IsSquare => Width == Height;
}

public record Labelled(string Label, Point At)
{
    public string Kind { get; init; } = "marker";
}

public static class Shapes
{
    public static Point MoveRight(Point p) => p with { X = p.X < 1000 ? p.X + 1 : p.X };
    public static Size Square(int side) => new() { Width = side, Height = side };
    public static Labelled Mark(string label) => new(label, Point.Origin);
    public static bool SameRow(Point a, Point b) => a.Y == b.Y;
}
