module Translated {
  import opened CSharp

  // C# static class Arith.Ops
  class Ops {
    static function SafeAdd(a: int32, b: int32): int32
    {
      if a > 0 && b > 2147483647 - a then 2147483647
      else if a < 0 && b < -2147483648 - a then -2147483648 else a + b
    }

    static function Square(x: int32): int32
    {
      x * x
    }

    static function Ratio(a: int32, b: int32): int32
    {
      Div(a as int, b as int) as int32
    }

    static function Quotient(a: int32, b: int32): int32
    {
      if b == 0 then 0 else Div(a as int, b as int) as int32
    }

    static method DivisionSemantics(seven: int32, two: int32)
      requires seven == 7 && two == 2
    {
      var minusSeven: int32 := -seven;
      assert Div(minusSeven as int, two as int) as int32 == -3;
      assert Rem(minusSeven as int, two as int) as int32 == -1;
      assert Div(seven as int, -two as int) as int32 == -3;
      assert Rem(seven as int, -two as int) as int32 == 1;
    }

    static function Widen(x: int32): int64
    {
      x as int64
    }

    static function Halve(x: uint32): uint32
    {
      x / 2
    }
  }
}
