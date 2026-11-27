module Translated {
  import opened CSharp

  // C# record Inventory.Item
  datatype Item = Item(Name: string, Count: int32)

  // C# static class Inventory.Stock
  class Stock {
    static function Has(items: seq<Item>, name: string): bool
    {
      exists i: Item :: i in items && i.Name == name
    }

    static function AllInStock(items: seq<Item>): bool
    {
      forall i: Item :: i in items ==> i.Count > 0
    }

    static function Names(items: seq<Item>): seq<string>
    {
      Select(items, (i: Item) => i.Name)
    }

    static function InStock(items: seq<Item>): seq<Item>
    {
      Where(items, (i: Item) => i.Count > 0)
    }

    static function Including(names: set<string>, extra: string): set<string>
    {
      names + {extra}
    }

    static function Known(names: set<string>, n: string): bool
    {
      n in names
    }

    static function CountFor(counts: map<string, int32>, name: string): int32
    {
      if name in counts then counts[name] else 0
    }

    static function Restock(counts: map<string, int32>, name: string): map<string, int32>
    {
      counts[name := 10]
    }

    static function AddItem(items: seq<Item>, item: Item): seq<Item>
    {
      items + [item]
    }

    static function Pair(a: int32, b: int32): seq<int32>
    {
      [a, b]
    }

    static function First(xs: seq<int32>): int32
    {
      if SeqCount(xs) > 0 then xs[0] else -1
    }
  }
}
