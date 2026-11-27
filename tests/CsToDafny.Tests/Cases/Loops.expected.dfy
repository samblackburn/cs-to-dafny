// Loops.cs(42,13): warning: in-place collection update translated as a value update of the variable; other references to the same collection will not see it
// Loops.cs(51,13): warning: in-place collection update translated as a value update of the variable; other references to the same collection will not see it

module Translated {
  import opened CSharp

  // C# static class Algorithms.Loops
  class Loops {
    static method SumTo(n: int32) returns (result: int64)
    {
      var total: int64 := 0;
      for i: int32 := 0 to n {
        total := total + i as int64;
      }
      return total;
    }

    static method CountDown(n: int32) returns (result: int32)
    {
      var n': int32 := n;
      var steps: int32 := 0;
      while n' > 0 {
        n' := n' - 1;
        steps := steps + 1;
      }
      return steps;
    }

    static method IndexOf(xs: seq<int32>, target: int32) returns (result: int32)
    {
      for i: int32 := 0 to SeqCount(xs) {
        if xs[i] == target {
          return i;
        }
      }
      return -1;
    }

    static method Evens(xs: seq<int32>) returns (result: seq<int32>)
    {
      var result': seq<int32> := [];
      var tmp0 := xs;
      for tmp0i := 0 to |tmp0| {
        var x: int32 := tmp0[tmp0i];
        if Rem(x as int, 2) as int32 != 0 {
          continue;
        }
        result' := result' + [x];
      }
      return result';
    }

    static method Reverse(xs: seq<int32>) returns (result: seq<int32>)
    {
      var copy: seq<int32> := seq(SeqCount(xs), _ => 0);
      for i: int32 := SeqCount(xs) downto 0 {
        copy := copy[SeqCount(xs) - 1 - i := xs[i]];
      }
      return copy;
    }

    static method Bounds(xs: seq<int32>) returns (result: (int32, int32))
    {
      var lo: int32, hi: int32 := xs[0], xs[0];
      var tmp0 := xs;
      for tmp0i := 0 to |tmp0| {
        var x: int32 := tmp0[tmp0i];
        if x < lo {
          lo := x;
        }
        if x > hi {
          hi := x;
        }
      }
      return (lo, hi);
    }
  }
}
