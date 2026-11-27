using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>Function bodies: a statement list that only declares, branches and returns becomes one Dafny expression.</summary>
public sealed partial class MemberTranslator
{
    /// <summary>Translate a pure member body. Null means every path throws.</summary>
    public Expr? FunctionBody(IReadOnlyList<IOperation> ops, int start, DType resultType, IOperation site)
    {
        _pending = null;
        return BodyToExpr(ops, start, resultType, site);
    }

    private Expr? BodyToExpr(IReadOnlyList<IOperation> ops, int i, DType result, IOperation site)
    {
        if (i >= ops.Count)
            return Unsupported("function body path that does not return a value", site);
        var op = ops[i];
        switch (op)
        {
            case IReturnOperation { ReturnedValue: { } value }:
                if (StripImplicitConversion(value) is IThrowOperation) return null;
                return ExprAs(value, result);
            case IThrowOperation or IExpressionStatementOperation { Operation: IThrowOperation }:
                return null;
            case IBlockOperation b:
                return BodyToExpr(b.Operations.Concat(ops.Skip(i + 1)).ToList(), 0, result, site);
            case IEmptyOperation:
                return BodyToExpr(ops, i + 1, result, site);
            case IVariableDeclarationGroupOperation g:
            {
                var names = new List<string>();
                var rhss = new List<Expr>();
                foreach (var d in g.Declarations.SelectMany(d => d.Declarators))
                {
                    var init = d.GetVariableInitializer() ?? (d.Initializer is null ? null : null);
                    if (init is null) return Unsupported("uninitialized local in a function", d);
                    var t = LocalType(d.Symbol);
                    names.Add($"{LocalName(d.Symbol)}: {Printer.TypeToString(t)}");
                    rhss.Add(ExprAs(init.Value, t));
                }
                var body = BodyToExpr(ops, i + 1, result, site);
                return body is null ? null : Lets(names, rhss, body);
            }
            case IDeconstructionAssignmentOperation da when da.Target is IDeclarationExpressionOperation { Expression: ITupleOperation tuple }:
            {
                var names = tuple.Elements.Select(e => e is ILocalReferenceOperation l ? LocalName(l.Local) : "_").ToList();
                var rhs = Expr(da.Value);
                var body = BodyToExpr(ops, i + 1, result, site);
                return body is null ? null : new LetExpr([$"({string.Join(", ", names)})"], [rhs], body);
            }
            case IExpressionStatementOperation { Operation: IDeconstructionAssignmentOperation da2 }:
                return BodyToExpr([da2, .. ops.Skip(i + 1)], 0, result, site);
            case IConditionalOperation c:
            {
                var cond = Expr(c.Condition);
                var rest = ops.Skip(i + 1).ToList();
                var thenOps = EndsInJump(c.WhenTrue) ? [c.WhenTrue] : new List<IOperation> { c.WhenTrue }.Concat(rest).ToList();
                var elseOps = c.WhenFalse is null ? rest
                    : EndsInJump(c.WhenFalse) ? [c.WhenFalse] : new List<IOperation> { c.WhenFalse }.Concat(rest).ToList();
                var thenE = BodyToExpr(thenOps, 0, result, site);
                var elseE = BodyToExpr(elseOps, 0, result, site);
                return Choose(cond, thenE, elseE);
            }
            case ISwitchOperation sw:
                return SwitchStatementToExpr(sw, ops.Skip(i + 1).ToList(), result, site);
            case IExpressionStatementOperation { Operation: IInvocationOperation inv } when IsDebugAssert(inv):
            {
                var cond = Expr(inv.Arguments[0].Value);
                var body = BodyToExpr(ops, i + 1, result, site);
                return body is null ? null : new AssertExpr(cond, body);
            }
            case IExpressionStatementOperation { Operation: IInvocationOperation inv2 } when GuardCondition(inv2) is { } guard:
            {
                var body = BodyToExpr(ops, i + 1, result, site);
                return body is null ? null : new AssertExpr(guard, body);
            }
            default:
                return Unsupported($"statement in a function body ({op.Kind})", op);
        }
    }

    private static Expr Lets(List<string> names, List<Expr> rhss, Expr body)
    {
        for (var k = names.Count - 1; k >= 0; k--) body = new LetExpr([names[k]], [rhss[k]], body);
        return body;
    }

    /// <summary>Combine branches where a null branch throws: "throw" becomes an assertion that the branch is not taken.</summary>
    private static Expr? Choose(Expr cond, Expr? thenE, Expr? elseE) => (thenE, elseE) switch
    {
        (null, null) => null,
        (null, { } e) => new AssertExpr(Negate(cond), e),
        ({ } t, null) => new AssertExpr(cond, t),
        ({ } t, { } e) => new IteExpr(cond, t, e),
    };

    private static bool EndsInJump(IOperation op) => op switch
    {
        IReturnOperation or IThrowOperation or IExpressionStatementOperation { Operation: IThrowOperation } => true,
        IBranchOperation => true,
        IBlockOperation b => b.Operations.Length > 0 && EndsInJump(b.Operations[^1]),
        IConditionalOperation { WhenFalse: not null } c => EndsInJump(c.WhenTrue) && EndsInJump(c.WhenFalse),
        _ => false,
    };

    private static bool IsDebugAssert(IInvocationOperation inv) =>
        inv.TargetMethod.Name == "Assert" && Owner(inv.TargetMethod) == "System.Diagnostics.Debug" && inv.Arguments.Length >= 1;

    private Expr? SwitchStatementToExpr(ISwitchOperation sw, List<IOperation> rest, DType result, IOperation site)
    {
        var subject = Simple(Translate(sw.Value));
        var sections = new List<(Expr Cond, IOperation? Guard, List<IOperation> Body, IPatternOperation? Pattern)>();
        foreach (var section in sw.Cases)
        {
            var body = section.Body.ToList();
            if (body.Count > 0 && body[^1] is IBranchOperation { BranchKind: BranchKind.Break }) body.RemoveAt(body.Count - 1);
            if (body.Count == 0 || !EndsInJump(body[^1])) body.AddRange(rest);
            Expr cond = new Lit("false");
            IOperation? guard = null;
            IPatternOperation? pattern = null;
            foreach (var clause in section.Clauses)
            {
                var (c, g, p) = ClauseCondition(clause, subject);
                cond = cond is Lit { Text: "false" } ? c : new BinaryExpr("||", cond, c);
                guard ??= g;
                pattern = section.Clauses.Length == 1 ? p : null;
            }
            sections.Add((cond, guard, body, pattern));
        }
        // Default sections are tried last in C# regardless of position.
        var ordered = sections.OrderBy(s => s.Cond is Lit { Text: "true" } ? 1 : 0).ToList();
        var hasDefault = ordered.Any(s => s.Cond is Lit { Text: "true" });
        Expr? acc = hasDefault ? null : BodyToExpr(rest, 0, result, site);
        var first = true;
        for (var k = ordered.Count - 1; k >= 0; k--)
        {
            var (cond, guard, body, _) = ordered[k];
            if (guard is not null) cond = And(cond, Expr(guard));
            var e = BodyToExpr(body, 0, result, site);
            if (first && hasDefault) acc = e;
            else acc = Choose(cond, e, acc);
            first = false;
        }
        return acc;
    }

    /// <summary>The condition (and bindings, registered as aliases) for one switch-statement case label.</summary>
    private (Expr Cond, IOperation? Guard, IPatternOperation? Pattern) ClauseCondition(ICaseClauseOperation clause, TExpr subject)
    {
        switch (clause)
        {
            case IDefaultCaseClauseOperation:
                return (True, null, null);
            case ISingleValueCaseClauseOperation sv:
            {
                var inner = StripImplicitConversion(sv.Value);
                if (inner is IFieldReferenceOperation { Field: var f } && _symbols.CtorOfEnumMember(f) is { } ctor)
                    return (new MemberSel(subject.E, ctor.Name + "?"), null, null);
                return (new BinaryExpr("==", subject.E, ExprAs(sv.Value, subject.T)), null, null);
            }
            case IPatternCaseClauseOperation pc:
            {
                var (cond, bindings) = Pattern(pc.Pattern, subject);
                Bind(bindings);
                return (cond, pc.Guard, pc.Pattern);
            }
            default:
                return (Unsupported("case clause", clause), null, null);
        }
    }

    #region Guards and preconditions

    private static readonly HashSet<string> GuardExceptions =
    [
        "System.ArgumentException", "System.ArgumentNullException", "System.ArgumentOutOfRangeException",
    ];

    /// <summary>
    /// Leading "if (bad) throw new ArgumentException(...)" statements and ThrowIfXxx helper calls, as the
    /// preconditions they express. Returns how many statements were consumed.
    /// </summary>
    public (List<Expr> Requires, int Consumed) LeadingGuards(IReadOnlyList<IOperation> ops)
    {
        var requires = new List<Expr>();
        var i = 0;
        var saved = _pending;
        _pending = null;
        try
        {
            for (; i < ops.Count; i++)
            {
                var op = ops[i];
                if (op is IConditionalOperation { WhenFalse: null } c && ThrowsGuardException(c.WhenTrue) && !ReferencesLocals(c.Condition))
                {
                    requires.Add(Negate(Expr(c.Condition)));
                    continue;
                }
                if (op is IExpressionStatementOperation { Operation: IInvocationOperation inv } && GuardCondition(inv) is { } g)
                {
                    requires.Add(g);
                    continue;
                }
                // A leading Debug.Assert documents what callers must ensure.
                if (op is IExpressionStatementOperation { Operation: IInvocationOperation da } && IsDebugAssert(da) && !ReferencesLocals(da.Arguments[0].Value))
                {
                    requires.Add(Expr(da.Arguments[0].Value));
                    continue;
                }
                break;
            }
        }
        finally
        {
            _pending = saved;
        }
        return (requires, i);
    }

    private static bool ThrowsGuardException(IOperation op)
    {
        var t = op switch
        {
            IBlockOperation { Operations.Length: 1 } b => b.Operations[0],
            _ => op,
        };
        if (t is IExpressionStatementOperation es) t = es.Operation;
        return t is IThrowOperation { Exception: { } ex } &&
               StripImplicitConversion(ex).Type is { } type &&
               GuardExceptions.Contains(type.ToDisplayString());
    }

    private static bool ReferencesLocals(IOperation op) =>
        op.DescendantsAndSelf().Any(d => d is ILocalReferenceOperation or IInvocationOperation { TargetMethod.IsStatic: false, Instance: not IInstanceReferenceOperation });

    /// <summary>ArgumentNullException.ThrowIfNull(x) and friends: the condition under which they do not throw.</summary>
    private Expr? GuardCondition(IInvocationOperation inv)
    {
        var m = inv.TargetMethod;
        var owner = Owner(m);
        if (owner is not ("System.ArgumentNullException" or "System.ArgumentOutOfRangeException" or "System.ArgumentException")) return null;
        if (!m.Name.StartsWith("ThrowIf", StringComparison.Ordinal)) return null;
        var args = OrderedArgs(inv);
        var x = Translate(args[0].Value is IConversionOperation { IsImplicit: true } conv ? conv.Operand : args[0].Value);
        Expr Arg(int k) => ExprAs(args[k].Value, Unwrap(x).Value.T);
        var v = Unwrap(x).Value.E;
        return m.Name switch
        {
            "ThrowIfNull" => TypeTranslator.IsOption(x.T, out _) ? new MemberSel(x.E, "Some?")
                : x.T is NamedType { Name: var n } && n.EndsWith('?') ? new BinaryExpr("!=", x.E, new Lit("null")) : True,
            "ThrowIfNullOrEmpty" => And(Unwrap(x).Cond, new BinaryExpr(">", new CardinalityExpr(v), new Lit("0"))),
            "ThrowIfNegative" => new BinaryExpr(">=", v, new Lit("0")),
            "ThrowIfZero" => new BinaryExpr("!=", v, new Lit("0")),
            "ThrowIfNegativeOrZero" => new BinaryExpr(">", v, new Lit("0")),
            "ThrowIfGreaterThan" => new BinaryExpr("<=", v, Arg(1)),
            "ThrowIfGreaterThanOrEqual" => new BinaryExpr("<", v, Arg(1)),
            "ThrowIfLessThan" => new BinaryExpr(">=", v, Arg(1)),
            "ThrowIfLessThanOrEqual" => new BinaryExpr(">", v, Arg(1)),
            "ThrowIfEqual" => new BinaryExpr("!=", v, Arg(1)),
            "ThrowIfNotEqual" => new BinaryExpr("==", v, Arg(1)),
            _ => null,
        };
    }

    #endregion
}
