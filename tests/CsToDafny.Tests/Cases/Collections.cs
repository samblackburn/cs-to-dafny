// .NET collections become seq/set/map; LINQ becomes quantifiers and prelude functions.
// verify: 0
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Inventory;

public record Item(string Name, int Count);

public static class Stock
{
    public static bool Has(IReadOnlyList<Item> items, string name) => items.Any(i => i.Name == name);

    public static bool AllInStock(ImmutableList<Item> items) => items.All(i => i.Count > 0);

    public static IEnumerable<string> Names(IReadOnlyList<Item> items) => items.Select(i => i.Name);

    public static IReadOnlyList<Item> InStock(IReadOnlyList<Item> items) => items.Where(i => i.Count > 0).ToList();

    public static ImmutableHashSet<string> Including(ImmutableHashSet<string> names, string extra) => names.Add(extra);

    public static bool Known(IReadOnlySet<string> names, string n) => names.Contains(n);

    public static int CountFor(IReadOnlyDictionary<string, int> counts, string name) =>
        counts.ContainsKey(name) ? counts[name] : 0;

    public static ImmutableDictionary<string, int> Restock(ImmutableDictionary<string, int> counts, string name) =>
        counts.SetItem(name, 10);

    public static ImmutableList<Item> AddItem(ImmutableList<Item> items, Item item) => items.Add(item);

    public static int[] Pair(int a, int b) => [a, b];

    public static int First(IReadOnlyList<int> xs) => xs.Count > 0 ? xs[0] : -1;
}
