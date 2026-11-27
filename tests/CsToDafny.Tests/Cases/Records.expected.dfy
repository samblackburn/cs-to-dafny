module Translated {
  import opened CSharp

  // C# record Geometry.Point
  datatype Point = Point(X: int32, Y: int32) {
    // C# property Origin
    static function Origin(): Point
    {
      Point(0, 0)
    }

    // C# property IsOrigin
    function IsOrigin(): bool
    {
      X == 0 && Y == 0
    }

    function Mirror(): Point
    {
      Point(Y, X)
    }
  }

  // C# record Geometry.Size
  datatype Size = Size(Width: int32, Height: int32) {
    // C# property IsSquare
    function IsSquare(): bool
    {
      Width == Height
    }
  }

  // C# record Geometry.Labelled
  datatype Labelled = Labelled(Label: string, At: Point, Kind: string)

  // C# static class Geometry.Shapes
  class Shapes {
    static function MoveRight(p: Point): Point
    {
      p.(X := if p.X < 1000 then p.X + 1 else p.X)
    }

    static function Square(side: int32): Size
    {
      Size(side, side)
    }

    static function Mark(label': string): Labelled
    {
      Labelled(label', Point.Origin(), "marker")
    }

    static function SameRow(a: Point, b: Point): bool
    {
      a.Y == b.Y
    }
  }
}
