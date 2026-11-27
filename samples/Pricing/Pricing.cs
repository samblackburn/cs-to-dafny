using System;
using System.Collections.Immutable;
using System.Linq;

namespace Shop.Pricing;

public enum CustomerTier { Standard, Silver, Gold }

public record Money(long Pence)
{
    public Money Add(Money other) => new(Pence + other.Pence);
    public bool IsZero => Pence == 0;
}

public record LineItem(string Sku, int Quantity, Money UnitPrice);

public abstract record Discount;
public sealed record PercentOff(int Percent) : Discount;
public sealed record FixedOff(Money Amount) : Discount;
public sealed record BuyNGetOneFree(string Sku, int N) : Discount;

public record Order(ImmutableList<LineItem> Items, CustomerTier Tier, Discount? Voucher);

public static class PriceCalculator
{
    public static long LineTotal(LineItem item) => item.Quantity * item.UnitPrice.Pence;

    public static long Subtotal(Order order) => order.Items.Sum(i => LineTotal(i));

    public static int TierPercent(CustomerTier tier) => tier switch
    {
        CustomerTier.Standard => 0,
        CustomerTier.Silver => 5,
        CustomerTier.Gold => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    public static long DiscountAmount(Discount discount, Order order) => discount switch
    {
        PercentOff p => Subtotal(order) * p.Percent / 100,
        FixedOff f => f.Amount.Pence,
        BuyNGetOneFree b => FreeItems(order, b.Sku, b.N),
        _ => 0,
    };

    public static long FreeItems(Order order, string sku, int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        var matching = order.Items.Where(i => i.Sku == sku).ToList();
        if (matching.Count == 0) return 0;
        var first = matching[0];
        return first.Quantity / (n + 1) * first.UnitPrice.Pence;
    }

    public static long Total(Order order)
    {
        var subtotal = Subtotal(order);
        var tierDiscount = subtotal * TierPercent(order.Tier) / 100;
        var voucherDiscount = order.Voucher is null ? 0 : DiscountAmount(order.Voucher, order);
        var total = subtotal - tierDiscount - voucherDiscount;
        return Math.Max(total, 0);
    }

    public static string? CheapestSku(Order order)
    {
        if (order.Items.IsEmpty) return null;
        var best = order.Items[0];
        foreach (var item in order.Items)
        {
            if (item.UnitPrice.Pence < best.UnitPrice.Pence)
                best = item;
        }
        return best.Sku;
    }

    public static int? AverageQuantity(ImmutableList<LineItem> items)
    {
        if (items.Count == 0) return null;
        var sum = 0;
        for (var i = 0; i < items.Count; i++)
            sum += items[i].Quantity;
        return sum / items.Count;
    }

    public static bool AllPositive(Order order) => order.Items.All(i => i.Quantity > 0 && i.UnitPrice.Pence >= 0);

    public static Order WithTier(Order order, CustomerTier tier) => order with { Tier = tier };
}
