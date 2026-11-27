using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>Translations of .NET base-library members (collections, LINQ, strings, Math) into Dafny built-ins and the prelude.</summary>
public sealed partial class MemberTranslator
{
    private static string Owner(ISymbol member) => member.ContainingType?.OriginalDefinition.ToDisplayString() ?? "";

    private static bool IsCollectionOrString(ITypeSymbol? t) =>
        t?.SpecialType == SpecialType.System_String || TypeTranslator.CollectionKind(t) is not null;

    private TExpr? BclProperty(IPropertyReferenceOperation p)
    {
        var prop = p.Property;
        var owner = Owner(prop);
        var type = Map(prop.Type, p.Syntax);

        if (prop.IsIndexer && p.Instance is { } indexed && IsCollectionOrString(indexed.Type) && p.Arguments.Length == 1)
        {
            var target = ReceiverT(indexed).E;
            var index = p.Arguments[0].Value;
            if (index.Type?.Name == "Index" || index.Type?.Name == "Range")
                return new TExpr(Unsupported("index-from-end or range indexer", p), type);
            if (TypeTranslator.CollectionKind(indexed.Type) == "map")
                NoteKeyEquality(indexed.Type);
            return new TExpr(new IndexExpr(target, [Expr(index)]), type);
        }

        switch (prop.Name)
        {
            case "Length" or "Count" when p.Instance is { } inst && IsCollectionOrString(inst.Type):
                return CountOf(ReceiverT(inst));
            case "IsEmpty" when p.Instance is { } inst && IsCollectionOrString(inst.Type):
                return new TExpr(new BinaryExpr("==", new CardinalityExpr(ReceiverT(inst).E), new Lit("0")), Types.Bool);
            case "HasValue" when owner == "System.Nullable<T>":
                return new TExpr(new MemberSel(Translate(p.Instance!).E, "Some?"), Types.Bool);
            case "Value" when owner == "System.Nullable<T>":
                return new TExpr(new MemberSel(Translate(p.Instance!).E, "value"), type);
            case "Keys" when TypeTranslator.CollectionKind(p.Instance?.Type) == "map":
                return new TExpr(new MemberSel(ReceiverT(p.Instance!).E, "Keys"), new NamedType("set", [((NamedType)ReceiverT(p.Instance!).T).Args[0]]));
            case "Key" when owner == "System.Collections.Generic.KeyValuePair<TKey, TValue>":
                return new TExpr(new MemberSel(ReceiverT(p.Instance!).E, "0"), type);
            case "Value" when owner == "System.Collections.Generic.KeyValuePair<TKey, TValue>":
                return new TExpr(new MemberSel(ReceiverT(p.Instance!).E, "1"), type);
            case "Empty" when prop.IsStatic && TypeTranslator.CollectionKind(prop.Type) is { } kind:
                return new TExpr(kind == "map" ? new MapDisplayExpr([]) : new DisplayExpr(kind, []), type);
        }
        return null;
    }

    /// <summary>
    /// Count/Length of a .NET collection or string. In bounded mode this goes through the prelude's XCount
    /// functions, which encode that .NET sizes always fit in an int.
    /// </summary>
    private TExpr CountOf(TExpr collection)
    {
        if (_options.Integers == IntegerMode.Mathematical) return new TExpr(new CardinalityExpr(collection.E), Types.Int);
        var fn = collection.T is NamedType { Name: "set" } ? "SetCount" : collection.T is NamedType { Name: "map" } ? "MapCount" : "SeqCount";
        return new TExpr(new Apply(new Ident(fn), [collection.E]), new NamedType("int32"));
    }

    private void NoteKeyEquality(ITypeSymbol? mapType)
    {
        if (mapType is INamedTypeSymbol { TypeArguments.Length: > 0 } n && n.TypeArguments[0] is ITypeParameterSymbol tp)
            _symbols.NeedsEquality.Add(tp);
    }

    private TExpr? BclInvocation(IInvocationOperation inv)
    {
        var m = inv.TargetMethod;
        var owner = Owner(m);
        var args = OrderedArgs(inv).Select(a => a.Value).ToList();
        var type = m.ReturnsVoid ? NumLit : Map(m.ReturnType, inv.Syntax);

        // Extension methods (LINQ) take their receiver as the first argument; instance methods as Instance.
        IOperation? recvOp = m.IsExtensionMethod ? args.FirstOrDefault() : inv.Instance;
        var rest = m.IsExtensionMethod ? args.Skip(1).ToList() : args;

        TExpr R() => ReceiverT(recvOp!);
        Expr A(int i) => Expr(rest[i]);

        switch (owner, m.Name, rest.Count)
        {
            // object equality
            case (_, "Equals", 1) when !m.IsStatic && recvOp is not null && !_symbols.IsSourceType(m.ContainingType):
                return new TExpr(new BinaryExpr("==", R().E, Expr(rest[0])), Types.Bool);
            case ("object", "Equals", 2) or ("System.Object", "Equals", 2):
                return new TExpr(new BinaryExpr("==", Expr(args[0]), Expr(args[1])), Types.Bool);

            // Math
            case ("System.Math", "Max" or "Min", 2):
            {
                var t = Map(m.ReturnType, inv.Syntax);
                var a = Simple(new TExpr(ExprAs(args[0], t), t));
                var b = Simple(new TExpr(ExprAs(args[1], t), t));
                var op = m.Name == "Max" ? ">=" : "<=";
                return new TExpr(LetIfComplex([a.E, b.E], xs => new IteExpr(new BinaryExpr(op, xs[0], xs[1]), xs[0], xs[1])), t);
            }
            case ("System.Math", "Abs", 1):
            {
                var t = Map(m.ReturnType, inv.Syntax);
                var a = ExprAs(args[0], t);
                return new TExpr(LetIfComplex([a], xs => new IteExpr(new BinaryExpr("<", xs[0], new Lit("0")), new UnaryExpr("-", xs[0]), xs[0])), t);
            }

            // Diagnostics
            case ("System.Diagnostics.Debug", "Assert", >= 1):
                return new TExpr(new Opaque("#assert"), NumLit); // handled as a statement

            // string
            case ("string", "IsNullOrEmpty", 1):
            {
                var s = Simple(Translate(args[0]));
                if (TypeTranslator.IsOption(s.T, out _))
                    return new TExpr(new BinaryExpr("||", new MemberSel(s.E, "None?"), new BinaryExpr("==", new CardinalityExpr(new MemberSel(s.E, "value")), new Lit("0"))), Types.Bool);
                return new TExpr(new BinaryExpr("==", new CardinalityExpr(s.E), new Lit("0")), Types.Bool);
            }
            case ("string", "Substring", 1):
                return new TExpr(new SliceExpr(R().E, A(0), null), Types.String);
            case ("string", "Substring", 2):
            {
                var start = Simple(new TExpr(A(0), Types.Int));
                var end = start.E is Lit { Text: "0" } ? A(1) : new BinaryExpr("+", start.E, A(1));
                return new TExpr(new SliceExpr(R().E, start.E, end), Types.String);
            }
            case ("string", "StartsWith", 1) when rest[0].Type?.SpecialType == SpecialType.System_String:
                return new TExpr(new BinaryExpr("<=", A(0), R().E), Types.Bool);
            case ("string", "Contains", 1) when rest[0].Type?.SpecialType == SpecialType.System_Char:
                return new TExpr(new BinaryExpr("in", A(0), R().E), Types.Bool);
            case ("string", "Concat", _) when args.All(a => a.Type?.SpecialType == SpecialType.System_String):
                return new TExpr(args.Select(Expr).Aggregate((x, y) => new BinaryExpr("+", x, y)), Types.String);

            // Factory methods for immutable collections
            case ("System.Collections.Immutable.ImmutableList" or "System.Collections.Immutable.ImmutableArray" or
                  "System.Collections.Immutable.ImmutableHashSet", "Create", _):
            {
                var kind = TypeTranslator.CollectionKind(m.ReturnType)!;
                if (rest.Count == 1 && rest[0].Type is IArrayTypeSymbol && rest[0] is not IArrayCreationOperation { IsImplicit: true })
                    return new TExpr(Expr(rest[0]), type);
                var elems = rest.Count == 1 && rest[0] is IArrayCreationOperation { Initializer: { } init }
                    ? init.ElementValues.Select(Expr).ToList()
                    : rest.Select(Expr).ToList();
                return new TExpr(new DisplayExpr(kind, elems), type);
            }
            case (_, "CreateRange", 1) when owner.StartsWith("System.Collections.Immutable."):
                return new TExpr(ToKind(Translate(args[0]), TypeTranslator.CollectionKind(m.ReturnType)!, inv), type);
            case ("System.Array", "Empty", 0) or ("System.Linq.Enumerable", "Empty", 0):
                return new TExpr(new DisplayExpr("seq", []), type);
            case ("System.Tuple", "Create", _):
                return new TExpr(new TupleExpr(args.Select(Expr).ToList()), type);
        }

        if (recvOp is not null && TypeTranslator.CollectionKind(recvOp.Type) is { } recvKind)
            return CollectionMethod(inv, m, recvKind, recvOp, rest, type);
        if (owner == "System.Linq.Enumerable" && m.Name == "Range" && args.Count == 2)
        {
            var start = Simple(new TExpr(Expr(args[0]), Map(args[0].Type!, inv.Syntax)));
            var i = new Ident("i");
            return new TExpr(new SeqConstructionExpr(Expr(args[1]), new LambdaExpr([new BoundVar("i", null)],
                new BinaryExpr("+", start.E, new ConversionExpr(i, start.T)))), type);
        }
        return null;
    }

    /// <summary>Methods on sequences, sets and maps (instance methods and LINQ).</summary>
    private TExpr? CollectionMethod(IInvocationOperation inv, IMethodSymbol m, string kind, IOperation recvOp, List<IOperation> rest, DType type)
    {
        var recv = ReceiverT(recvOp);
        var r = recv.E;
        Expr A(int i) => Expr(rest[i]);
        DType Elem() => recv.T is NamedType { Args.Count: > 0 } n ? n.Args[0] : NumLit;

        switch (m.Name, rest.Count)
        {
            case ("Contains", 1):
                NoteElementEquality(recvOp.Type);
                return new TExpr(new BinaryExpr("in", ExprAs(rest[0], Elem()), r), Types.Bool);
            case ("ContainsKey", 1) when kind == "map":
                NoteKeyEquality(recvOp.Type);
                return new TExpr(new BinaryExpr("in", ExprAs(rest[0], Elem()), r), Types.Bool);
            case ("GetValueOrDefault", 2) when kind == "map":
            {
                var key = Simple(new TExpr(A(0), Elem()));
                return new TExpr(new IteExpr(new BinaryExpr("in", key.E, r), new IndexExpr(r, [key.E]), A(1)), type);
            }
            case ("Count", 0):
                return CountOf(recv);
            case ("LongCount", 0):
                return new TExpr(new CardinalityExpr(r), Types.Int);
            case ("Any", 0):
                return new TExpr(new BinaryExpr(">", new CardinalityExpr(r), new Lit("0")), Types.Bool);
            case ("Any" or "All", 1) when kind is "seq" or "set":
                return new TExpr(Quantified(m.Name == "All", r, rest[0], Elem()), Types.Bool);
            case ("Count", 1) when kind == "seq":
                return CountOf(recv with { E = new Apply(new Ident("Where"), [r, A(0)]) });
            case ("Select", 1) when kind == "seq" && IsUnaryLambdaOrFunction(rest[0]):
                return new TExpr(new Apply(new Ident("Select"), [r, A(0)]), type);
            case ("Where", 1) when kind == "seq" && IsUnaryLambdaOrFunction(rest[0]):
                return new TExpr(new Apply(new Ident("Where"), [r, A(0)]), type);
            case ("Aggregate", 2) when kind == "seq":
                return new TExpr(new Apply(new Ident("Aggregate"), [r, A(0), A(1)]), type);
            case ("Sum", 0) when kind == "seq":
                return Sum(r, Elem(), null, inv);
            case ("Sum", 1) when kind == "seq":
                return Sum(new Apply(new Ident("Select"), [r, A(0)]), Map(m.ReturnType, inv.Syntax), null, inv);
            case ("First", 0) when kind == "seq":
                return new TExpr(new IndexExpr(r, [new Lit("0")]), type);
            case ("Last", 0) when kind == "seq":
            {
                var s = Simple(recv);
                return new TExpr(new IndexExpr(s.E, [new BinaryExpr("-", new CardinalityExpr(s.E), new Lit("1"))]), type);
            }
            case ("ElementAt", 1) when kind == "seq":
                return new TExpr(new IndexExpr(r, [A(0)]), type);
            case ("Concat", 1) when kind == "seq":
            case ("AddRange", 1) when kind == "seq" && IsImmutable(recvOp.Type):
                return new TExpr(new BinaryExpr("+", r, A(0)), type);
            case ("Add", 1) when IsImmutable(recvOp.Type) && kind is "seq" or "set":
                return new TExpr(new BinaryExpr("+", r, new DisplayExpr(kind, [ExprAs(rest[0], Elem())])), type);
            case ("Remove", 1) when IsImmutable(recvOp.Type) && kind is "set" or "map":
                return new TExpr(new BinaryExpr("-", r, new DisplayExpr("set", [A(0)])), type);
            case ("SetItem" or "Add", 2) when IsImmutable(recvOp.Type) && kind == "map":
            case ("SetItem", 2) when IsImmutable(recvOp.Type) && kind == "seq":
                return new TExpr(new SeqUpdateExpr(r, A(0), A(1)), type);
            case ("Union", 1) when kind == "set":
                return new TExpr(new BinaryExpr("+", r, ToKind(Translate(rest[0]), "set", inv)), type);
            case ("Intersect", 1) when kind == "set":
                return new TExpr(new BinaryExpr("*", r, ToKind(Translate(rest[0]), "set", inv)), type);
            case ("Except", 1) when kind == "set":
                return new TExpr(new BinaryExpr("-", r, ToKind(Translate(rest[0]), "set", inv)), type);
            case ("ToList" or "ToArray" or "ToImmutableList" or "ToImmutableArray" or "AsEnumerable" or "AsReadOnly", 0):
            case ("ToHashSet" or "ToImmutableHashSet", 0):
            case ("ToDictionary" or "ToImmutableDictionary", 0) when kind == "map":
                return new TExpr(ToKind(recv, TypeTranslator.CollectionKind(m.ReturnType) ?? kind, inv), type);
            case ("Skip" or "Take", 1) when kind == "seq":
            {
                // LINQ clamps out-of-range counts; Dafny slices would demand 0 <= n <= |s|.
                var s = Simple(recv);
                var n = Simple(new TExpr(ExprAs(rest[0], Types.Int), Types.Int));
                var clamped = new IteExpr(new BinaryExpr("<", n.E, new Lit("0")), new Lit("0"),
                    new IteExpr(new BinaryExpr(">", n.E, new CardinalityExpr(s.E)), new CardinalityExpr(s.E), n.E));
                return new TExpr(m.Name == "Skip" ? new SliceExpr(s.E, clamped, null) : new SliceExpr(s.E, null, clamped), type);
            }
        }
        return null;
    }

    private static bool IsImmutable(ITypeSymbol? t) =>
        t?.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Immutable";

    private void NoteElementEquality(ITypeSymbol? t)
    {
        if (t is INamedTypeSymbol { TypeArguments.Length: > 0 } n && n.TypeArguments[0] is ITypeParameterSymbol tp)
            _symbols.NeedsEquality.Add(tp);
        if (t is IArrayTypeSymbol { ElementType: ITypeParameterSymbol atp }) _symbols.NeedsEquality.Add(atp);
    }

    private static bool IsUnaryLambdaOrFunction(IOperation op) =>
        op is IDelegateCreationOperation { Target: IAnonymousFunctionOperation { Symbol.Parameters.Length: 1 } or IMethodReferenceOperation }
        || op.Type is INamedTypeSymbol { DelegateInvokeMethod.Parameters.Length: 1 };

    /// <summary>xs.Any(p) / xs.All(p) as quantifiers over the collection's elements.</summary>
    private Expr Quantified(bool isAll, Expr collection, IOperation predicate, DType elemType)
    {
        string name;
        Expr body;
        if (predicate is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lam } && lam.Symbol.Parameters.Length == 1)
        {
            name = LocalName(lam.Symbol.Parameters[0]);
            var saved = _pending;
            _pending = null;
            try { body = BodyToExpr(lam.Body.Operations, 0, Types.Bool, lam) ?? new Lit("false"); }
            finally { _pending = saved; }
        }
        else
        {
            name = "x";
            body = new Apply(Expr(predicate), [new Ident(name)]);
        }
        var range = new BinaryExpr("in", new Ident(name), collection);
        return new QuantifierExpr(!isAll ? false : true, [new BoundVar(name, elemType)],
            isAll ? new BinaryExpr("==>", range, body) : new BinaryExpr("&&", range, body));
    }

    private TExpr Sum(Expr seq, DType elemType, object? _, IOperation site)
    {
        if (elemType is NamedType { Name: "real" })
            return new TExpr(new Apply(new Ident("Aggregate"), [seq, new Lit("0.0"),
                new LambdaExpr([new BoundVar("a", Types.Real), new BoundVar("b", Types.Real)], new BinaryExpr("+", new Ident("a"), new Ident("b")))]), Types.Real);
        // Enumerable.Sum on integers is checked: the result is converted back to the C# type, so overflow is a proof obligation.
        var asInt = TypeTranslator.TypeEquals(elemType, Types.Int)
            ? seq
            : new Apply(new Ident("Select"), [seq, new LambdaExpr([new BoundVar("x", elemType)], new ConversionExpr(new Ident("x"), Types.Int))]);
        return new TExpr(new Apply(new Ident("Sum"), [asInt]), Types.Int);
    }

    /// <summary>Convert a collection to another kind: seq to set becomes a set comprehension, etc.</summary>
    private Expr ToKind(TExpr source, string kind, IOperation site)
    {
        var srcKind = source.T is NamedType n ? n.Name : null;
        if (srcKind == kind) return source.E;
        if (kind == "set" && srcKind == "seq")
            return new ComprehensionExpr("set", [new BoundVar("x", null)], new BinaryExpr("in", new Ident("x"), source.E), null, null);
        return Unsupported($"conversion from {srcKind} to {kind}", site);
    }

    /// <summary>Bind complex operands with "var" so they are evaluated once.</summary>
    private static Expr LetIfComplex(IReadOnlyList<Expr> operands, Func<IReadOnlyList<Expr>, Expr> build)
    {
        var names = new List<string>();
        var rhss = new List<Expr>();
        var uses = new List<Expr>();
        for (var i = 0; i < operands.Count; i++)
        {
            if (IsSimple(operands[i])) uses.Add(operands[i]);
            else
            {
                var name = $"v{i}'";
                names.Add(name);
                rhss.Add(operands[i]);
                uses.Add(new Ident(name));
            }
        }
        var body = build(uses);
        return names.Count == 0 ? body : new LetExpr(names, rhss, body);
    }
}
