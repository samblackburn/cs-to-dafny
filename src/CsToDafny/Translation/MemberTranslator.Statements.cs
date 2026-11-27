using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>Method bodies: C# statements to Dafny statements, hoisting method calls out of expressions.</summary>
public sealed partial class MemberTranslator
{
    /// <summary>Parameters assigned in the body; Dafny parameters are immutable, so they get a local copy.</summary>
    private readonly Dictionary<IParameterSymbol, string> _paramCopies = new(SymbolEqualityComparer.Default);

    private DType? _returnType;

    public List<Stmt> MethodBody(IReadOnlyList<IOperation> ops, int start, DType? returnType, IMethodSymbol method)
    {
        _returnType = returnType;
        var output = new List<Stmt>();
        foreach (var p in method.Parameters)
        {
            var assigned = ops.SelectMany(o => o.DescendantsAndSelf()).Any(o =>
                o is IAssignmentOperation { Target: IParameterReferenceOperation pr } && SymbolEqualityComparer.Default.Equals(pr.Parameter, p) ||
                o is IIncrementOrDecrementOperation { Target: IParameterReferenceOperation pr2 } && SymbolEqualityComparer.Default.Equals(pr2.Parameter, p));
            if (!assigned) continue;
            var copy = LocalName(p) + "'";
            _paramCopies[p] = copy;
            output.Add(new VarDeclStmt([new BoundVar(copy, Map(p.Type, null))], [new ExprRhs(new Ident(LocalName(p)))]));
        }
        for (var i = start; i < ops.Count; i++) Statement(ops[i], output);
        return output;
    }

    private BlockStmt Block(IOperation op)
    {
        var output = new List<Stmt>();
        Statement(op, output);
        return new BlockStmt(output);
    }

    /// <summary>Run <paramref name="translate"/> collecting hoisted statements, then emit them before its result.</summary>
    private void WithPending(List<Stmt> output, Func<Stmt?> translate)
    {
        var saved = _pending;
        _pending = [];
        try
        {
            var s = translate();
            output.AddRange(_pending);
            if (s is not null) output.Add(s);
        }
        finally
        {
            _pending = saved;
        }
    }

    /// <summary>
    /// Translate a right-hand side. A method call or allocation that would be hoisted into a temporary is instead
    /// used directly, since Dafny allows them as the whole right-hand side of an assignment.
    /// </summary>
    private Rhs RhsAs(IOperation op, DType type)
    {
        var e = ExprAs(op, type);
        if (e is Ident { Name: var name } && _pending is { Count: > 0 } && _pending[^1] is VarDeclStmt { Vars: [{ Name: var tmp }], Rhss: [var rhs] } && tmp == name)
        {
            _pending.RemoveAt(_pending.Count - 1);
            return rhs;
        }
        return new ExprRhs(e);
    }

    private void Statement(IOperation op, List<Stmt> output)
    {
        switch (op)
        {
            case IBlockOperation b:
                foreach (var s in b.Operations) Statement(s, output);
                return;
            case IEmptyOperation:
                return;
            case ILabeledOperation { Operation: { } inner }:
                Statement(inner, output);
                return;
            case IVariableDeclarationGroupOperation g:
                foreach (var d in g.Declarations.SelectMany(d => d.Declarators))
                {
                    var t = LocalType(d.Symbol);
                    var v = new BoundVar(LocalName(d.Symbol), t);
                    var init = d.GetVariableInitializer();
                    if (init is null) output.Add(new VarDeclStmt([v], null));
                    else WithPending(output, () => new VarDeclStmt([v], [RhsAs(init.Value, t)]));
                }
                return;
            case IExpressionStatementOperation es:
                ExpressionStatement(es.Operation, output);
                return;
            case IConditionalOperation c:
                If(c, output);
                return;
            case IWhileLoopOperation w:
                While(w, output);
                return;
            case IForLoopOperation f:
                For(f, output);
                return;
            case IForEachLoopOperation fe:
                ForEach(fe, output);
                return;
            case IReturnOperation r:
                if (r.ReturnedValue is null) output.Add(new ReturnStmt([]));
                else if (StripImplicitConversion(r.ReturnedValue) is IThrowOperation t) Throw(t, output);
                else WithPending(output, () => new ReturnStmt([ExprAs(r.ReturnedValue, _returnType!)]));
                return;
            case IBranchOperation { BranchKind: BranchKind.Break }:
                output.Add(new BreakStmt(null));
                return;
            case IBranchOperation { BranchKind: BranchKind.Continue }:
                output.Add(new BreakStmt(null, IsContinue: true));
                return;
            case IThrowOperation t:
                Throw(t, output);
                return;
            case ISwitchOperation sw:
                Switch(sw, output);
                return;
            case IUsingOperation u:
                _diags.Warn("'using' translated as its body; disposal is not modelled", op.Syntax);
                Statement(u.Body, output);
                return;
            case ILockOperation l:
                _diags.Warn("'lock' translated as its body; concurrency is not modelled", op.Syntax);
                Statement(l.Body, output);
                return;
            default:
                _diags.Unsupported($"statement ({op.Kind})", op.Syntax);
                output.Add(new OpaqueStmt(op.Syntax.ToString()));
                return;
        }
    }

    private void Throw(IThrowOperation t, List<Stmt> output)
    {
        var what = t.Exception is { } ex ? StripImplicitConversion(ex).Syntax.ToString() : "rethrow";
        if (_options.Throws == ThrowMode.Expect)
            output.Add(new ExpectStmt(new Lit("false"), new Lit(StringLiteral("throw " + what))));
        else
            output.Add(new AssertStmt(new Lit("false"), "throw " + what));
    }

    #region Expression statements

    private void ExpressionStatement(IOperation op, List<Stmt> output)
    {
        switch (op)
        {
            case ISimpleAssignmentOperation a:
                Assign(a.Target, a.Value, null, output);
                return;
            case ICompoundAssignmentOperation ca:
                Assign(ca.Target, ca.Value, ca.OperatorKind, output);
                return;
            case IIncrementOrDecrementOperation inc:
                AssignComputed(inc.Target, output, current =>
                    new BinaryExpr(inc.Kind == OperationKind.Increment ? "+" : "-", current, new Lit("1")));
                return;
            case IDeconstructionAssignmentOperation da:
                Deconstruction(da, output);
                return;
            case IInvocationOperation inv when IsDebugAssert(inv):
                WithPending(output, () => new AssertStmt(Expr(inv.Arguments[0].Value)));
                return;
            case IInvocationOperation inv when GuardCondition(inv) is { } guard:
                output.Add(_options.Throws == ThrowMode.Expect ? new ExpectStmt(guard, null) : new AssertStmt(guard));
                return;
            case IInvocationOperation inv when MutatingCollectionCall(inv, output):
                return;
            case IInvocationOperation inv when _symbols.IsSourceType(inv.TargetMethod.ContainingType) && inv.TargetMethod.MethodKind != MethodKind.LocalFunction:
            {
                var m = inv.TargetMethod;
                WithPending(output, () =>
                {
                    var call = new Apply(Callee(m, inv.Instance), OrderedArgs(inv).Select(a => ExprAs(a.Value, Map(a.Parameter!.Type, a.Syntax))).ToList());
                    if (IsFunction(m))
                    {
                        _diags.Warn("result of a pure call is discarded", inv.Syntax);
                        return new CommentStmt("discarded: " + Printer.PrintExpr(call));
                    }
                    return m.ReturnsVoid ? new CallStmt(call) : new VarDeclStmt([new BoundVar("_", null)], [new ExprRhs(call)]);
                });
                return;
            }
            case IObjectCreationOperation:
            case IInvocationOperation:
            default:
                WithPending(output, () =>
                {
                    var e = Translate(op).E;
                    return e is Opaque ? new OpaqueStmt(op.Syntax.ToString()) : new CommentStmt("discarded: " + Printer.PrintExpr(e));
                });
                return;
        }
    }

    /// <summary>The Dafny l-value for an assignment target, noting writes to fields of "this".</summary>
    private Expr? LValue(IOperation target)
    {
        switch (target)
        {
            case ILocalReferenceOperation l:
                return new Ident(LocalName(l.Local));
            case IParameterReferenceOperation p:
                return new Ident(_paramCopies.GetValueOrDefault(p.Parameter, LocalName(p.Parameter)));
            case IFieldReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: false }:
            {
                var member = target is IFieldReferenceOperation f ? (ISymbol)f.Field : ((IPropertyReferenceOperation)target).Property;
                var instance = (target as IMemberReferenceOperation)!.Instance;
                if (!_symbols.IsSourceType(member.ContainingType)) return null;
                if (_symbols.DatatypeOf(member.ContainingType) is not null) return null;
                if (instance is IInstanceReferenceOperation) ModifiesThis = true;
                else if (!member.IsStatic) _diags.Warn("assignment to a field of another object: add it to the modifies clause", target.Syntax);
                var symbol = member is IFieldSymbol { AssociatedSymbol: IPropertySymbol backing } ? backing : member;
                return Access(symbol, instance, _symbols.MemberName(symbol));
            }
            case IDiscardOperation:
                return new Ident("_");
            default:
                return null;
        }
    }

    private void Assign(IOperation target, IOperation value, BinaryOperatorKind? compound, List<Stmt> output)
    {
        if (compound is { } kind)
        {
            AssignComputed(target, output, current => CompoundValue(kind, current, value, target));
            return;
        }
        // Element writes on value-typed collections: xs[i] = v becomes xs := xs[i := v].
        if (target is IArrayElementReferenceOperation { Indices.Length: 1 } ae)
        {
            AssignElement(ae.ArrayReference, ae.Indices[0], value, target, output);
            return;
        }
        if (target is IPropertyReferenceOperation { Property.IsIndexer: true, Instance: { } coll } ip && TypeTranslator.CollectionKind(coll.Type) is not null)
        {
            AssignElement(coll, ip.Arguments[0].Value, value, target, output);
            return;
        }
        if (target is IDiscardOperation)
        {
            WithPending(output, () => new VarDeclStmt([new BoundVar("_", null)], [RhsAs(value, ExpectedFor(value))]));
            return;
        }
        var lhs = LValue(target);
        if (lhs is null)
        {
            _diags.Unsupported("assignment target", target.Syntax);
            output.Add(new OpaqueStmt(target.Parent?.Syntax.ToString() ?? target.Syntax.ToString()));
            return;
        }
        var type = TargetType(target);
        WithPending(output, () => new AssignStmt([lhs], [RhsAs(value, type)]));
    }

    private DType TargetType(IOperation target) => target switch
    {
        ILocalReferenceOperation l => LocalType(l.Local),
        IParameterReferenceOperation p => Map(p.Parameter.Type, target.Syntax),
        IFieldReferenceOperation f => Map(f.Field.Type, target.Syntax),
        IPropertyReferenceOperation p => Map(p.Property.Type, target.Syntax),
        _ => ExpectedFor(target),
    };

    private void AssignElement(IOperation collection, IOperation index, IOperation value, IOperation site, List<Stmt> output)
    {
        var lhs = LValue(collection);
        if (lhs is null)
        {
            _diags.Unsupported("element assignment to a collection that is not a variable or field", site.Syntax);
            output.Add(new OpaqueStmt(site.Syntax.ToString()));
            return;
        }
        WarnValueSemantics(site);
        var elemType = Map(site.Type!, site.Syntax);
        WithPending(output, () => new AssignStmt([lhs], [new ExprRhs(new SeqUpdateExpr(Expr(collection), Expr(index), ExprAs(value, elemType)))]));
    }

    private void AssignComputed(IOperation target, List<Stmt> output, Func<Expr, Expr> compute)
    {
        var lhs = LValue(target);
        if (lhs is null)
        {
            _diags.Unsupported("assignment target", target.Syntax);
            output.Add(new OpaqueStmt(target.Parent?.Syntax.ToString() ?? target.Syntax.ToString()));
            return;
        }
        var type = TargetType(target);
        WithPending(output, () => new AssignStmt([lhs], [new ExprRhs(Coerce(new TExpr(compute(Expr(target)), type), type))]));
    }

    private Expr CompoundValue(BinaryOperatorKind kind, Expr current, IOperation value, IOperation target)
    {
        var t = target.Type;
        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && TypeTranslator.IsSignedIntegral(t))
        {
            var currentInt = new ConversionExpr(current, Types.Int);
            return new ConversionExpr(new Apply(new Ident(kind == BinaryOperatorKind.Divide ? "Div" : "Rem"), [currentInt, ExprAs(value, Types.Int)]), Map(t!, target.Syntax));
        }
        if (kind == BinaryOperatorKind.Add && t?.SpecialType == SpecialType.System_String)
            return new BinaryExpr("+", current, StringOperand(value));
        var op = OperatorText(kind);
        if (op is null || kind is BinaryOperatorKind.Remainder && TypeTranslator.IsFloating(t))
            return Unsupported($"compound assignment {kind}", target.Parent ?? target);
        return new BinaryExpr(op, current, ExprAs(value, Map(t!, target.Syntax)));
    }

    private void Deconstruction(IDeconstructionAssignmentOperation da, List<Stmt> output)
    {
        var declares = da.Target is IDeclarationExpressionOperation;
        var tupleTarget = da.Target is IDeclarationExpressionOperation { Expression: ITupleOperation dt } ? dt : da.Target as ITupleOperation;
        var targets = tupleTarget?.Elements ?? default;
        if (tupleTarget is null)
        {
            _diags.Unsupported("deconstruction", da.Syntax);
            output.Add(new OpaqueStmt(da.Syntax.ToString()));
            return;
        }
        WithPending(output, () =>
        {
            var rhss = da.Value is ITupleOperation tv
                ? tv.Elements.Select((e, k) => (Rhs)new ExprRhs(ExprAs(e, TargetType(targets[k])))).ToList()
                : null;
            if (declares)
            {
                var vars = targets.Select(t => t is ILocalReferenceOperation l ? new BoundVar(LocalName(l.Local), LocalType(l.Local)) : new BoundVar("_", null)).ToList();
                if (rhss is not null) return new VarDeclStmt(vars, rhss);
                var tmp = FreshTemp();
                _pending!.Add(new VarDeclStmt([new BoundVar(tmp, null)], [new ExprRhs(Expr(da.Value))]));
                return new VarDeclStmt(vars, vars.Select((_, k) => (Rhs)new ExprRhs(new MemberSel(new Ident(tmp), k.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList());
            }
            var lhss = targets.Select(LValue).ToList();
            if (lhss.Any(l => l is null)) return new OpaqueStmt(da.Syntax.ToString());
            if (rhss is not null) return new AssignStmt(lhss!, rhss);
            var t2 = FreshTemp();
            _pending!.Add(new VarDeclStmt([new BoundVar(t2, null)], [new ExprRhs(Expr(da.Value))]));
            return new AssignStmt(lhss!, lhss.Select((_, k) => (Rhs)new ExprRhs(new MemberSel(new Ident(t2), k.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList());
        });
    }

    /// <summary>list.Add(x), set.Add(x), dict[k] = v... on mutable collections, as value updates of the variable.</summary>
    private bool MutatingCollectionCall(IInvocationOperation inv, List<Stmt> output)
    {
        var m = inv.TargetMethod;
        if (inv.Instance is not { } coll || IsImmutable(coll.Type)) return false;
        var kind = TypeTranslator.CollectionKind(coll.Type);
        if (kind is null) return false;
        var lhs = LValue(coll);
        if (lhs is null) return false;
        var args = OrderedArgs(inv).Select(a => a.Value).ToList();
        Expr? updated = (m.Name, args.Count, kind) switch
        {
            ("Add", 1, "seq" or "set") => new BinaryExpr("+", lhs, new DisplayExpr(kind, [Expr(args[0])])),
            ("AddRange", 1, "seq") => new BinaryExpr("+", lhs, Expr(args[0])),
            ("UnionWith", 1, "set") => new BinaryExpr("+", lhs, ToKind(Translate(args[0]), "set", inv)),
            ("Remove", 1, "set" or "map") => new BinaryExpr("-", lhs, new DisplayExpr("set", [Expr(args[0])])),
            ("Add", 2, "map") => new SeqUpdateExpr(lhs, Expr(args[0]), Expr(args[1])),
            ("Clear", 0, _) => kind == "map" ? new MapDisplayExpr([]) : new DisplayExpr(kind, []),
            _ => null,
        };
        if (updated is null) return false;
        WarnValueSemantics(inv);
        if (m.Name == "Add" && kind == "map")
            output.Add(new AssertStmt(new BinaryExpr("!in", Expr(args[0]), lhs), "Dictionary.Add throws on a duplicate key"));
        WithPending(output, () => new AssignStmt([lhs], [new ExprRhs(updated)]));
        return true;
    }

    #endregion

    private void WarnValueSemantics(IOperation site) =>
        _diags.WarnOnce("value-semantics:" + site.Syntax.SyntaxTree.FilePath + site.Syntax.SpanStart,
            "in-place collection update translated as a value update of the variable; other references to the same collection will not see it",
            site.Syntax);

    #region Control flow

    private void If(IConditionalOperation c, List<Stmt> output)
    {
        // if (bad) throw ...;  =>  assert !bad;
        if (c.WhenFalse is null && SingleThrow(c.WhenTrue) is { } t)
        {
            WithPending(output, () =>
            {
                var cond = Negate(Expr(c.Condition));
                var what = t.Exception is { } ex ? StripImplicitConversion(ex).Syntax.ToString() : "rethrow";
                return _options.Throws == ThrowMode.Expect
                    ? new ExpectStmt(cond, new Lit(StringLiteral("throw " + what)))
                    : new AssertStmt(cond, "else throw " + what);
            });
            return;
        }
        WithPending(output, () =>
        {
            var cond = Expr(c.Condition);
            var saved = _pending;
            _pending = null;
            try
            {
                var then = Block(c.WhenTrue);
                Stmt? els = c.WhenFalse switch
                {
                    null => null,
                    IConditionalOperation nested => Nested(nested),
                    var other => Block(other),
                };
                return new IfStmt(cond, then, els);
            }
            finally
            {
                _pending = saved;
            }
        });
    }

    private Stmt Nested(IConditionalOperation nested)
    {
        var output = new List<Stmt>();
        If(nested, output);
        return output.Count == 1 ? output[0] : new BlockStmt(output);
    }

    private static IThrowOperation? SingleThrow(IOperation op)
    {
        if (op is IBlockOperation { Operations.Length: 1 } b) op = b.Operations[0];
        if (op is IExpressionStatementOperation es) op = es.Operation;
        return op as IThrowOperation;
    }

    private void While(IWhileLoopOperation w, List<Stmt> output)
    {
        var condPending = new List<Stmt>();
        var saved = _pending;
        _pending = condPending;
        var cond = w.Condition is null ? null : Expr(w.Condition);
        _pending = saved;
        var body = Block(w.Body).Body.ToList();

        if (w.ConditionIsTop && condPending.Count == 0)
        {
            output.Add(new WhileStmt(cond, new BlockStmt(body)));
            return;
        }
        // Conditions with hoisted calls, and do-while loops, test inside an infinite loop.
        if (!w.ConditionIsTop && w.Body.DescendantsAndSelf().Any(o => o is IBranchOperation { BranchKind: BranchKind.Continue }))
            _diags.Warn("'continue' in a do-while loop skips the condition check in the translation", w.Syntax);
        var exit = cond is null ? [] : new List<Stmt>(condPending) { new IfStmt(Negate(cond), new BlockStmt([new BreakStmt(null)]), null) };
        var loop = w.ConditionIsTop ? exit.Concat(body).ToList() : body.Concat(exit).ToList();
        output.Add(new WhileStmt(null, new BlockStmt(loop)));
    }

    private void For(IForLoopOperation f, List<Stmt> output)
    {
        if (TryCountingFor(f) is { } counting)
        {
            output.Add(counting);
            return;
        }
        foreach (var b in f.Before) Statement(b, output);
        if (f.Body.DescendantsAndSelf().Any(o => o is IBranchOperation { BranchKind: BranchKind.Continue }))
            _diags.Warn("'continue' in a for loop skips the increment in the translation", f.Syntax);
        var condPending = new List<Stmt>();
        var saved = _pending;
        _pending = condPending;
        var cond = f.Condition is null ? null : Expr(f.Condition);
        _pending = saved;
        var body = Block(f.Body).Body.ToList();
        foreach (var step in f.AtLoopBottom) Statement(step, body);
        if (condPending.Count == 0)
        {
            output.Add(new WhileStmt(cond, new BlockStmt(body)));
            return;
        }
        var exit = new List<Stmt>(condPending) { new IfStmt(Negate(cond!), new BlockStmt([new BreakStmt(null)]), null) };
        output.Add(new WhileStmt(null, new BlockStmt(exit.Concat(body).ToList())));
    }

    /// <summary>for (int i = lo; i &lt; hi; i++) with i and hi untouched by the body becomes "for i := lo to hi".</summary>
    private Stmt? TryCountingFor(IForLoopOperation f)
    {
        if (f.Before is not [IVariableDeclarationGroupOperation { Declarations: [{ Declarators: [var decl] }] }]) return null;
        if (f.AtLoopBottom is not [IExpressionStatementOperation { Operation: var step }]) return null;
        var i = decl.Symbol;
        var init = decl.GetVariableInitializer()?.Value;
        if (init is null) return null;
        bool IsI(IOperation o) => o is ILocalReferenceOperation l && SymbolEqualityComparer.Default.Equals(l.Local, i);
        var up = step is IIncrementOrDecrementOperation { Kind: OperationKind.Increment } inc && IsI(inc.Target)
                 || step is ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Value.ConstantValue.Value: 1 } ca && IsI(ca.Target);
        var down = step is IIncrementOrDecrementOperation { Kind: OperationKind.Decrement } dec && IsI(dec.Target);
        if (!up && !down) return null;
        if (f.Condition is not IBinaryOperation cond || !IsI(cond.LeftOperand)) return null;

        // The loop variable and every variable in the bound must be unchanged by the body.
        var assigned = f.Body.DescendantsAndSelf().Where(o => o is IAssignmentOperation or IIncrementOrDecrementOperation)
            .Select(o => o is IAssignmentOperation a ? a.Target : ((IIncrementOrDecrementOperation)o).Target)
            .Select(t => t is ILocalReferenceOperation l ? (ISymbol)l.Local : t is IParameterReferenceOperation p ? p.Parameter : null)
            .Where(s => s is not null).ToHashSet(SymbolEqualityComparer.Default);
        if (assigned.Contains(i)) return null;
        if (cond.RightOperand.DescendantsAndSelf().Any(o =>
                o is ILocalReferenceOperation l && assigned.Contains(l.Local) || o is IParameterReferenceOperation p && assigned.Contains(p.Parameter) ||
                o is IInvocationOperation || o is IFieldReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: true }))
            return null;

        var saved = _pending;
        _pending = null;
        try
        {
            var type = Map(i.Type, decl.Syntax);
            var bound = ExprAs(cond.RightOperand, type);
            var start = ExprAs(init, type);
            Expr lo, hi;
            if (up)
            {
                lo = start;
                hi = cond.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => bound,
                    BinaryOperatorKind.LessThanOrEqual => new BinaryExpr("+", bound, new Lit("1")),
                    _ => null!,
                };
                if (hi is null) return null;
            }
            else
            {
                // for (i = hi - 1; i >= lo; i--)  =>  for i := hi downto lo
                if (start is not BinaryExpr { Op: "-", Right: Lit { Text: "1" } } minusOne) return null;
                hi = minusOne.Left;
                lo = cond.OperatorKind switch
                {
                    BinaryOperatorKind.GreaterThanOrEqual => bound,
                    BinaryOperatorKind.GreaterThan => new BinaryExpr("+", bound, new Lit("1")),
                    _ => null!,
                };
                if (lo is null) return null;
            }
            _pending = saved;
            var body = Block(f.Body);
            return up ? new ForStmt($"{LocalName(i)}: {Printer.TypeToString(type)}", lo, hi, false, body)
                      : new ForStmt($"{LocalName(i)}: {Printer.TypeToString(type)}", hi, lo, true, body);
        }
        finally
        {
            _pending = saved;
        }
    }

    private void ForEach(IForEachLoopOperation fe, List<Stmt> output)
    {
        var coll = StripImplicitConversion(fe.Collection);
        if (fe.LoopControlVariable is not IVariableDeclaratorOperation { Symbol: var x })
        {
            _diags.Unsupported("foreach with a deconstructed loop variable", fe.Syntax);
            output.Add(new OpaqueStmt(fe.Syntax.ToString()));
            return;
        }
        var xType = LocalType(x);
        var xName = LocalName(x);

        // foreach (var i in Enumerable.Range(a, n))  =>  for i := a to a + n
        if (coll is IInvocationOperation { TargetMethod.Name: "Range", Arguments.Length: 2 } range && Owner(range.TargetMethod) == "System.Linq.Enumerable")
        {
            WithPending(output, () =>
            {
                var start = Simple(new TExpr(ExprAs(range.Arguments[0].Value, xType), xType));
                var count = ExprAs(range.Arguments[1].Value, xType);
                return new ForStmt($"{xName}: {Printer.TypeToString(xType)}", start.E, new BinaryExpr("+", start.E, count), false, Block(fe.Body));
            });
            return;
        }

        var kind = TypeTranslator.CollectionKind(coll.Type) ?? (coll.Type?.SpecialType == SpecialType.System_String ? "seq" : null);
        if (kind is null)
        {
            _diags.Unsupported("foreach over this collection type", fe.Syntax);
            output.Add(new OpaqueStmt(fe.Syntax.ToString()));
            return;
        }
        WithPending(output, () =>
        {
            // Evaluate the collection once, as C# does.
            var source = Translate(coll);
            var tmp = FreshTemp();
            _pending!.Add(new VarDeclStmt([new BoundVar(tmp, null)], [new ExprRhs(Coerce(source, Unwrap(source).Value.T))]));
            var s = new Ident(tmp);
            var body = Block(fe.Body).Body.ToList();
            if (kind == "seq")
            {
                var idx = tmp + "i";
                body.Insert(0, new VarDeclStmt([new BoundVar(xName, xType)], [new ExprRhs(new IndexExpr(s, [new Ident(idx)]))]));
                return new ForStmt(idx, new Lit("0"), new CardinalityExpr(s), false, new BlockStmt(body));
            }
            // Sets and maps have no order: iterate by repeatedly choosing an element (any order C# might use).
            _diags.Warn($"foreach over a {kind} iterates in an unspecified order", fe.Syntax);
            var remaining = tmp + "r";
            var pick = kind == "map" ? tmp + "k" : xName;
            var loopBody = new List<Stmt>
            {
                new AssignSuchThatStmt(pick, new Ident(remaining)),
                new AssignStmt([new Ident(remaining)], [new ExprRhs(new BinaryExpr("-", new Ident(remaining), new DisplayExpr("set", [new Ident(pick)])))]),
            };
            if (kind == "map")
                loopBody.Add(new VarDeclStmt([new BoundVar(xName, xType)], [new ExprRhs(new TupleExpr([new Ident(pick), new IndexExpr(s, [new Ident(pick)])]))]));
            loopBody.AddRange(body);
            var keys = kind == "map" ? new MemberSel(s, "Keys") : (Expr)s;
            _pending.Add(new VarDeclStmt([new BoundVar(remaining, null)], [new ExprRhs(keys)]));
            return new WhileStmt(new BinaryExpr("!=", new Ident(remaining), new DisplayExpr("set", [])), new BlockStmt(loopBody));
        });
    }

    private void Switch(ISwitchOperation sw, List<Stmt> output)
    {
        WithPending(output, () =>
        {
            var subject = Simple(Translate(sw.Value));
            var sections = sw.Cases.Select(section =>
            {
                var body = section.Body.ToList();
                if (body.Count > 0 && body[^1] is IBranchOperation { BranchKind: BranchKind.Break }) body.RemoveAt(body.Count - 1);
                if (body.Any(o => o.DescendantsAndSelf().Any(d => d is IBranchOperation { BranchKind: BranchKind.Break } br && IsSwitchBreak(br, sw))))
                    _diags.Unsupported("'break' out of the middle of a switch section", section.Syntax);
                return (section, body);
            }).ToList();

            var saved = _pending;
            _pending = null;
            try
            {
                // Prefer a Dafny match when every section is a single plain constructor pattern.
                var arms = sections.Select(s => s.section.Clauses.Length == 1 ? ClauseAsPattern(s.section.Clauses[0]) : null).ToList();
                if (arms.All(a => a is not null))
                {
                    var asArms = arms.Select(a => new Arm(a!.Value.Pattern, a.Value.Guard, false, null)).ToList();
                    if (TryMatchPatterns(sw.Value.Type, subject, asArms) is { } patterns)
                    {
                        var cases = sections.Select((s, k) => (s, k)).Where(x => patterns[x.k] != Unreachable)
                            .Select(x => new MatchCaseStmt(patterns[x.k], StatementsOf(x.s.body))).ToList();
                        if (!patterns.Any(p => p is WildPattern or CtorPattern { Ctor: "#unreachable" }) && _symbols.DatatypeOf(sw.Value.Type) is { } dt &&
                            dt.Ctors.Any(c => !patterns.OfType<CtorPattern>().Any(p => p.Ctor == c.Name)))
                            cases.Add(new MatchCaseStmt(new WildPattern(), []));
                        return new MatchStmt(subject.E, cases);
                    }
                }

                // Otherwise an if / else-if chain, with the default section last.
                Stmt? chain = null;
                var ordered = sections.OrderBy(s => s.section.Clauses.Any(c => c is IDefaultCaseClauseOperation) ? 1 : 0).Reverse();
                foreach (var (section, body) in ordered)
                {
                    Expr cond = new Lit("false");
                    IOperation? guard = null;
                    foreach (var clause in section.Clauses)
                    {
                        var (c, g, _) = ClauseCondition(clause, subject);
                        cond = cond is Lit { Text: "false" } ? c : c is Lit { Text: "true" } ? c : new BinaryExpr("||", cond, c);
                        guard ??= g;
                    }
                    if (guard is not null) cond = And(cond, Expr(guard));
                    var block = new BlockStmt(StatementsOf(body));
                    chain = cond is Lit { Text: "true" } ? block : new IfStmt(cond, block, chain);
                }
                return chain;
            }
            finally
            {
                _pending = saved;
            }
        });
    }

    private static bool IsSwitchBreak(IBranchOperation br, ISwitchOperation sw)
    {
        for (var p = br.Parent; p is not null; p = p.Parent)
        {
            if (p is ILoopOperation) return false;
            if (p == sw) return true;
        }
        return false;
    }

    private static (IPatternOperation? Pattern, IOperation? Guard)? ClauseAsPattern(ICaseClauseOperation clause) => clause switch
    {
        IPatternCaseClauseOperation pc => (pc.Pattern, pc.Guard),
        IDefaultCaseClauseOperation => (null, null),
        _ => null,
    };

    private List<Stmt> StatementsOf(IEnumerable<IOperation> ops)
    {
        var output = new List<Stmt>();
        foreach (var op in ops) Statement(op, output);
        return output;
    }

    #endregion
}

