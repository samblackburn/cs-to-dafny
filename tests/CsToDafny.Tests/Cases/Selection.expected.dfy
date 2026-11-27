module Translated {
  import opened CSharp

  // C# record Billing.Customer
  // Outside the --include selection: only the members the selection uses, without bodies.
  datatype Customer = Customer(Id: string, LoyaltyYears: int32)

  // C# interface Billing.IClock
  // Outside the --include selection: only the members the selection uses, without bodies.
  trait IClock extends object {
    method CurrentYear() returns (result: int32)
  }

  // C# class Billing.CustomerRepository
  // Outside the --include selection: only the members the selection uses, without bodies.
  class CustomerRepository {
    method Find(id: string) returns (result: Option<Customer>)
  }

  // C# static class Billing.Rules
  class Rules {
    static function DiscountPercent(c: Customer): int32
    {
      if c.LoyaltyYears >= 10 then 15
      else if c.LoyaltyYears >= 5 then 10 else 0
    }

    static method IsAnniversaryYear(c: Customer, clock: IClock, joined: int32) returns (result: bool)
    {
      var year: int32 := clock.CurrentYear();
      return year > joined && year - joined < 1000 && Rem((year - joined) as int, 5) as int32 == 0;
    }

    static method DiscountFor(repo: CustomerRepository, id: string) returns (result: int32)
    {
      var c: Option<Customer> := repo.Find(id);
      return if c.None? then 0 else DiscountPercent(c.value);
    }
  }
}
