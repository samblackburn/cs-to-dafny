using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>
/// Which source types are translated in full (the --include selection), and which other source types and
/// members the selection uses. Those are emitted as signature-only stubs: Dafny treats a bodiless function as
/// uninterpreted and a bodiless method as arbitrary, so the selected logic is verified against any implementation.
/// </summary>
public sealed class Selection
{
    private readonly SymbolModel _symbols;
    private readonly IReadOnlyList<string> _patterns;
    private readonly HashSet<INamedTypeSymbol> _types = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _members = new(SymbolEqualityComparer.Default);

    public Selection(SymbolModel symbols, IReadOnlyList<string> patterns)
    {
        _symbols = symbols;
        _patterns = patterns;
        if (patterns.Count > 0) ComputeReferences();
    }

    public bool SelectsEverything => _patterns.Count == 0;

    /// <summary>Translated in full.</summary>
    public bool IsIncluded(INamedTypeSymbol t)
    {
        if (_patterns.Count == 0) return true;
        t = t.OriginalDefinition;
        // A record hierarchy is one datatype: include it whole if any part is selected.
        if (_symbols.DatatypeOf(t) is { } dt && !dt.IsEnum)
            return dt.Ctors.Select(c => c.Type!).Prepend(dt.Root).Any(Matches);
        return Matches(t);
    }

    /// <summary>Emitted at all: included, or used by included code.</summary>
    public bool IsEmitted(INamedTypeSymbol t) => IsIncluded(t) || _types.Contains(t.OriginalDefinition) ||
        _symbols.DatatypeOf(t) is { } dt && _types.Contains(dt.Root);

    /// <summary>For a stub type, whether a member is used by included code.</summary>
    public bool IsMemberEmitted(ISymbol member) => IsIncluded(member.ContainingType) || _members.Contains(member.OriginalDefinition);

    private bool Matches(INamedTypeSymbol t)
    {
        var full = t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));
        return _patterns.Any(p => full == p || full.StartsWith(p + ".", StringComparison.Ordinal));
    }

    private void ComputeReferences()
    {
        var work = new Queue<ISymbol>();
        void UseType(ITypeSymbol? t)
        {
            switch (t)
            {
                case INamedTypeSymbol n:
                    foreach (var a in n.TypeArguments) UseType(a);
                    var def = n.OriginalDefinition;
                    if (_symbols.IsSourceType(def) && !IsIncluded(def) && _types.Add(def)) work.Enqueue(def);
                    break;
                case IArrayTypeSymbol a:
                    UseType(a.ElementType);
                    break;
            }
        }
        void UseMember(ISymbol m)
        {
            m = m.OriginalDefinition;
            if (m is IMethodSymbol { AssociatedSymbol: IPropertySymbol p }) m = p;
            if (m.ContainingType is null || !_symbols.IsSourceType(m.ContainingType) || IsIncluded(m.ContainingType)) return;
            UseType(m.ContainingType);
            if (_members.Add(m)) work.Enqueue(m);
        }
        void UseBody(IOperation? body)
        {
            if (body is null) return;
            foreach (var op in body.DescendantsAndSelf())
            {
                UseType(op.Type);
                switch (op)
                {
                    case IInvocationOperation inv: UseMember(inv.TargetMethod); break;
                    case IMemberReferenceOperation mr: UseMember(mr.Member); break;
                    case IObjectCreationOperation { Constructor: { } c }: UseMember(c); break;
                }
            }
        }

        foreach (var t in _symbols.SourceTypes.Where(IsIncluded))
        {
            foreach (var m in t.GetMembers())
            {
                switch (m)
                {
                    case IMethodSymbol method:
                        foreach (var p in method.Parameters) UseType(p.Type);
                        UseType(method.ReturnType);
                        UseBody(Classifier.BodyOf(method, _symbols));
                        break;
                    case IPropertySymbol prop:
                        UseType(prop.Type);
                        if (prop.GetMethod is { } g) UseBody(Classifier.BodyOf(g, _symbols));
                        break;
                    case IFieldSymbol field:
                        UseType(field.Type);
                        break;
                }
            }
            foreach (var i in t.AllInterfaces) UseType(i);
            UseType(t.BaseType);
        }

        while (work.Count > 0)
        {
            switch (work.Dequeue())
            {
                case INamedTypeSymbol t:
                    foreach (var i in t.Interfaces) UseType(i);
                    UseType(t.BaseType);
                    if (_symbols.DatatypeOf(t) is { } dt)
                        foreach (var c in dt.Ctors)
                        foreach (var f in c.Fields)
                            UseType(f.Type);
                    // A stub class must still implement the members it inherits from stub traits.
                    foreach (var iface in t.AllInterfaces)
                    foreach (var im in iface.GetMembers().Where(_members.Contains))
                        if (t.FindImplementationForInterfaceMember(im) is { } impl) UseMember(impl);
                    break;
                case IMethodSymbol m:
                    foreach (var p in m.Parameters) UseType(p.Type);
                    UseType(m.ReturnType);
                    // Implementations of a used interface member in other stub types must exist too.
                    foreach (var t in _types.ToList())
                        if (t.FindImplementationForInterfaceMember(m) is { } impl2) UseMember(impl2);
                    break;
                case IPropertySymbol p:
                    UseType(p.Type);
                    foreach (var t in _types.ToList())
                        if (t.FindImplementationForInterfaceMember(p) is { } impl3) UseMember(impl3);
                    break;
                case IFieldSymbol f:
                    UseType(f.Type);
                    break;
            }
        }
    }
}
