# cs2dfy: C# to Dafny

Lifts logic out of C# applications into [Dafny](https://dafny.org) so you can prove it is free of
whole classes of bugs: integer overflow, division by zero, null dereferences, out-of-range indexing,
missing dictionary keys, unhandled cases and violated argument checks.

The translation is designed to be **the inverse of the Dafny compiler**. Every C# construct maps to
the Dafny construct that Dafny's own C# backend would compile back to it. For example, C# `int` is
not translated to Dafny's unbounded `int` (which compiles to `BigInteger`). It becomes
`newtype int32 = x: int | -0x8000_0000 <= x < 0x8000_0000`, which Dafny compiles to C# `int`.
Choosing the faithful type is what makes C#'s failure modes visible to the verifier.

## Quick start

```sh
# Whole project: package and project references are loaded, so everything binds as it builds.
dotnet run --project src/cs2dfy -- path/to/App.csproj --include App.Pricing -o Pricing.dfy

# Loose files work too (references beyond the .NET runtime can be added with -r).
dotnet run --project src/cs2dfy -- samples/Pricing/Pricing.cs -o Pricing.dfy

dafny verify Pricing.dfy
```

Every verification error in the output points at a potential bug in the C#, or at a fact the
code relies on that Dafny can't see yet. You fix the latter by adding `requires`/`ensures`/loop
invariants to the generated Dafny. See [samples/Pricing](samples/Pricing) for a worked example.
Verifying it finds that a `BuyNGetOneFree` voucher with `N <= 0` makes pricing throw, that
`n + 1` can overflow, and that order totals can overflow `long`.

### Translating part of a large codebase

`--include <namespace-or-type>` (repeatable) translates just the logic you care about. Other
source types it uses are emitted as **signature-only stubs**. Records and enums keep their
constructors, and the members actually used are kept without bodies. Dafny treats a bodiless
function as uninterpreted and a bodiless method as arbitrary, so the selected logic is verified
against *any* implementation of its integrations. Instance members of stateful classes outside
the selection always become methods. That way the verifier never assumes, for example, that a
repository lookup returns the same answer twice.

## The mapping

| C# | Dafny | What Dafny then checks |
|---|---|---|
| `int`, `long`, `byte`, `uint`, ... | `int32`, `int64`, `uint8`, `uint32`, ... (bounded newtypes) | overflow on every operation and conversion |
| `BigInteger` | `int` | |
| `a / b`, `a % b` (signed) | `Div(a, b)`, `Rem(a, b)` (truncating, like C#) | division by zero, `MinValue / -1` |
| `double`, `float`, `decimal` | `real` (warned: rounding and NaN are not modelled) | division by zero |
| `bool`, `char`, `string` | `bool`, `char`, `string` | |
| `T?`, `Nullable<T>` | `Option<T>` for values, `C?` for class references | each dereference (`.value`) |
| `record` | `datatype` with one constructor | |
| `abstract record` + sealed derived records | one `datatype`, a constructor per derived record | exhaustive matches |
| `enum` | `datatype` with nullary constructors | |
| `switch` expression/statement, `is` patterns | `match`, or an `if` chain with `assert` for the unmatched case | that no arm is missing |
| `with { X = v }` | `r.(X := v)` | |
| `IReadOnlyList<T>`, `List<T>`, `ImmutableArray<T>`, `T[]`, ... | `seq<T>` | indexing in range |
| `ISet<T>`, `HashSet<T>`, `ImmutableHashSet<T>`, ... | `set<T>` | |
| `IDictionary<K,V>`, `ImmutableDictionary<K,V>`, ... | `map<K,V>` | key present on lookup |
| `(A, B)` tuples, `Func<A, B>` | `(A, B)`, `A -> B` | |
| LINQ `Any`/`All` | `exists`/`forall` | |
| LINQ `Select`/`Where`/`Sum`/`Aggregate`/`Skip`/`Take` | prelude functions with LINQ's semantics (checked `Sum`, clamping `Skip`/`Take`) | `Sum` overflow |
| `.Count`, `.Length` | `SeqCount(xs)` etc. (encodes that .NET sizes fit in an `int`) | |
| `interface`, `abstract class` | `trait` | |
| `class` | `class` with `var`/`const` fields; `modifies this` / `reads this` inferred | |
| `static class` | `class` with `static` members | |
| pure, expression-shaped method or computed property | `function` | termination |
| other methods | `method`; calls inside expressions are hoisted into temporaries | |
| leading `if (bad) throw new ArgumentException(...)`, `ThrowIfNull`, `ThrowIfNegative`, `Debug.Assert` | `requires` | callers satisfy it |
| any other `throw` | `assert false` (or `expect false` with `--throw-as-expect`) | that it is unreachable |
| `Debug.Assert(c)` | `assert c` | that it holds |

Overloaded methods are renamed (`Name_2`), multiple constructors become named constructors, and
identifiers that clash with Dafny keywords get a trailing `'`.

The prelude (module `CSharp` at the top of every output) holds the C# semantics models. It
verifies on its own.

## What is not translated

Anything the converter cannot translate faithfully is **reported, never dropped**. It appears in
the output as `UNTRANSLATED(/* original C# */)`, which Dafny rejects, and as an error diagnostic
with its source location. The current gaps include:

- `async`/`await`, iterators (`yield`), `try`/`catch`, local functions, `goto`
- bitwise operators and shifts on integers, `checked`/`unchecked` wrap-around semantics
- string formatting and `ToString()`, string interpolation of non-strings
- `OrderBy`, `GroupBy`, `Distinct`, `Min`/`Max` and other LINQ beyond the list above
- inheritance from non-abstract classes, user-defined operators and conversions, indexers
- records that implement interfaces (the interface is dropped, with a warning)
- validation logic in explicit record constructors (warned)

Mutable collections are modelled as values. `list.Add(x)` becomes `list := list + [x]`, which is
exact for a local list but not if the same list is shared through another reference. Each such
update is reported as a warning.

## Options

```
-o, --output <file>    write Dafny here (default: stdout)
-i, --include <name>   translate only this namespace or type (repeatable)
--module <name>        name of the generated module (default: Translated)
--math-ints            map C# integers to unbounded int (ignore overflow)
--throw-as-expect      translate throw as a runtime 'expect' instead of a proof obligation
--no-preconditions     do not lift argument-validation throws into 'requires'
-r, --reference <dll>  additional assembly needed to compile loose .cs files
```

## Layout

| Path | Purpose |
|---|---|
| `src/CsToDafny/Converter.cs` | Public API: `Converter.Convert(...)` from source text or a Roslyn compilation |
| `src/CsToDafny/Dafny/` | Dafny AST and a precedence-aware printer |
| `src/CsToDafny/Translation/SymbolModel.cs` | Which C# types become datatypes; naming |
| `src/CsToDafny/Translation/Classifier.cs` | Function vs method, decided per override group to a fixpoint over the call graph |
| `src/CsToDafny/Translation/Selection.cs` | `--include` and stub computation |
| `src/CsToDafny/Translation/TypeTranslator.cs` | Type mapping |
| `src/CsToDafny/Translation/MemberTranslator.*.cs` | Bodies, from Roslyn's `IOperation` tree: expressions, patterns, statements, function bodies, BCL mappings |
| `src/CsToDafny/Translation/DeclTranslator.cs` | Declarations: datatypes, classes, traits, dispatch of record-hierarchy members |
| `src/CsToDafny/Translation/Prelude.cs` | The `CSharp` module of semantic models |
| `src/cs2dfy/` | Command-line tool |
| `tests/CsToDafny.Tests/Cases/` | Golden tests |

## Tests

Each `tests/CsToDafny.Tests/Cases/*.cs` is converted and compared with its `.expected.dfy`. The
output must also contain no `UNTRANSLATED` holes, must pass `dafny resolve`, and, where the case
has a `// verify: N` comment, `dafny verify` must report exactly N errors. The cases plant real
bugs to show the verifier catches them, and include asserts that check the translation follows
C#'s semantics rather than Dafny's (for example, division of negative numbers). An `// include:`
comment exercises `--include`. Dafny 4.x must be on the `PATH` for the resolve and verify checks.

```sh
dotnet test                      # run
UPDATE_GOLDEN=1 dotnet test      # accept new output after reviewing the diff
```
