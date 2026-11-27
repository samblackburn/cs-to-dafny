using Microsoft.CodeAnalysis;

namespace CsToDafny.Translation;

public enum IntegerMode
{
    /// <summary>C# fixed-width integers become bounded newtypes (int32, ...); overflow is a proof obligation.</summary>
    Bounded,
    /// <summary>C# integers become Dafny's mathematical int; overflow is ignored.</summary>
    Mathematical,
}

public enum ThrowMode
{
    /// <summary>throw becomes "assert false": Dafny must prove the throw is unreachable.</summary>
    Assert,
    /// <summary>throw becomes "expect false": a runtime check only, nothing to prove.</summary>
    Expect,
}

public sealed record ConverterOptions
{
    public IntegerMode Integers { get; init; } = IntegerMode.Bounded;
    public ThrowMode Throws { get; init; } = ThrowMode.Assert;

    /// <summary>Lift leading "if (cond) throw new Argument...Exception" guards into requires clauses.</summary>
    public bool GuardsAsPreconditions { get; init; } = true;

    /// <summary>Name of the Dafny module wrapping the translated code.</summary>
    public string ModuleName { get; init; } = "Translated";

    /// <summary>
    /// Namespaces or type names (prefix match) to translate in full. Other source types they use are emitted as
    /// signature-only stubs. Empty means translate everything.
    /// </summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Extra metadata references (paths to .dll files) needed to bind the source.</summary>
    public IReadOnlyList<string> References { get; init; } = [];
}

public enum Severity { Info, Warning, Error }

public sealed record ConversionDiagnostic(Severity Severity, string Message, string? Location)
{
    public override string ToString() => $"{Location ?? "<unknown>"}: {Severity.ToString().ToLowerInvariant()}: {Message}";
}

public sealed class Diagnostics
{
    private readonly List<ConversionDiagnostic> _items = [];
    private readonly HashSet<string> _once = [];

    public IReadOnlyList<ConversionDiagnostic> Items => _items;

    public void Add(Severity severity, string message, SyntaxNode? node) =>
        _items.Add(new ConversionDiagnostic(severity, message, Describe(node)));

    public void Warn(string message, SyntaxNode? node) => Add(Severity.Warning, message, node);

    public void Unsupported(string what, SyntaxNode node) =>
        Add(Severity.Error, $"unsupported: {what}: {Trim(node.ToString())}", node);

    /// <summary>Report a warning only the first time a given key is seen.</summary>
    public void WarnOnce(string key, string message, SyntaxNode? node)
    {
        if (_once.Add(key)) Warn(message, node);
    }

    private static string Trim(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s.Length > 80 ? s[..77] + "..." : s;
    }

    private static string? Describe(SyntaxNode? node)
    {
        if (node is null) return null;
        var span = node.GetLocation().GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }
}
