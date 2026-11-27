using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;

namespace CsToDafny.Translation;

/// <summary>
/// Maps C# types to Dafny types. The mapping is the inverse of the Dafny C# compiler's:
/// BigInteger &lt;- int, Dafny.ISequence &lt;- seq, native-int newtypes &lt;- int/long/..., etc.,
/// extended to the idiomatic .NET collection types.
/// </summary>
public sealed class TypeTranslator(SymbolModel symbols, ConverterOptions options, Diagnostics diags)
{
    public static readonly NamedType IntType = Types.Int;

    /// <summary>Types referenced but not declared in the source; emitted as abstract "type X" declarations.</summary>
    public SortedDictionary<string, int> OpaqueTypes { get; } = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> SeqTypes = new()
    {
        ["System.Collections.Generic.IEnumerable<T>"] = "seq",
        ["System.Collections.Generic.IReadOnlyList<T>"] = "seq",
        ["System.Collections.Generic.IReadOnlyCollection<T>"] = "seq",
        ["System.Collections.Generic.IList<T>"] = "seq",
        ["System.Collections.Generic.ICollection<T>"] = "seq",
        ["System.Collections.Generic.List<T>"] = "seq",
        ["System.Collections.Immutable.ImmutableList<T>"] = "seq",
        ["System.Collections.Immutable.IImmutableList<T>"] = "seq",
        ["System.Collections.Immutable.ImmutableArray<T>"] = "seq",
        ["System.Collections.Immutable.ImmutableQueue<T>"] = "seq",
        ["System.Collections.Immutable.ImmutableStack<T>"] = "seq",
        ["System.ReadOnlySpan<T>"] = "seq",
        ["System.ReadOnlyMemory<T>"] = "seq",
        ["System.Collections.Generic.ISet<T>"] = "set",
        ["System.Collections.Generic.IReadOnlySet<T>"] = "set",
        ["System.Collections.Generic.HashSet<T>"] = "set",
        ["System.Collections.Generic.SortedSet<T>"] = "set",
        ["System.Collections.Immutable.ImmutableHashSet<T>"] = "set",
        ["System.Collections.Immutable.ImmutableSortedSet<T>"] = "set",
        ["System.Collections.Immutable.IImmutableSet<T>"] = "set",
        ["System.Collections.Generic.IDictionary<TKey, TValue>"] = "map",
        ["System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"] = "map",
        ["System.Collections.Generic.Dictionary<TKey, TValue>"] = "map",
        ["System.Collections.Generic.SortedDictionary<TKey, TValue>"] = "map",
        ["System.Collections.Immutable.ImmutableDictionary<TKey, TValue>"] = "map",
        ["System.Collections.Immutable.ImmutableSortedDictionary<TKey, TValue>"] = "map",
        ["System.Collections.Immutable.IImmutableDictionary<TKey, TValue>"] = "map",
    };

    private static readonly HashSet<string> MutableCollections =
    [
        "System.Collections.Generic.List<T>", "System.Collections.Generic.IList<T>", "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.HashSet<T>", "System.Collections.Generic.ISet<T>", "System.Collections.Generic.SortedSet<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.SortedDictionary<TKey, TValue>",
    ];

    /// <summary>List, HashSet, Dictionary...: collections a readonly field can still be mutated through.</summary>
    public static bool IsMutableCollection(ITypeSymbol? t) =>
        t is IArrayTypeSymbol || t is INamedTypeSymbol { IsGenericType: true } n && MutableCollections.Contains(n.OriginalDefinition.ToDisplayString());

    /// <summary>"seq", "set", "map" or null for a C# collection type (arrays and strings excluded).</summary>
    public static string? CollectionKind(ITypeSymbol? t)
    {
        if (t is IArrayTypeSymbol { Rank: 1 }) return "seq";
        if (t is not INamedTypeSymbol { IsGenericType: true } n) return null;
        return SeqTypes.GetValueOrDefault(n.OriginalDefinition.ToDisplayString());
    }

    public string IntName(SpecialType st)
    {
        if (options.Integers == IntegerMode.Mathematical) return "int";
        return st switch
        {
            SpecialType.System_SByte => "int8",
            SpecialType.System_Byte => "uint8",
            SpecialType.System_Int16 => "int16",
            SpecialType.System_UInt16 => "uint16",
            SpecialType.System_Int32 => "int32",
            SpecialType.System_UInt32 => "uint32",
            SpecialType.System_Int64 => "int64",
            SpecialType.System_UInt64 => "uint64",
            _ => throw new ArgumentOutOfRangeException(nameof(st)),
        };
    }

    public static bool IsIntegral(ITypeSymbol? t) => t?.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    public static bool IsSignedIntegral(ITypeSymbol? t) => t?.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64;

    public static bool IsBigInteger(ITypeSymbol? t) => t?.ToDisplayString() == "System.Numerics.BigInteger";

    public static bool IsFloating(ITypeSymbol? t) => t?.SpecialType is
        SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    public static bool IsNullableValueType(ITypeSymbol? t, out ITypeSymbol underlying)
    {
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            underlying = n.TypeArguments[0];
            return true;
        }
        underlying = t!;
        return false;
    }

    /// <summary>Dafny reference types are classes and traits; datatypes, strings and collections are values.</summary>
    public bool IsDafnyReferenceType(ITypeSymbol t) =>
        t.TypeKind is TypeKind.Interface or TypeKind.Class && symbols.IsSourceType(t) && symbols.DatatypeOf(t) is null;

    /// <summary>Map a type, honouring its nullable annotation.</summary>
    public DType Map(ITypeSymbol t, SyntaxNode? site = null) =>
        Map(t, t.NullableAnnotation == NullableAnnotation.Annotated, site);

    public DType Map(ITypeSymbol t, bool nullable, SyntaxNode? site)
    {
        if (IsNullableValueType(t, out var underlying))
            return Option(Map(underlying, false, site));
        if (nullable && t.IsReferenceType && t.TypeKind != TypeKind.TypeParameter)
        {
            var inner = Map(t, false, site);
            // Class and trait references use Dafny's own nullable reference types; values get Option.
            return IsDafnyReferenceType(t) && inner is NamedType nt ? nt with { Name = nt.Name + "?" } : Option(inner);
        }
        return MapNonNull(t, site);
    }

    public static NamedType Option(DType inner) => new("Option", [inner]);

    public static bool IsOption(DType t, out DType inner)
    {
        if (t is NamedType { Name: "Option", Args.Count: 1 } n)
        {
            inner = n.Args[0];
            return true;
        }
        inner = t;
        return false;
    }

    private DType MapNonNull(ITypeSymbol t, SyntaxNode? site)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean: return Types.Bool;
            case SpecialType.System_Char: return Types.Char;
            case SpecialType.System_String: return Types.String;
            case SpecialType.System_Object:
                diags.WarnOnce("object", "System.Object mapped to Dafny 'object' (reference types only)", site);
                return Types.Object;
            case SpecialType.System_Single or SpecialType.System_Double:
                diags.WarnOnce("float", "float/double mapped to Dafny 'real': rounding, NaN and infinities are not modelled", site);
                return Types.Real;
            case SpecialType.System_Decimal:
                diags.WarnOnce("decimal", "decimal mapped to Dafny 'real': precision limits and overflow are not modelled", site);
                return Types.Real;
        }
        if (IsIntegral(t)) return new NamedType(IntName(t.SpecialType));
        if (IsBigInteger(t)) return Types.Int;

        switch (t)
        {
            case ITypeParameterSymbol tp:
                return new NamedType(SymbolModel.Ident(tp.Name));
            case IArrayTypeSymbol { Rank: 1 } arr:
                return new NamedType("seq", [Map(arr.ElementType, site)]);
            case IArrayTypeSymbol arr:
                diags.WarnOnce("array" + arr.Rank, $"multi-dimensional arrays are mapped to array{arr.Rank}", site);
                return new NamedType($"array{arr.Rank}", [Map(arr.ElementType, site)]);
            case INamedTypeSymbol { IsTupleType: true } tuple:
                return new TupleType(tuple.TupleElements.Select(e => Map(e.Type, site)).ToList());
            case INamedTypeSymbol { TypeKind: TypeKind.Delegate } del:
                return MapDelegate(del, site);
            case INamedTypeSymbol n:
                return MapNamed(n, site);
        }
        return Opaque(t.ToDisplayString(), site);
    }

    private DType MapNamed(INamedTypeSymbol n, SyntaxNode? site)
    {
        var def = n.OriginalDefinition.ToDisplayString();
        if (SeqTypes.TryGetValue(def, out var kind))
        {
            return new NamedType(kind, n.TypeArguments.Select(a => Map(a, site)).ToList());
        }
        if (def == "System.Collections.Generic.KeyValuePair<TKey, TValue>")
            return new TupleType(n.TypeArguments.Select(a => Map(a, site)).ToList());

        if (symbols.IsSourceType(n))
        {
            var dt = symbols.DatatypeOf(n);
            var name = dt?.Name ?? symbols.TypeName(n);
            var args = (dt is not null ? dt.Root.TypeParameters.Length == 0 ? [] : TypeArgsForRoot(n, dt) : n.TypeArguments)
                .Select(a => Map(a, site)).ToList();
            return new NamedType(name, args);
        }
        return Opaque(n.ToDisplayString(), site);
    }

    private static IEnumerable<ITypeSymbol> TypeArgsForRoot(INamedTypeSymbol n, DatatypeInfo dt)
    {
        // A constructor record of a generic hierarchy is mapped to the root type with the base's type arguments.
        for (var cur = n; cur is not null; cur = cur.BaseType)
            if (SymbolEqualityComparer.Default.Equals(cur.OriginalDefinition, dt.Root)) return cur.TypeArguments;
        return n.TypeArguments;
    }

    private DType MapDelegate(INamedTypeSymbol del, SyntaxNode? site)
    {
        var invoke = del.DelegateInvokeMethod!;
        if (invoke.ReturnsVoid)
        {
            diags.WarnOnce("action", "delegates returning void (Action) have no Dafny function-type equivalent", site);
            return Opaque(del.ToDisplayString(), site);
        }
        return new ArrowType(invoke.Parameters.Select(p => Map(p.Type, site)).ToList(), Map(invoke.ReturnType, site));
    }

    private DType Opaque(string csName, SyntaxNode? site)
    {
        var name = SymbolModel.Ident(new string(csName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_'));
        if (!OpaqueTypes.ContainsKey(name))
        {
            OpaqueTypes[name] = 0;
            diags.Warn($"type '{csName}' has no source or known mapping; declared as an abstract Dafny type '{name}'", site);
        }
        return new NamedType(name);
    }

    public static bool TypeEquals(DType a, DType b) => (a, b) switch
    {
        (NamedType x, NamedType y) => x.Name.TrimEnd('?') == y.Name.TrimEnd('?') && x.Args.Count == y.Args.Count &&
                                      x.Args.Zip(y.Args).All(p => TypeEquals(p.First, p.Second)),
        (TupleType x, TupleType y) => x.Elements.Count == y.Elements.Count && x.Elements.Zip(y.Elements).All(p => TypeEquals(p.First, p.Second)),
        (ArrowType x, ArrowType y) => x.Params.Count == y.Params.Count && TypeEquals(x.Result, y.Result) &&
                                      x.Params.Zip(y.Params).All(p => TypeEquals(p.First, p.Second)),
        _ => false,
    };

    public static bool IsNumeric(DType t) => t is NamedType { Args.Count: 0 } n &&
        n.Name is "int" or "real" or "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64";
}
