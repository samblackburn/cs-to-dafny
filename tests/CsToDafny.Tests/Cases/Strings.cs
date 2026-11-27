// Strings are Dafny strings (sequences of char).
// verify: 0
namespace Text;

public static class Words
{
    public static bool IsBlank(string? s) => string.IsNullOrEmpty(s);

    public static string Greeting(string name) => $"Hello, {name}!";

    public static string Initial(string name) => name.Length > 0 ? name.Substring(0, 1) : "";

    public static bool IsCommand(string s) => s.StartsWith("/");

    public static bool HasSpace(string s) => s.Contains(' ');

    public static int Digit(char c) => c >= '0' && c <= '9' ? c - '0' : -1;

    public static string Join(string a, string b) => a + "," + b;
}
