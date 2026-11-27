// Interfaces become traits; classes keep their fields; mutation needs 'modifies'.
namespace Banking;

public interface IFeePolicy
{
    long Fee(long amount);
}

public sealed class FlatFee : IFeePolicy
{
    private readonly long _fee;
    public FlatFee(long fee) { _fee = fee; }
    public long Fee(long amount) => _fee;
}

public sealed class Wallet
{
    private long _balance;
    public int Transactions { get; private set; }

    public Wallet(long opening)
    {
        _balance = opening;
    }

    public long Balance => _balance;

    public void Deposit(long amount)
    {
        if (amount <= 0) throw new System.ArgumentOutOfRangeException(nameof(amount));
        _balance += amount;
        Transactions++;
    }

    public bool TryWithdraw(long amount, IFeePolicy policy)
    {
        var total = amount + policy.Fee(amount);
        if (total > _balance) return false;
        _balance -= total;
        Transactions++;
        return true;
    }
}

public static class Bank
{
    public const long MaxDeposit = 1_000_000;

    public static Wallet Open() => new Wallet(0);
}
