module Translated {
  import opened CSharp

  // C# record Shapes.Shape
  datatype Shape = Circle(Radius: int32) | Rect(W: int32, H: int32) | Triangle(A: int32, B: int32, C: int32) {
    function Corners(): int32
    {
      match this
      case Circle(_) =>
        0
      case Rect(_, _) =>
        4
      case Triangle(_, _, _) =>
        3
    }

    function Describe(): string
    {
      match this
      case Circle(_) =>
        "circle"
      case Rect(_, _) =>
        "shape"
      case Triangle(_, _, _) =>
        "shape"
    }

    // C# property IsEquilateral
    function IsEquilateral(): bool
      requires this.Triangle?
    {
      A == B && B == C
    }
  }

  // C# static class Shapes.Geometry
  class Geometry {
    static function Name(s: Shape): string
    {
      if s.Circle? && s.Radius == 0 then "point"
      else if s.Circle? && s.Radius > 100 then "big circle"
      else if s.Circle? then "circle"
      else if s.Rect? && s.W == s.H then "square"
      else if s.Rect? then "rectangle"
      else if s.Triangle? then (if s.IsEquilateral() then "equilateral" else "triangle") else "unknown"
    }

    static function Width(s: Shape): int32
    {
      match s
      case Circle(_) =>
        if s.Radius < 1000 && s.Radius > -1000 then 2 * s.Radius else 2000
      case Rect(_, _) =>
        s.W
      case Triangle(a, _, _) =>
        a
    }

    static function IsRound(s: Shape): bool
    {
      s.Circle?
    }

    static function RadiusOrZero(s: Shape): int32
    {
      if s.Circle? then s.Radius else 0
    }

    static function Area(r: Shape): int32
      requires r.Rect?
    {
      if r.W < 1000 && r.H < 1000 && r.W >= 0 && r.H >= 0 then r.W * r.H else 0
    }
  }
}
