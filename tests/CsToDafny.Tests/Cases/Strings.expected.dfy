module Translated {
  import opened CSharp

  // C# static class Text.Words
  class Words {
    static function IsBlank(s: Option<string>): bool
    {
      s.None? || |s.value| == 0
    }

    static function Greeting(name: string): string
    {
      "Hello, " + name + "!"
    }

    static function Initial(name: string): string
    {
      if SeqCount(name) > 0 then name[0..1] else ""
    }

    static function IsCommand(s: string): bool
    {
      "/" <= s
    }

    static function HasSpace(s: string): bool
    {
      ' ' in s
    }

    static function Digit(c: char): int32
    {
      if c >= '0' && c <= '9' then c as int32 - '0' as int32 else -1
    }

    static function Join(a: string, b: string): string
    {
      a + "," + b
    }
  }
}
