module Translated {
  import opened CSharp

  // C# static class Validation.Rules
  class Rules {
    static function Percent(part: int32, whole: int32): int32
      requires whole > 0
      requires part >= 0
      requires part <= whole
    {
      Div((part as int64 * 100) as int, (whole as int64) as int) as int32
    }

    static function Clamp(x: int32, lo: int32, hi: int32): int32
      requires lo <= hi
    {
      if x < lo then lo
      else if x > hi then hi else x
    }

    static function Grade(score: int32): string
      requires score >= 0
    {
      if score >= 90 then "A"
      else if score >= 50 then "B" else "C"
    }

    static function Half(): int32
    {
      Percent(1, 2)
    }

    static function Broken(whole: int32): int32
    {
      Percent(0, whole)
    }
  }
}
