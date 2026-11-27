module Translated {
  import opened CSharp

  // C# record Accounts.Account
  datatype Account = Account(Id: string, Nickname: Option<string>, Limit: Option<int32>)

  // C# static class Accounts.Lookup
  class Lookup {
    static function DisplayName(a: Account): string
    {
      if a.Nickname.Some? then a.Nickname.value else a.Id
    }

    static function NicknameLength(a: Account): int32
    {
      if a.Nickname.None? then 0 else SeqCount(a.Nickname.value)
    }

    static function EffectiveLimit(a: Account): int32
    {
      if a.Limit.Some? then a.Limit.value else 0
    }

    static function HasNickname(a: Account): bool
    {
      a.Nickname.Some?
    }

    static function LimitPlusOne(a: Account): Option<int32>
    {
      if a.Limit.Some? && a.Limit.value < 1000 then Some(a.Limit.value + 1) else None
    }

    static function NicknameLengthOrNull(a: Account): Option<int32>
    {
      if a.Nickname.Some? then Some(SeqCount(a.Nickname.value)) else None
    }

    static function UnsafeLength(a: Account): int32
    {
      SeqCount(a.Nickname.value)
    }
  }
}
