module Translated {
  import opened CSharp

  // C# interface Banking.IFeePolicy
  trait IFeePolicy extends object {
    function Fee(amount: int64): int64
  }

  // C# class Banking.FlatFee
  class FlatFee extends IFeePolicy {
    const fee': int64

    constructor(fee: int64)
    {
      fee' := fee;
    }

    function Fee(amount: int64): int64
    {
      fee'
    }
  }

  // C# class Banking.Wallet
  class Wallet {
    var balance': int64
    var Transactions: int32

    constructor(opening: int64)
    {
      balance' := opening;
    }

    // C# property Balance
    function Balance(): int64
      reads this
    {
      balance'
    }

    method Deposit(amount: int64)
      requires amount > 0
      modifies this
    {
      balance' := balance' + amount;
      Transactions := Transactions + 1;
    }

    method TryWithdraw(amount: int64, policy: IFeePolicy) returns (result: bool)
      modifies this
    {
      var total: int64 := amount + policy.Fee(amount);
      if total > balance' {
        return false;
      }
      balance' := balance' - total;
      Transactions := Transactions + 1;
      return true;
    }
  }

  // C# static class Banking.Bank
  class Bank {
    static const MaxDeposit: int64 := 1000000

    static method Open() returns (result: Wallet)
    {
      var tmp0: Wallet := new Wallet(0);
      return tmp0;
    }
  }
}
