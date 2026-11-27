using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsToDafny.Translation;

/// <summary>A Dafny datatype assembled from a C# record, a sealed record hierarchy, or an enum.</summary>
public sealed class DatatypeInfo(INamedTypeSymbol root, string name, bool isEnum)
{
    public INamedTypeSymbol Root { get; } = root;
    public string Name { get; } = name;
    public bool IsEnum { get; } = isEnum;
    public List<CtorInfo> Ctors { get; } = [];
    public bool IsMultiCtor => Ctors.Count > 1 || Ctors.Any(c => !SymbolEqualityComparer.Default.Equals(c.Type, Root));
}

public sealed class CtorInfo(string name, INamedTypeSymbol? type, IFieldSymbol? enumMember)
{
    public string Name { get; } = name;
    /// <summary>The record type for this constructor (null for enum members).</summary>
    public INamedTypeSymbol? Type { get; } = type;
    public IFieldSymbol? EnumMember { get; } = enumMember;
    public List<DatatypeField> Fields { get; } = [];
    public DatatypeInfo Datatype { get; set; } = null!;
}

public sealed record DatatypeField(string Name, ITypeSymbol Type, IPropertySymbol? Property, IParameterSymbol? Parameter);

/// <summary>Whole-program facts the member translators need: datatypes, names, and which methods are Dafny functions.</summary>
public sealed class SymbolModel
{
    private readonly Dictionary<ISymbol, string> _names = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, DatatypeInfo> _datatypeByType = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, CtorInfo> _ctorByType = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IFieldSymbol, CtorInfo> _ctorByEnumMember = new(SymbolEqualityComparer.Default);

    public CSharpCompilation Compilation { get; }
    public List<INamedTypeSymbol> SourceTypes { get; } = [];
    public List<DatatypeInfo> Datatypes { get; } = [];
    public HashSet<IMethodSymbol> Functions { get; } = new(SymbolEqualityComparer.Default);
    public HashSet<ITypeParameterSymbol> NeedsEquality { get; } = new(SymbolEqualityComparer.Default);

    public SymbolModel(CSharpCompilation compilation)
    {
        Compilation = compilation;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                if (model.GetDeclaredSymbol(decl) is INamedTypeSymbol t && !SourceTypes.Contains(t, SymbolEqualityComparer.Default))
                    SourceTypes.Add(t);
        }
        AssignTypeNames();
        DiscoverDatatypes();
    }

    public SemanticModel ModelFor(SyntaxTree tree) => Compilation.GetSemanticModel(tree);

    public bool IsSourceType(ITypeSymbol t) => t is INamedTypeSymbol n && SourceTypes.Contains(n.OriginalDefinition, SymbolEqualityComparer.Default);

    #region Datatypes

    public DatatypeInfo? DatatypeOf(ITypeSymbol? t) =>
        t is INamedTypeSymbol n && _datatypeByType.TryGetValue(n.OriginalDefinition, out var d) ? d : null;

    /// <summary>The constructor for a record type that is one alternative of a datatype.</summary>
    public CtorInfo? CtorOf(ITypeSymbol? t) =>
        t is INamedTypeSymbol n && _ctorByType.TryGetValue(n.OriginalDefinition, out var c) ? c : null;

    public CtorInfo? CtorOfEnumMember(IFieldSymbol f) => _ctorByEnumMember.GetValueOrDefault(f.OriginalDefinition);

    private void DiscoverDatatypes()
    {
        foreach (var t in SourceTypes)
        {
            if (t.TypeKind == TypeKind.Enum)
            {
                var dt = new DatatypeInfo(t, TypeName(t), isEnum: true);
                foreach (var f in t.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                {
                    var c = new CtorInfo(Ident(f.Name), null, f) { Datatype = dt };
                    dt.Ctors.Add(c);
                    _ctorByEnumMember[f] = c;
                }
                Register(dt);
                continue;
            }

            if (!t.IsRecord) continue;
            if (IsSourceRecord(t.BaseType) && t.BaseType!.IsAbstract) continue; // a constructor of its base's datatype

            var root = new DatatypeInfo(t, TypeName(t), isEnum: false);
            if (t.IsAbstract)
            {
                foreach (var d in SourceTypes.Where(d => d.IsRecord && !d.IsAbstract &&
                                                         SymbolEqualityComparer.Default.Equals(d.BaseType?.OriginalDefinition, t)))
                    root.Ctors.Add(MakeCtor(d, root));
            }
            else
            {
                root.Ctors.Add(MakeCtor(t, root));
            }
            Register(root);
        }
    }

    private bool IsSourceRecord(INamedTypeSymbol? t) => t is not null && t.IsRecord && IsSourceType(t);

    private CtorInfo MakeCtor(INamedTypeSymbol type, DatatypeInfo dt)
    {
        var ctor = new CtorInfo(TypeName(type), type, null) { Datatype = dt };
        _ctorByType[type] = ctor;
        var primary = PrimaryConstructor(type);
        if (primary is not null)
        {
            foreach (var p in primary.Parameters)
            {
                var prop = FindProperty(type, p.Name);
                ctor.Fields.Add(new DatatypeField(Ident(p.Name), p.Type, prop, p));
            }
        }
        // Non-positional data: auto-properties declared on the record (and inherited from its base records).
        for (var cur = type; cur is not null && IsSourceRecord(cur); cur = cur.BaseType)
        {
            foreach (var prop in cur.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsImplicitlyDeclared || !IsAutoProperty(prop)) continue;
                if (ctor.Fields.Any(f => f.Name == Ident(prop.Name))) continue;
                ctor.Fields.Add(new DatatypeField(Ident(prop.Name), prop.Type, prop, null));
            }
        }
        return ctor;
    }

    private void Register(DatatypeInfo dt)
    {
        Datatypes.Add(dt);
        _datatypeByType[dt.Root] = dt;
        foreach (var c in dt.Ctors)
            if (c.Type is not null) _datatypeByType[c.Type] = dt;
    }

    public static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
            if (syntaxRef.GetSyntax() is TypeDeclarationSyntax { ParameterList: not null })
                return type.InstanceConstructors.FirstOrDefault(c =>
                    c.Parameters.Length == ((TypeDeclarationSyntax)syntaxRef.GetSyntax()).ParameterList!.Parameters.Count &&
                    !c.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, type)));
        return null;
    }

    private static IPropertySymbol? FindProperty(INamedTypeSymbol? type, string name)
    {
        for (var cur = type; cur is not null; cur = cur.BaseType)
            if (cur.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is { } p) return p;
        return null;
    }

    /// <summary>A property with compiler-generated storage: { get; } / { get; init; } / { get; set; } with no bodies.</summary>
    public static bool IsAutoProperty(IPropertySymbol prop)
    {
        foreach (var r in prop.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is not PropertyDeclarationSyntax pd) return false;
            if (pd.ExpressionBody is not null || pd.AccessorList is null) return false;
            if (pd.AccessorList.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null)) return false;
            return true;
        }
        // Positional record parameters produce properties without a PropertyDeclarationSyntax.
        return prop.DeclaringSyntaxReferences.Length > 0;
    }

    /// <summary>A property computed from other state: expression-bodied or with a getter body.</summary>
    public static bool IsComputedProperty(IPropertySymbol prop) =>
        prop.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is PropertyDeclarationSyntax) && !IsAutoProperty(prop);

    #endregion

    #region Names

    private void AssignTypeNames()
    {
        var bySimple = SourceTypes.GroupBy(t => SimpleTypeName(t));
        foreach (var g in bySimple)
        {
            var list = g.ToList();
            foreach (var t in list)
            {
                // Disambiguate same-named types from different namespaces with a namespace prefix.
                var name = list.Count == 1 ? g.Key : $"{t.ContainingNamespace.ToDisplayString().Replace('.', '_')}_{g.Key}";
                name = Ident(name);
                _names[t] = PreludeNames.Contains(name) ? name + "'" : name;
            }
        }
    }

    private static string SimpleTypeName(INamedTypeSymbol t)
    {
        var name = t.Name;
        for (var outer = t.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.Name + "_" + name;
        return name;
    }

    public string TypeName(INamedTypeSymbol t) =>
        _names.TryGetValue(t.OriginalDefinition, out var n) ? n : Ident(t.Name);

    /// <summary>The Dafny name for a method or property, renaming overloads (Dafny has no overloading).</summary>
    public string MemberName(ISymbol member)
    {
        member = member.OriginalDefinition;
        if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet } getter) member = getter.AssociatedSymbol!;
        if (_names.TryGetValue(member, out var n)) return n;
        if (member is IMethodSymbol m && m.ContainingType is { } ct)
        {
            // Overrides and interface implementations keep the name of what they implement.
            if (m.OverriddenMethod is { } overridden) return _names[member] = MemberName(overridden);
            var overloads = ct.GetMembers(m.Name).OfType<IMethodSymbol>().Where(x => x.MethodKind == MethodKind.Ordinary).ToList();
            var idx = overloads.FindIndex(x => SymbolEqualityComparer.Default.Equals(x, m));
            var baseName = Ident(m.Name);
            return _names[member] = idx <= 0 ? baseName : $"{baseName}_{idx + 1}";
        }
        return _names[member] = Ident(member.Name);
    }

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "allocated", "as", "assert", "assume", "bool", "break", "by", "calc", "case", "char", "class",
        "codatatype", "const", "constructor", "continue", "datatype", "decreases", "downto", "else", "ensures",
        "exists", "expect", "export", "extends", "false", "for", "forall", "fresh", "function", "ghost", "if", "imap",
        "import", "in", "include", "int", "invariant", "is", "iset", "iterator", "label", "lemma", "map", "match",
        "method", "modifies", "modify", "module", "multiset", "nat", "new", "newtype", "null", "object", "old",
        "older", "opened", "ORDINAL", "predicate", "print", "provides", "reads", "real", "refines", "requires",
        "return", "returns", "reveal", "reveals", "seq", "set", "static", "string", "then", "this", "to", "trait",
        "true", "twostate", "type", "unchanged", "var", "while", "witness", "yield", "yields", "array", "result"
    ];

    /// <summary>Names the prelude declares at module level; source types may not reuse them.</summary>
    private static readonly HashSet<string> PreludeNames =
        ["int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "Option", "Some", "None", "CSharp",
         "Div", "Rem", "SeqCount", "SetCount", "MapCount", "Select", "Where", "Aggregate", "Sum"];

    /// <summary>Turn a C# identifier into a legal Dafny identifier that cannot clash with keywords or the prelude.</summary>
    public static string Ident(string csName)
    {
        var name = csName.TrimStart('@');
        // Dafny identifiers may not start with an underscore.
        var stripped = name.TrimStart('_');
        if (stripped.Length != name.Length) name = stripped.Length == 0 ? "u" : stripped + "'";
        if (Keywords.Contains(name) || name.StartsWith("bv") && name.Length > 2 && name[2..].All(char.IsDigit))
            name += "'";
        return name;
    }

    #endregion
}
