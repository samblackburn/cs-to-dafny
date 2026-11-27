using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>C# patterns, "is" tests and switch expressions.</summary>
public sealed partial class MemberTranslator
{
    private static readonly Lit True = new("true");

    /// <summary>
    /// The condition under which <paramref name="p"/> matches <paramref name="subject"/>, plus the values its
    /// designations bind. The subject must be safe to duplicate.
    /// </summary>
    private (Expr Cond, List<(ISymbol, Expr)> Bindings) Pattern(IPatternOperation p, TExpr subject)
    {
        var bindings = new List<(ISymbol, Expr)>();
        switch (p)
        {
            case IDiscardPatternOperation:
                return (True, bindings);

            case IDeclarationPatternOperation d:
            {
                var (cond, value) = d.MatchesNull ? (True, subject) : Narrow(subject, d.NarrowedType, p);
                if (d.DeclaredSymbol is { } sym) bindings.Add((sym, value.E));
                return (cond, bindings);
            }

            case ITypePatternOperation t:
                return (Narrow(subject, t.MatchedType, p).Cond, bindings);

            case IConstantPatternOperation c:
            {
                if (IsNullConstant(c.Value))
                    return (TypeTranslator.IsOption(subject.T, out _) ? new MemberSel(subject.E, "None?")
                        : new BinaryExpr("==", subject.E, new Lit("null")), bindings);
                var (notNull, value) = Unwrap(subject);
                var inner = StripImplicitConversion(c.Value);
                if (inner is IFieldReferenceOperation { Field: var f } && _symbols.CtorOfEnumMember(f) is { } ctor)
                    return (And(notNull, new MemberSel(value.E, ctor.Name + "?")), bindings);
                return (And(notNull, new BinaryExpr("==", value.E, ExprAs(c.Value, value.T))), bindings);
            }

            case IRelationalPatternOperation r:
            {
                var (notNull, value) = Unwrap(subject);
                var op = r.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => "<",
                    BinaryOperatorKind.LessThanOrEqual => "<=",
                    BinaryOperatorKind.GreaterThan => ">",
                    BinaryOperatorKind.GreaterThanOrEqual => ">=",
                    BinaryOperatorKind.NotEquals => "!=",
                    _ => "==",
                };
                return (And(notNull, new BinaryExpr(op, value.E, ExprAs(r.Value, value.T))), bindings);
            }

            case IBinaryPatternOperation bp:
            {
                var (l, lb) = Pattern(bp.LeftPattern, subject);
                var (r, rb) = Pattern(bp.RightPattern, subject);
                bindings.AddRange(lb);
                bindings.AddRange(rb);
                return (bp.OperatorKind == BinaryOperatorKind.Or ? new BinaryExpr("||", l, r) : And(l, r), bindings);
            }

            case INegatedPatternOperation n:
            {
                var (c, _) = Pattern(n.Pattern, subject);
                return (Negate(c), bindings);
            }

            case IRecursivePatternOperation rp:
            {
                var (cond, value) = rp.MatchedType is { } mt && !SymbolEqualityComparer.Default.Equals(mt, rp.InputType)
                    ? Narrow(subject, mt, p)
                    : Unwrap(subject);
                var ctor = _symbols.CtorOf(rp.NarrowedType);
                for (var i = 0; i < rp.DeconstructionSubpatterns.Length; i++)
                {
                    string member;
                    DType memberType;
                    if (rp.NarrowedType.IsTupleType)
                    {
                        member = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        memberType = Map(((INamedTypeSymbol)rp.NarrowedType).TupleElements[i].Type, p.Syntax);
                    }
                    else if (ctor is not null && i < ctor.Fields.Count)
                    {
                        member = ctor.Fields[i].Name;
                        memberType = Map(ctor.Fields[i].Type, p.Syntax);
                    }
                    else
                    {
                        return (Unsupported("positional pattern on a type without a known deconstruction", p), bindings);
                    }
                    var (c, b) = Pattern(rp.DeconstructionSubpatterns[i], new TExpr(new MemberSel(value.E, member), memberType));
                    cond = And(cond, c);
                    bindings.AddRange(b);
                }
                foreach (var sp in rp.PropertySubpatterns)
                {
                    var saved = _implicitInstance;
                    _implicitInstance = value;
                    TExpr member;
                    try { member = Translate(sp.Member); }
                    finally { _implicitInstance = saved; }
                    var (c, b) = Pattern(sp.Pattern, member);
                    cond = And(cond, c);
                    bindings.AddRange(b);
                }
                if (rp.DeclaredSymbol is { } sym) bindings.Add((sym, value.E));
                return (cond, bindings);
            }

            default:
                return (Unsupported($"pattern {p.Kind}", p), bindings);
        }
    }

    /// <summary>Strip an Option: the condition that the value is present, and the present value.</summary>
    private static (Expr Cond, TExpr Value) Unwrap(TExpr subject) =>
        TypeTranslator.IsOption(subject.T, out var inner)
            ? (new MemberSel(subject.E, "Some?"), new TExpr(new MemberSel(subject.E, "value"), inner))
            : (True, subject);

    /// <summary>Test that <paramref name="subject"/> has C# type <paramref name="to"/>, giving the narrowed value.</summary>
    private (Expr Cond, TExpr Value) Narrow(TExpr subject, ITypeSymbol to, IOperation site)
    {
        var (notNull, value) = Unwrap(subject);
        if (_symbols.CtorOf(to) is { } ctor && ctor.Datatype.IsMultiCtor)
            return (And(notNull, new MemberSel(value.E, ctor.Name + "?")), value);
        var target = MapNonNull(to, site.Syntax);
        if (TypeTranslator.TypeEquals(value.T, target) || to.SpecialType == SpecialType.System_Object)
            return (notNull, value);
        if (_types.IsDafnyReferenceType(to))
            return (And(notNull, new Lit($"({Printer.PrintExpr(value.E)} is {Printer.TypeToString(target)})")),
                new TExpr(new ConversionExpr(value.E, target), target));
        return (Unsupported($"type test against {to.ToDisplayString()}", site), value);
    }

    private Expr TypeTest(TExpr subject, ITypeSymbol to, IOperation site) => Narrow(Simple(subject), to, site).Cond;

    #region Switch expressions

    private TExpr SwitchExpression(ISwitchExpressionOperation sw)
    {
        var type = Map(sw.Type!, sw.Syntax);
        var subject = Simple(Translate(sw.Value));
        var arms = sw.Arms.Select(a => new Arm(a.Pattern, a.Guard, StripImplicitConversion(a.Value) is IThrowOperation, a.Value)).ToList();

        if (TryMatchPatterns(sw.Value.Type, subject, arms) is { } patterns)
        {
            var cases = new List<MatchCaseExpr>();
            for (var i = 0; i < arms.Count; i++)
                if (!arms[i].Throws && patterns[i] != Unreachable) cases.Add(new MatchCaseExpr(patterns[i], Branch(arms[i].Body!, type)));
            return new TExpr(new MatchExpr(subject.E, cases), type);
        }

        // General case: an if-then-else chain. The last reachable arm is asserted rather than tested,
        // because C# throws when no arm matches.
        Expr? result = null;
        for (var i = arms.Count - 1; i >= 0; i--)
        {
            var arm = arms[i];
            if (arm.Throws) continue;
            var (cond, bindings) = arm.Pattern is null ? (True, []) : Pattern(arm.Pattern, subject);
            Bind(bindings);
            if (arm.Guard is not null) cond = And(cond, Expr(arm.Guard));
            var body = Branch(arm.Body!, type);
            result = result is null
                ? cond is Lit { Text: "true" } ? body : new AssertExpr(cond, body)
                : cond is Lit { Text: "true" } ? body : new IteExpr(cond, body, result);
        }
        return new TExpr(result ?? Unsupported("switch expression whose arms all throw", sw), type);
    }

    /// <summary>A switch arm; a null pattern is a "default:" label.</summary>
    private sealed record Arm(IPatternOperation? Pattern, IOperation? Guard, bool Throws, IOperation? Body);

    /// <summary>
    /// If every arm is a plain constructor pattern over a datatype (no guards, only variable or discard
    /// sub-patterns) and together they are exhaustive, the Dafny match patterns for each arm; otherwise null.
    /// </summary>
    private List<Pattern>? TryMatchPatterns(ITypeSymbol? subjectType, TExpr subject, IReadOnlyList<Arm> arms)
    {
        var dt = _symbols.DatatypeOf(subjectType);
        if (dt is null || TypeTranslator.IsOption(subject.T, out _)) return null;
        var patterns = new List<Pattern>();
        var covered = new HashSet<string>();
        var hasDefault = false;
        var aliases = new List<(ISymbol, Expr)>();
        foreach (var arm in arms)
        {
            if (arm.Guard is not null || hasDefault) return null;
            var pat = CasePattern(arm.Pattern, dt, subject, aliases);
            if (pat is null) return null;
            if (pat is WildPattern) hasDefault = true;
            else if (pat is CtorPattern cp && !covered.Add(cp.Ctor)) return null;
            patterns.Add(pat);
        }
        // Throwing arms are dropped, so the remaining arms must still cover every constructor.
        var live = arms.Select((a, i) => (a, patterns[i])).Where(x => !x.a.Throws).Select(x => x.Item2).ToList();
        var liveCovered = live.OfType<CtorPattern>().Select(c => c.Ctor).ToHashSet();
        if (!live.Any(p => p is WildPattern) && !dt.Ctors.All(c => liveCovered.Contains(c.Name))) return null;
        if (live.Count == 0) return null;
        // A trailing discard after every constructor is already covered would be redundant in Dafny.
        for (var k = 0; k < patterns.Count; k++)
            if (patterns[k] is WildPattern && dt.Ctors.All(c => patterns.Take(k).OfType<CtorPattern>().Any(p => p.Ctor == c.Name)))
                patterns[k] = Unreachable;
        Bind(aliases);
        return patterns;
    }

    /// <summary>Marks a switch arm that Dafny's match must omit because earlier arms are exhaustive.</summary>
    private static readonly Pattern Unreachable = new CtorPattern("#unreachable", []);

    private Pattern? CasePattern(IPatternOperation? p, DatatypeInfo dt, TExpr subject, List<(ISymbol, Expr)> aliases)
    {
        switch (p)
        {
            case null:
                return new WildPattern();
            case IDiscardPatternOperation:
                return new WildPattern();
            case IDeclarationPatternOperation { MatchesNull: true, DeclaredSymbol: null }:
                return new WildPattern();
            case IConstantPatternOperation c when StripImplicitConversion(c.Value) is IFieldReferenceOperation { Field: var f }
                                                  && _symbols.CtorOfEnumMember(f) is { } enumCtor && enumCtor.Datatype == dt:
                return new CtorPattern(enumCtor.Name, []);
            case IDeclarationPatternOperation d when _symbols.CtorOf(d.NarrowedType) is { } ctor && ctor.Datatype == dt:
                if (d.DeclaredSymbol is { } sym) aliases.Add((sym, subject.E));
                return new CtorPattern(ctor.Name, ctor.Fields.Select(_ => (Pattern)new WildPattern()).ToList());
            case ITypePatternOperation t when _symbols.CtorOf(t.MatchedType) is { } ctor2 && ctor2.Datatype == dt:
                return new CtorPattern(ctor2.Name, ctor2.Fields.Select(_ => (Pattern)new WildPattern()).ToList());
            case IRecursivePatternOperation rp when _symbols.CtorOf(rp.NarrowedType) is { } ctor3 && ctor3.Datatype == dt:
            {
                var args = ctor3.Fields.Select(_ => (Pattern)new WildPattern()).ToList();
                for (var i = 0; i < rp.DeconstructionSubpatterns.Length; i++)
                {
                    if (i >= args.Count || VarOrWild(rp.DeconstructionSubpatterns[i]) is not { } sub) return null;
                    args[i] = sub;
                }
                foreach (var sp in rp.PropertySubpatterns)
                {
                    if (sp.Member is not IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } pr) return null;
                    var index = ctor3.Fields.FindIndex(f => f.Name == SymbolModel.Ident(pr.Property.Name));
                    if (index < 0 || VarOrWild(sp.Pattern) is not { } sub) return null;
                    args[index] = sub;
                }
                if (rp.DeclaredSymbol is { } designation) aliases.Add((designation, subject.E));
                return new CtorPattern(ctor3.Name, args);
            }
            default:
                return null;
        }
    }

    private static Pattern? VarOrWild(IPatternOperation p) => p switch
    {
        IDiscardPatternOperation => new WildPattern(),
        IDeclarationPatternOperation { MatchesNull: true, DeclaredSymbol: null } => new WildPattern(),
        IDeclarationPatternOperation { MatchesNull: true, DeclaredSymbol: { } s } => new VarPattern(LocalName(s)),
        _ => null,
    };

    #endregion
}
