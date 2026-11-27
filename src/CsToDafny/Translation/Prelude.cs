namespace CsToDafny.Translation;

/// <summary>
/// Dafny definitions that model C#/.NET semantics. Each newtype here is exactly the
/// Dafny type that the Dafny compiler emits as the corresponding C# primitive, so
/// the translation is the inverse of compilation and overflow becomes a proof obligation.
/// </summary>
public static class Prelude
{
    public const string ModuleName = "CSharp";

    public const string Text = """
        // Models of C# semantics used by the translated code.
        module CSharp {
          newtype int8 = x: int | -0x80 <= x < 0x80
          newtype uint8 = x: int | 0 <= x < 0x100
          newtype int16 = x: int | -0x8000 <= x < 0x8000
          newtype uint16 = x: int | 0 <= x < 0x1_0000
          newtype int32 = x: int | -0x8000_0000 <= x < 0x8000_0000
          newtype uint32 = x: int | 0 <= x < 0x1_0000_0000
          newtype int64 = x: int | -0x8000_0000_0000_0000 <= x < 0x8000_0000_0000_0000
          newtype uint64 = x: int | 0 <= x < 0x1_0000_0000_0000_0000

          // Nullable values (T? / Nullable<T>). Dereferencing requires proving Some?.
          datatype Option<T> = None | Some(value: T)

          // C# integer division and remainder truncate toward zero; Dafny's / and % are Euclidean.
          function Div(a: int, b: int): int
            requires b != 0
          {
            var q := (if a < 0 then -a else a) / (if b < 0 then -b else b);
            if (a < 0) == (b < 0) then q else -q
          }

          function Rem(a: int, b: int): int
            requires b != 0
          {
            a - Div(a, b) * b
          }

          // .NET collections and strings hold at most int.MaxValue elements, so their Count fits in an int.
          function SeqCount<T>(xs: seq<T>): int32
            ensures SeqCount(xs) as int == |xs|
          {
            assume {:axiom} |xs| < 0x8000_0000;
            |xs| as int32
          }

          function SetCount<T>(xs: set<T>): int32
            ensures SetCount(xs) as int == |xs|
          {
            assume {:axiom} |xs| < 0x8000_0000;
            |xs| as int32
          }

          function MapCount<K, V>(m: map<K, V>): int32
            ensures MapCount(m) as int == |m|
          {
            assume {:axiom} |m| < 0x8000_0000;
            |m| as int32
          }

          // System.Linq.Enumerable over sequences.
          function Select<T, U>(xs: seq<T>, f: T --> U): seq<U>
            requires forall x | x in xs :: f.requires(x)
          {
            seq(|xs|, i requires 0 <= i < |xs| => f(xs[i]))
          }

          function Where<T>(xs: seq<T>, p: T --> bool): seq<T>
            requires forall x | x in xs :: p.requires(x)
          {
            if |xs| == 0 then [] else (if p(xs[0]) then [xs[0]] else []) + Where(xs[1..], p)
          }

          function Aggregate<T, A>(xs: seq<T>, seed: A, f: (A, T) -> A): A
          {
            if |xs| == 0 then seed else Aggregate(xs[1..], f(seed, xs[0]), f)
          }

          function Sum(xs: seq<int>): int
          {
            if |xs| == 0 then 0 else xs[0] + Sum(xs[1..])
          }
        }
        """;
}
