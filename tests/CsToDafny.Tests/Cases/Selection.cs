// Only Billing.Rules is translated. The integrations it uses (IClock, CustomerRepository) become
// signature-only stubs, and code it does not use (ReportPrinter) is left out entirely.
// include: Billing.Rules
// verify: 1
using System;
using System.Collections.Generic;

namespace Billing;

public record Customer(string Id, int LoyaltyYears);

public interface IClock
{
    int CurrentYear();
}

public sealed class CustomerRepository
{
    private readonly Dictionary<string, Customer> _store = new();
    public Customer? Find(string id) => _store.TryGetValue(id, out var c) ? c : null;
    public void Save(Customer c) => _store[c.Id] = c;
}

public sealed class ReportPrinter
{
    public void Print(string text) => Console.WriteLine(text);
}

public static class Rules
{
    public static int DiscountPercent(Customer c) => c.LoyaltyYears >= 10 ? 15 : c.LoyaltyYears >= 5 ? 10 : 0;

    // Bug: year - joined overflows when joined is very negative.
    public static bool IsAnniversaryYear(Customer c, IClock clock, int joined)
    {
        var year = clock.CurrentYear();
        return year > joined && year - joined < 1000 && (year - joined) % 5 == 0;
    }

    public static int DiscountFor(CustomerRepository repo, string id)
    {
        var c = repo.Find(id);
        return c is null ? 0 : DiscountPercent(c);
    }
}
