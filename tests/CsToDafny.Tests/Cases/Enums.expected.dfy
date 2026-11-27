module Translated {
  import opened CSharp

  // C# enum Traffic.Light
  datatype Light = Red | Amber | Green

  // C# static class Traffic.Signals
  class Signals {
    static function Next(l: Light): Light
    {
      match l
      case Red =>
        Light.Green
      case Green =>
        Light.Amber
      case Amber =>
        Light.Red
    }

    static function CanGo(l: Light): bool
    {
      l == Light.Green
    }

    static function MustStop(l: Light): bool
    {
      l.Red? || l.Amber?
    }

    static function Seconds(l: Light): int32
    {
      if l.Red? then 30
      else if l.Amber? then 3 else 25
    }
  }
}
