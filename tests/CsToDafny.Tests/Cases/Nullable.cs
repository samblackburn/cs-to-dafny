// Nullable values become Option; dereferences become .value, which Dafny must prove safe.
// verify: 1
namespace Accounts;

public record Account(string Id, string? Nickname, int? Limit);

public static class Lookup
{
    public static string DisplayName(Account a) => a.Nickname ?? a.Id;

    public static int NicknameLength(Account a) => a.Nickname is null ? 0 : a.Nickname.Length;

    public static int EffectiveLimit(Account a) => a.Limit.HasValue ? a.Limit.Value : 0;

    public static bool HasNickname(Account a) => a.Nickname is not null;

    public static int? LimitPlusOne(Account a) => a.Limit is int l && l < 1000 ? l + 1 : null;

    public static int? NicknameLengthOrNull(Account a) => a.Nickname?.Length;

    // Bug: Nickname may be null. Dafny reports this dereference.
    public static int UnsafeLength(Account a) => a.Nickname!.Length;
}
