using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>Translates C# type declarations into Dafny datatypes, classes and traits.</summary>
public sealed class DeclTranslator(SymbolModel symbols, TypeTranslator types, Diagnostics diags, ConverterOptions options, Selection selection)
{
    public List<Decl> Translate()
    {
        var decls = new List<Decl>();
        foreach (var dt in symbols.Datatypes.Where(d => selection.IsEmitted(d.Root)))
            decls.Add(Datatype(dt, stub: !selection.IsIncluded(dt.Root)));
        foreach (var t in symbols.SourceTypes)
        {
            if (symbols.DatatypeOf(t) is not null || !selection.IsEmitted(t)) continue;
            var stub = !selection.IsIncluded(t);
            switch (t.TypeKind)
            {
                case TypeKind.Interface:
                    decls.Add(ClassLike(t, isTrait: true, stub));
                    break;
                case TypeKind.Class when t.IsAbstract && !t.IsStatic:
                    decls.Add(ClassLike(t, isTrait: true, stub));
                    break;
                case TypeKind.Class:
                    decls.Add(ClassLike(t, isTrait: false, stub));
                    break;
                case TypeKind.Struct:
                    diags.Warn($"struct '{t.Name}' translated as a class: copy semantics are not modelled (consider a record struct)", Syntax(t));
                    decls.Add(ClassLike(t, isTrait: false, stub));
                    break;
                default:
                    diags.Warn($"'{t.Name}' ({t.TypeKind}) skipped", Syntax(t));
                    break;
            }
        }
        foreach (var name in types.OpaqueTypes.Keys)
            decls.Add(new OpaqueTypeDecl(name, []) { Comment = "Declared outside the translated source." });
        return decls;
    }

    private static SyntaxNode? Syntax(ISymbol s) => s.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

    private List<string> TypeParams(IEnumerable<ITypeParameterSymbol> tps) =>
        tps.Select(tp => SymbolModel.Ident(tp.Name) + (symbols.NeedsEquality.Contains(tp) ? "(==)" : "")).ToList();

    private MemberTranslator Translator(INamedTypeSymbol owner) => new(symbols, types, diags, options, owner);

    #region Datatypes

    private DatatypeDecl Datatype(DatatypeInfo dt, bool stub)
    {
        var members = new List<Decl>();
        if (!dt.IsEnum)
        {
            foreach (var m in Classifier.Members(dt.Root))
            {
                if (IsObjectOverride(m) || stub && !selection.IsMemberEmitted(MemberSymbol(m))) continue;
                if (stub)
                {
                    members.Add(Member(m, dt.Root, stub));
                    continue;
                }
                var overriders = dt.Ctors.Select(c => (c, impl: ImplementationIn(c.Type!, m))).ToList();
                if (m.IsAbstract || m.IsVirtual && overriders.Any(o => o.impl is not null && !SymbolEqualityComparer.Default.Equals(o.impl, m)))
                    members.Add(Dispatch(dt, m, overriders));
                else
                    members.Add(Member(m, dt.Root));
            }
            foreach (var c in dt.Ctors.Where(c => !SymbolEqualityComparer.Default.Equals(c.Type, dt.Root)))
            foreach (var m in Classifier.Members(c.Type!))
            {
                if (IsObjectOverride(m) || m.OverriddenMethod is not null ||
                    m.AssociatedSymbol is IPropertySymbol { OverriddenProperty: not null }) continue;
                if (stub && !selection.IsMemberEmitted(MemberSymbol(m))) continue;
                var member = Member(m, c.Type!, stub);
                members.Add(m.IsStatic ? member : AddRequires(member, new MemberSel(new Lit("this"), c.Name + "?")));
            }
            foreach (var t in dt.Ctors.Select(c => c.Type!).Prepend(dt.Root).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>())
                members.AddRange(StaticFields(t).Where(f => !stub || selection.IsMemberEmitted(f.Symbol)).Select(f => f.Decl));
            foreach (var t in stub ? [] : dt.Ctors.Select(c => c.Type!).Prepend(dt.Root).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>())
            {
                if (t.AllInterfaces.Any(i => symbols.IsSourceType(i)))
                    diags.Warn($"record '{t.Name}' implements an interface; Dafny datatypes cannot extend traits by default, so the interface is dropped", Syntax(t));
                foreach (var ctor in t.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && Syntax(c) is ConstructorDeclarationSyntax))
                    diags.Warn($"explicit constructor of record '{t.Name}' ignored: validation logic is not translated", Syntax(ctor));
                if (t.GetMembers().OfType<IPropertySymbol>().Any(p => p.SetMethod is { IsInitOnly: false } && !p.IsImplicitlyDeclared))
                    diags.Warn($"record '{t.Name}' has a settable property; it is translated as an immutable datatype field", Syntax(t));
            }
        }

        var ctors = dt.Ctors.Select(c => new DatatypeCtor(c.Name, c.Fields.Select(f => new Formal(f.Name, types.Map(f.Type, Syntax(dt.Root)))).ToList())).ToList();
        var comment = (dt.IsEnum ? $"C# enum {dt.Root.ToDisplayString()}" : $"C# record {dt.Root.ToDisplayString()}") +
                      (stub ? StubNote : "");
        return new DatatypeDecl(dt.Name, TypeParams(dt.Root.TypeParameters), ctors, members) { Comment = comment };
    }

    private const string StubNote = "\nOutside the --include selection: only the members the selection uses, without bodies.";

    private static ISymbol MemberSymbol(IMethodSymbol m) => m.AssociatedSymbol ?? m;

    private static bool IsObjectOverride(IMethodSymbol m)
    {
        for (var o = m.OverriddenMethod; o is not null; o = o.OverriddenMethod)
            if (o.ContainingType.SpecialType == SpecialType.System_Object) return true;
        return m.Name is "ToString" or "GetHashCode" or "Equals" or "PrintMembers" or "Deconstruct" && m.OverriddenMethod is null && m.ContainingType.IsRecord;
    }

    /// <summary>The member of <paramref name="t"/> that a call to <paramref name="m"/> dispatches to.</summary>
    private static IMethodSymbol? ImplementationIn(INamedTypeSymbol t, IMethodSymbol m)
    {
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            foreach (var cand in Classifier.Members(cur))
            {
                for (var o = cand; o is not null; o = o.OverriddenMethod ?? (o.AssociatedSymbol as IPropertySymbol)?.OverriddenProperty?.GetMethod)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, m.OriginalDefinition))
                        return cand.IsAbstract ? null : cand;
            }
        }
        return null;
    }

    /// <summary>An abstract/virtual member of a record hierarchy: one Dafny member that matches on the constructor.</summary>
    private Decl Dispatch(DatatypeInfo dt, IMethodSymbol m, List<(CtorInfo Ctor, IMethodSymbol? Impl)> overriders)
    {
        var isFunction = symbols.Functions.Contains(m.OriginalDefinition);
        var formals = Formals(m);
        var resultType = m.ReturnsVoid ? null : types.Map(m.ReturnType, Syntax(m));
        var exprCases = new List<MatchCaseExpr>();
        var stmtCases = new List<MatchCaseStmt>();
        var modifies = false;
        foreach (var (ctor, impl) in overriders)
        {
            if (impl is null || Classifier.BodyOf(impl, symbols) is not { } body)
            {
                diags.Unsupported($"no implementation of {m.Name} for {ctor.Name}", Syntax(m) ?? Syntax(dt.Root)!);
                continue;
            }
            if (impl.Parameters.Select(p => p.Name).SequenceEqual(m.Parameters.Select(p => p.Name)) is false)
                diags.Warn($"{ctor.Name}.{m.Name} renames parameters of the member it overrides; the base names are used", Syntax(impl));
            var mt = Translator(ctor.Type!);
            if (isFunction)
            {
                var e = mt.FunctionBody(body.Operations, 0, resultType!, body)
                        ?? new AssertExpr(new Lit("false"), new Opaque("throw"));
                exprCases.Add(new MatchCaseExpr(BindUsedFields(ctor, Printer.PrintExpr(e)), e));
            }
            else
            {
                var stmts = mt.MethodBody(body.Operations, 0, resultType, impl);
                var text = Printer.Print([new MethodDecl("m", false, false, [], [], [], stmts)]);
                stmtCases.Add(new MatchCaseStmt(BindUsedFields(ctor, text), stmts));
                modifies |= mt.ModifiesThis;
            }
        }
        var name = symbols.MemberName(m);
        var tps = TypeParams(m.TypeParameters);
        if (isFunction)
            return new FunctionDecl(name, false, false, tps, formals, resultType!, new MatchExpr(new Lit("this"), exprCases))
                { Requires = ParamRequires(m) };
        return new MethodDecl(name, false, false, tps, formals, resultType is null ? [] : [new Formal("result", resultType)],
            [new MatchStmt(new Lit("this"), stmtCases)]) { Requires = ParamRequires(m) };
    }

    /// <summary>A constructor pattern binding (by its own name) each field the case body mentions.</summary>
    private static CtorPattern BindUsedFields(CtorInfo ctor, string bodyText) =>
        new(ctor.Name, ctor.Fields.Select(f =>
            System.Text.RegularExpressions.Regex.IsMatch(bodyText, $@"(?<![\w'.?]){System.Text.RegularExpressions.Regex.Escape(f.Name)}(?![\w'])")
                ? (Pattern)new VarPattern(f.Name)
                : new WildPattern()).ToList());

    #endregion

    #region Classes and traits

    private ClassDecl ClassLike(INamedTypeSymbol t, bool isTrait, bool stub)
    {
        var members = new List<Decl>();
        var fieldInits = new List<(string Name, IOperation Value, DType Type)>();

        foreach (var member in t.GetMembers())
        {
            if (member.IsImplicitlyDeclared || stub && !selection.IsMemberEmitted(member)) continue;
            switch (member)
            {
                case IFieldSymbol f:
                {
                    var type = types.Map(f.Type, Syntax(f));
                    var init = stub ? null : FieldInitializer(f);
                    var isConst = (f.IsReadOnly || f.IsConst) && !TypeTranslator.IsMutableCollection(f.Type);
                    if (f.IsStatic || f.IsConst)
                    {
                        members.Add(StaticField(f, type, init));
                        break;
                    }
                    members.Add(new FieldDecl(symbols.MemberName(f), type, isConst, false, null));
                    if (init is not null) fieldInits.Add((symbols.MemberName(f), init, type));
                    break;
                }
                case IPropertySymbol p when !p.IsIndexer && SymbolModel.IsAutoProperty(p) && !p.IsAbstract && t.TypeKind != TypeKind.Interface:
                {
                    var type = types.Map(p.Type, Syntax(p));
                    var init = stub ? null : FieldInitializer(p);
                    var isConst = p.SetMethod is null || p.SetMethod.IsInitOnly;
                    if (p.IsStatic)
                    {
                        members.Add(new FieldDecl(symbols.MemberName(p), type, true, true, init is null ? null : Translator(t).PureExpr(init, type)));
                        break;
                    }
                    members.Add(new FieldDecl(symbols.MemberName(p), type, isConst, false, null));
                    if (init is not null) fieldInits.Add((symbols.MemberName(p), init, type));
                    break;
                }
            }
        }

        var ctors = t.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && (!stub || selection.IsMemberEmitted(c))).ToList();
        if (stub)
        {
            foreach (var c in ctors)
                members.Add(new MethodDecl(MemberTranslator.ConstructorName(c) ?? "", false, true, [], Formals(c), [], null));
        }
        else if (!isTrait && !t.IsStatic)
        {
            if (ctors.Count == 0 && (fieldInits.Count > 0 || members.OfType<FieldDecl>().Any(f => !f.IsStatic)))
                members.Add(new MethodDecl("", false, true, [], [], [], FieldInitStatements(t, fieldInits)));
            foreach (var c in ctors) members.Add(Constructor(t, c, fieldInits));
        }

        foreach (var m in Classifier.Members(t))
        {
            if (stub && !selection.IsMemberEmitted(MemberSymbol(m))) continue;
            if (IsObjectOverride(m))
            {
                diags.Warn($"override of object.{m.Name} skipped: Dafny classes use reference equality", Syntax(m));
                continue;
            }
            members.Add(Member(m, t, stub));
        }

        var extends = new List<DType>();
        foreach (var i in t.Interfaces.Where(symbols.IsSourceType)) extends.Add(types.Map(i, nullable: false, Syntax(t)));
        if (t.BaseType is { } bt && symbols.IsSourceType(bt))
        {
            if (bt.IsAbstract) extends.Add(types.Map(bt, nullable: false, Syntax(t)));
            else diags.Unsupported($"inheritance from non-abstract class {bt.Name} (Dafny classes cannot be extended)", Syntax(t)!);
        }
        if (isTrait && extends.Count == 0) extends.Add(new NamedType("object"));
        var kind = t.TypeKind == TypeKind.Interface ? "interface" : t.IsStatic ? "static class" : t.IsAbstract ? "abstract class" : "class";
        return new ClassDecl(symbols.TypeName(t), TypeParams(t.TypeParameters), extends, members, isTrait)
            { Comment = $"C# {kind} {t.ToDisplayString()}" + (stub ? StubNote : "") };
    }

    private IOperation? FieldInitializer(ISymbol member)
    {
        var syntax = Syntax(member);
        EqualsValueClauseSyntax? init = syntax switch
        {
            VariableDeclaratorSyntax v => v.Initializer,
            PropertyDeclarationSyntax p => p.Initializer,
            _ => null,
        };
        if (init is null) return null;
        var op = symbols.ModelFor(init.SyntaxTree).GetOperation(init);
        return op switch
        {
            IFieldInitializerOperation fi => fi.Value,
            IPropertyInitializerOperation pi => pi.Value,
            _ => null,
        };
    }

    private IEnumerable<(IFieldSymbol Symbol, Decl Decl)> StaticFields(INamedTypeSymbol t)
    {
        foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
            if (!f.IsImplicitlyDeclared && (f.IsStatic || f.IsConst))
                yield return (f, StaticField(f, types.Map(f.Type, Syntax(f)), FieldInitializer(f)));
    }

    private Decl StaticField(IFieldSymbol f, DType type, IOperation? init)
    {
        if (!f.IsReadOnly && !f.IsConst)
            diags.Warn($"mutable static field '{f.Name}' translated as a constant", Syntax(f));
        var value = init is null ? null : Translator(f.ContainingType).PureExpr(init, type);
        return new FieldDecl(symbols.MemberName(f), type, true, true, value);
    }

    private List<Stmt> FieldInitStatements(INamedTypeSymbol t, List<(string Name, IOperation Value, DType Type)> inits)
    {
        var mt = Translator(t);
        return inits.Select(i => (Stmt)new AssignStmt([new Ident(i.Name)], [new ExprRhs(mt.PureExpr(i.Value, i.Type))])).ToList();
    }

    private MethodDecl Constructor(INamedTypeSymbol t, IMethodSymbol c, List<(string Name, IOperation Value, DType Type)> fieldInits)
    {
        var mt = Translator(t);
        var body = Classifier.BodyOf(c, symbols);
        if (Syntax(c) is ConstructorDeclarationSyntax { Initializer: { } ci })
            diags.Unsupported($"constructor initializer '{ci}'", ci);
        var ops = body?.Operations.ToList() ?? [];
        var (requires, consumed) = options.GuardsAsPreconditions ? mt.LeadingGuards(ops) : ([], 0);
        var stmts = FieldInitStatements(t, fieldInits);
        stmts.AddRange(mt.MethodBody(ops, consumed, null, c));
        stmts = WithNewDivider(stmts);
        return new MethodDecl(MemberTranslator.ConstructorName(c) ?? "", false, true, [], Formals(c), [], stmts)
            { Requires = requires.Concat(ParamRequires(c)).ToList() };
    }

    /// <summary>
    /// Dafny constructors run in two phases: fields are assigned first, and only after "new;" may the body call
    /// methods on "this" or use it otherwise. Insert "new;" before the first statement that needs it.
    /// </summary>
    private static List<Stmt> WithNewDivider(List<Stmt> stmts)
    {
        var bareCall = new System.Text.RegularExpressions.Regex(@"(?<![\w.'])[A-Za-z]\w*'?\(|\bthis\b(?!\.\w+ :=)");
        bool FirstPhase(Stmt s) => s switch
        {
            CommentStmt => true,
            AssignStmt or VarDeclStmt => !bareCall.IsMatch(Printer.Print([new MethodDecl("m", false, false, [], [], [], [s])]).Split('{', 2)[1]),
            _ => false,
        };
        var k = stmts.FindIndex(s => !FirstPhase(s));
        if (k < 0) return stmts;
        return [.. stmts.Take(k), new NewDividerStmt(), .. stmts.Skip(k)];
    }

    #endregion

    #region Members

    private List<Formal> Formals(IMethodSymbol m) =>
        m.Parameters.Select(p => new Formal(SymbolModel.Ident(p.Name), types.Map(p.Type, Syntax(p)))).ToList();

    /// <summary>A parameter typed as one alternative of a record hierarchy must hold that alternative.</summary>
    private List<Expr> ParamRequires(IMethodSymbol m) =>
        m.Parameters.Where(p => symbols.CtorOf(p.Type) is { Datatype.IsMultiCtor: true } && p.NullableAnnotation != NullableAnnotation.Annotated)
            .Select(p => (Expr)new MemberSel(new Ident(SymbolModel.Ident(p.Name)), symbols.CtorOf(p.Type)!.Name + "?"))
            .ToList();

    private static Decl AddRequires(Decl d, Expr r) => d switch
    {
        FunctionDecl f => f with { Requires = [r, .. f.Requires] },
        MethodDecl m => m with { Requires = [r, .. m.Requires] },
        _ => d,
    };

    private Decl Member(IMethodSymbol m, INamedTypeSymbol owner, bool stub = false)
    {
        var name = symbols.MemberName(m);
        var formals = Formals(m);
        var body = stub ? null : Classifier.BodyOf(m, symbols);
        if (body is not null && m.Parameters.FirstOrDefault(p => p.RefKind != RefKind.None) is { } byRef)
        {
            // Dropping ref/out would silently change behaviour: callers would not see the assignment.
            diags.Unsupported($"{byRef.RefKind.ToString().ToLowerInvariant()} parameter '{byRef.Name}'", Syntax(byRef)!);
            return new MethodDecl(name, m.IsStatic, false, TypeParams(m.TypeParameters), formals,
                m.ReturnsVoid ? [] : [new Formal("result", types.Map(m.ReturnType, Syntax(m)))],
                [new OpaqueStmt(Syntax(m)!.ToString())]);
        }
        var mt = Translator(owner);
        var ops = body?.Operations.ToList() ?? [];
        var (guards, consumed) = body is not null && options.GuardsAsPreconditions ? mt.LeadingGuards(ops) : ([], 0);
        var requires = guards.Concat(ParamRequires(m)).ToList();
        var resultType = m.ReturnsVoid ? null : types.Map(m.ReturnType, Syntax(m));
        var comment = m.AssociatedSymbol is IPropertySymbol ? $"C# property {m.AssociatedSymbol.Name}" : null;

        if (symbols.Functions.Contains(m.OriginalDefinition) || body is null && !m.ReturnsVoid && IsFunctionTraitMember(m))
        {
            Expr? expr = null;
            if (body is not null)
                expr = mt.FunctionBody(ops, consumed, resultType!, body) ?? new AssertExpr(new Lit("false"), new Opaque("throw"));
            var reads = !m.IsStatic && owner.TypeKind != TypeKind.Interface && symbols.DatatypeOf(owner) is null && HasMutableState(owner)
                ? new List<string> { "this" } : [];
            return new FunctionDecl(name, m.IsStatic, false, TypeParams(m.TypeParameters), formals, resultType!, expr)
                { Requires = requires, Reads = reads, Comment = comment };
        }

        List<Stmt>? stmts = body is null ? null : mt.MethodBody(ops, consumed, resultType, m);
        var modifies = mt.ModifiesThis ? new List<string> { "this" } : [];
        return new MethodDecl(name, m.IsStatic, false, TypeParams(m.TypeParameters), formals,
            resultType is null ? [] : [new Formal("result", resultType)], stmts)
            { Requires = requires, Modifies = modifies, Comment = comment };
    }

    private bool IsFunctionTraitMember(IMethodSymbol m) => symbols.Functions.Contains(m.OriginalDefinition);

    private static bool HasMutableState(INamedTypeSymbol t) =>
        t.GetMembers().Any(m => m is IFieldSymbol { IsStatic: false, IsConst: false } f && (!f.IsReadOnly || TypeTranslator.IsMutableCollection(f.Type)) ||
                                m is IPropertySymbol { IsStatic: false, SetMethod: { IsInitOnly: false } } p && SymbolModel.IsAutoProperty(p));

    #endregion
}
