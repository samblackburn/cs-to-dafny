# Pricing sample

[`Pricing.cs`](Pricing.cs) is ordinary functional-style C# business logic: records, a sealed
discount hierarchy, an enum, switch expressions, LINQ, nullable values and a guard clause.
[`Pricing.dfy`](Pricing.dfy) is the converter's output, unedited:

```sh
dotnet run --project ../../src/cs2dfy -- Pricing.csproj -o Pricing.dfy
dafny verify Pricing.dfy
```

Dafny reports 10 potential problems, each one a real way the C# can fail:

| Where (C#) | Finding |
|---|---|
| `DiscountAmount` → `FreeItems(order, b.Sku, b.N)` | Nothing stops a `BuyNGetOneFree` voucher with `N <= 0`, so `FreeItems` throws `ArgumentOutOfRangeException` at runtime |
| `FreeItems`: `n + 1` | Overflows when `N == int.MaxValue` |
| `LineTotal`, `Subtotal`, `Money.Add`, discount and total arithmetic | `long` overflow (silent wrap-around in C#, or `OverflowException` from LINQ `Sum`) |
| `AverageQuantity`: `sum += items[i].Quantity` | `int` overflow for large quantities |

To make the program verify, record the assumptions the business actually relies on. For example,
make `BuyNGetOneFree` validate `N > 0`, or bound prices and quantities with `requires` clauses.
Then re-verify. Each remaining error is either a bug or an assumption not yet written down.
