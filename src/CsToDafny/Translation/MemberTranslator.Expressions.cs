using System.Globalization;
using System.Text;
using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

public sealed partial class MemberTranslator
{
    /// <summary>Value standing for the implicit receiver inside property sub-patterns and conditional access.</summary>
    private TExpr? _implicitInstance;

    private TExpr Translate(IOperation op)
    {
        if (TryConstant(op) is { } constant) return constant;

        switch (op)
        {
            case IParenthesizedOperation p:
                return Translate(p.Operand);
            case IConversionOperation conv:
                return Conversion(conv);
            case ILocalReferenceOperation l:
                return _aliases.TryGetValue(l.Local, out var alias)
                    ? new TExpr(alias, LocalType(l.Local))
                    : new TExpr(new Ident(LocalName(l.Local)), LocalType(l.Local));
            case IParameterReferenceOperation p:
                return new TExpr(new Ident(_paramCopies.GetValueOrDefault(p.Parameter, LocalName(p.Parameter))), Map(p.Parameter.Type, op.Syntax));
            case IInstanceReferenceOperation ir:
                if (ir.ReferenceKind is InstanceReferenceKind.PatternInput or InstanceReferenceKind.ImplicitReceiver && _implicitInstance is { } inst)
                    return inst;
                return new TExpr(new Lit("this"), MapNonNull(op.Type!, op.Syntax));
            case IConditionalAccessInstanceOperation when _implicitInstance is { } ca:
                return ca;
            case IFieldReferenceOperation f:
                return FieldReference(f);
            case IPropertyReferenceOperation p:
                return PropertyReference(p);
            case IInvocationOperation inv:
                return Invocation(inv);
            case IBinaryOperation b:
                return Binary(b);
            case IUnaryOperation u:
                return Unary(u);
            case IConditionalOperation { WhenFalse: not null } c:
                return Conditional(c);
            case ICoalesceOperation co:
                return Coalesce(co);
            case IIsPatternOperation ip:
            {
                var subject = Simple(Translate(ip.Value));
                var (cond, bindings) = Pattern(ip.Pattern, subject);
                Bind(bindings);
                return new TExpr(cond, Types.Bool);
            }
            case IIsTypeOperation it:
            {
                var cond = TypeTest(Translate(it.ValueOperand), it.TypeOperand, op);
                return new TExpr(it.IsNegated ? Negate(cond) : cond, Types.Bool);
            }
            case ISwitchExpressionOperation sw:
                return SwitchExpression(sw);
            case IObjectCreationOperation oc:
                return ObjectCreation(oc);
            case IWithOperation w:
                return With(w);
            case ITupleOperation t:
                return new TExpr(new TupleExpr(t.Elements.Select(Expr).ToList()), MapNonNull(t.Type!, op.Syntax));
            case IDelegateCreationOperation dc:
                return Translate(dc.Target);
            case IAnonymousFunctionOperation lam:
                return Lambda(lam);
            case IMethodReferenceOperation mr:
                return new TExpr(Callee(mr.Method, mr.Instance), MapNonNull(op.Type ?? _symbols.Compilation.ObjectType, op.Syntax));
            case IArrayCreationOperation ac:
                return ArrayCreation(ac);
            case ICollectionExpressionOperation ce:
                return CollectionExpression(ce);
            case IArrayElementReferenceOperation ae when ae.Indices.Length == 1:
                return new TExpr(new IndexExpr(Expr(ae.ArrayReference), [Expr(ae.Indices[0])]), MapNonNull(ae.Type!, op.Syntax));
            case IInterpolatedStringOperation s:
                return InterpolatedString(s);
            case IConditionalAccessOperation ca:
                return ConditionalAccess(ca);
            case IDefaultValueOperation dv when dv.Type is not null:
                return new TExpr(DefaultValue(dv.Type, op), Map(dv.Type, op.Syntax));
            case IThrowOperation:
                return new TExpr(Unsupported("throw in this position", op), NumLit);
            default:
                return new TExpr(Unsupported(op.Kind.ToString(), op), op.Type is null ? NumLit : MapNonNull(op.Type, op.Syntax));
        }
    }

    /// <summary>Make an expression safe to duplicate: hoist it into a temporary (method context) if it is not a simple path.</summary>
    private TExpr Simple(TExpr x)
    {
        if (IsSimple(x.E) || _pending is null) return x;
        var tmp = FreshTemp();
        _pending.Add(new VarDeclStmt([new BoundVar(tmp, x.T)], [new ExprRhs(x.E)]));
        return x with { E = new Ident(tmp) };
    }

    private static bool IsSimple(Expr e) => e switch
    {
        Ident or Lit => true,
        MemberSel m => IsSimple(m.Target),
        _ => false,
    };

    private void Bind(IEnumerable<(ISymbol Symbol, Expr Value)> bindings)
    {
        foreach (var (sym, value) in bindings) _aliases[sym] = value;
    }

    #region Constants and literals

    private TExpr? TryConstant(IOperation op)
    {
        if (!op.ConstantValue.HasValue) return null;
        switch (op)
        {
            case ILiteralOperation or INameOfOperation:
                break;
            case IUnaryOperation { Operand: ILiteralOperation }:
                break;
            case IConversionOperation conv when conv.Operand is ILiteralOperation or IUnaryOperation { Operand: ILiteralOperation }
                                                && !(conv.Operand.Type?.SpecialType == SpecialType.System_Char && conv.Type?.SpecialType != SpecialType.System_Char):
                break;
            // Constants from referenced assemblies (int.MaxValue, Math.PI...) are inlined; source constants keep their names.
            case IFieldReferenceOperation f when !_symbols.IsSourceType(f.Field.ContainingType):
                break;
            default:
                return null;
        }
        return Literal(op.ConstantValue.Value, op.Type, op);
    }

    private TExpr Literal(object? value, ITypeSymbol? type, IOperation op)
    {
        switch (value)
        {
            case null:
                return new TExpr(new Lit("null"), NullLitType);
            case bool b:
                return new TExpr(new Lit(b ? "true" : "false"), Types.Bool);
            case string s:
                return new TExpr(new Lit(StringLiteral(s)), Types.String);
            case char c:
                return new TExpr(new Lit(CharLiteral(c)), Types.Char);
            case float or double or decimal:
            {
                var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                var text = d.ToString(CultureInfo.InvariantCulture);
                if (!text.Contains('.')) text += ".0";
                return new TExpr(new Lit(text), Types.Real);
            }
            case sbyte or byte or short or ushort or int or uint or long or ulong:
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                return new TExpr(new Lit(text), NumLit);
            }
            default:
                return new TExpr(Unsupported($"constant of type {value.GetType().Name}", op), NumLit);
        }
    }

    public static string StringLiteral(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var r in s.EnumerateRunes()) sb.Append(EscapeRune(r, '"'));
        return sb.Append('"').ToString();
    }

    private static string CharLiteral(char c) => "'" + (char.IsSurrogate(c) ? $"\\U{{{(int)c:X4}}}" : EscapeRune(new Rune(c), '\'')) + "'";

    private static string EscapeRune(Rune r, char quote) => r.Value switch
    {
        '\\' => "\\\\",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\0' => "\\0",
        _ when r.Value == quote => "\\" + quote,
        _ when r.Value < 0x20 || r.Value > 0x7E => $"\\U{{{r.Value:X4}}}",
        _ => r.ToString(),
    };

    private Expr DefaultValue(ITypeSymbol t, IOperation site)
    {
        var mapped = Map(t, site.Syntax);
        if (TypeTranslator.IsOption(mapped, out _)) return new Ident("None");
        if (t.SpecialType == SpecialType.System_Boolean) return new Lit("false");
        if (t.SpecialType == SpecialType.System_Char) return new Lit("'\\0'");
        if (TypeTranslator.IsIntegral(t) || TypeTranslator.IsBigInteger(t)) return new Lit("0");
        if (TypeTranslator.IsFloating(t)) return new Lit("0.0");
        if (t.IsReferenceType && mapped is NamedType { Name: var n } && n.EndsWith('?')) return new Lit("null");
        _diags.Warn($"default value of '{t.ToDisplayString()}' is null in C#; it has no Dafny equivalent", site.Syntax);
        return new Opaque($"default({t.ToDisplayString()})");
    }

    #endregion

    #region Conversions

    private TExpr Conversion(IConversionOperation conv)
    {
        var target = conv.Type!;
        var source = conv.Operand.Type;
        if (conv.OperatorMethod is not null)
            return new TExpr(Unsupported("user-defined conversion", conv), Map(target, conv.Syntax));
        if (source?.TypeKind == TypeKind.Enum && target.TypeKind != TypeKind.Enum ||
            target.TypeKind == TypeKind.Enum && source?.TypeKind != TypeKind.Enum)
            return new TExpr(Unsupported("conversion between an enum and its underlying value", conv), Map(target, conv.Syntax));

        var inner = Translate(conv.Operand);
        var mapped = Map(target, conv.Syntax);
        // Downcasts between Dafny reference types need an explicit "as"; datatype downcasts are identities.
        if (conv.Conversion.IsReference && !conv.IsImplicit && _types.IsDafnyReferenceType(target) &&
            !TypeTranslator.TypeEquals(inner.T, mapped))
            return new TExpr(new ConversionExpr(inner.E, mapped), mapped);
        if (conv.Conversion.IsNumeric && !conv.IsImplicit && TypeTranslator.IsIntegral(target) && TypeTranslator.IsFloating(source))
            return new TExpr(new ConversionExpr(new MemberSel(inner.E, "Floor"), mapped), mapped) is var t && IsTowardZeroSafe(conv)
                ? t
                : new TExpr(Unsupported("floating-point to integer cast (truncation toward zero)", conv), mapped);
        return new TExpr(Coerce(inner, mapped), mapped);
    }

    private bool IsTowardZeroSafe(IConversionOperation conv)
    {
        _diags.Warn("floating-point to integer cast translated with .Floor, which differs from C# truncation for negative values", conv.Syntax);
        return true;
    }

    #endregion

    #region Member access

    /// <summary>The receiver of an instance member access, or null for an implicit "this".</summary>
    private Expr? Receiver(IOperation? instance)
    {
        if (instance is null) return null;
        if (instance is IInstanceReferenceOperation { IsImplicit: true, ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) return null;
        return Expr(instance);
    }

    private TExpr ReceiverT(IOperation instance)
    {
        var t = Translate(instance);
        // Dereferencing requires a non-null value.
        return TypeTranslator.IsOption(t.T, out var inner) ? new TExpr(new MemberSel(t.E, "value"), inner) : t;
    }

    private Expr StaticOwner(INamedTypeSymbol owner)
    {
        var dt = _symbols.DatatypeOf(owner);
        return new Ident(dt?.Name ?? _symbols.TypeName(owner));
    }

    private bool IsCurrentType(INamedTypeSymbol t)
    {
        var a = _symbols.DatatypeOf(t)?.Root ?? t.OriginalDefinition;
        var b = _symbols.DatatypeOf(_containingType)?.Root ?? _containingType.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(a, b);
    }

    private Expr Access(ISymbol member, IOperation? instance, string name)
    {
        if (member.IsStatic)
            return IsCurrentType(member.ContainingType) ? new Ident(name) : new MemberSel(StaticOwner(member.ContainingType), name);
        var recv = Receiver(instance);
        return recv is null ? new Ident(name) : new MemberSel(recv, name);
    }

    private TExpr FieldReference(IFieldReferenceOperation f)
    {
        var field = f.Field;
        if (field.ContainingType.IsTupleType)
        {
            var tupleField = field.CorrespondingTupleField ?? field;
            var index = int.Parse(tupleField.Name["Item".Length..], CultureInfo.InvariantCulture) - 1;
            return new TExpr(new MemberSel(ReceiverT(f.Instance!).E, index.ToString(CultureInfo.InvariantCulture)), Map(field.Type, f.Syntax));
        }
        if (field.ContainingType.TypeKind == TypeKind.Enum && _symbols.CtorOfEnumMember(field) is { } ctor)
            return new TExpr(new MemberSel(new Ident(ctor.Datatype.Name), ctor.Name), Map(field.Type, f.Syntax));
        if (field.ContainingType.SpecialType == SpecialType.System_String && field.Name == "Empty")
            return new TExpr(new Lit("\"\""), Types.String);
        if (!_symbols.IsSourceType(field.ContainingType))
            return new TExpr(Unsupported("field of an external type", f), Map(field.Type, f.Syntax));
        if (field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol backing)
            return new TExpr(Access(backing, f.Instance, _symbols.MemberName(backing)), Map(field.Type, f.Syntax));
        return new TExpr(Access(field, f.Instance, _symbols.MemberName(field)), Map(field.Type, f.Syntax));
    }

    private TExpr PropertyReference(IPropertyReferenceOperation p)
    {
        var prop = p.Property;
        if (BclProperty(p) is { } mapped) return mapped;
        if (!_symbols.IsSourceType(prop.ContainingType))
            return new TExpr(Unsupported($"property {prop.ContainingType.Name}.{prop.Name}", p), MapNonNull(p.Type!, p.Syntax));
        if (prop.IsIndexer)
            return new TExpr(Unsupported("user-defined indexer", p), Map(prop.Type, p.Syntax));

        var name = _symbols.MemberName(prop);
        var type = Map(prop.Type, p.Syntax);
        if (!SymbolModel.IsComputedProperty(prop) && !prop.IsAbstract && prop.ContainingType.TypeKind != TypeKind.Interface)
            return new TExpr(Access(prop, p.Instance, name), type);

        // Computed properties become parameterless functions (or methods).
        var call = new Apply(Access(prop, p.Instance, name), []);
        return prop.GetMethod is { } getter && !_symbols.Functions.Contains(getter.OriginalDefinition)
            ? new TExpr(HoistCall(call, type, p), type)
            : new TExpr(call, type);
    }

    /// <summary>Method calls and allocations cannot appear in Dafny expressions: bind them to a temporary first.</summary>
    private Expr HoistCall(Expr call, DType type, IOperation site) => HoistRhs(new ExprRhs(call), type, site);

    private Expr HoistRhs(Rhs rhs, DType type, IOperation site)
    {
        if (_pending is null)
        {
            _diags.Unsupported("method call or allocation inside an expression that must be pure (function, lambda or contract)", site.Syntax);
            return rhs is ExprRhs er ? er.Expr : new Opaque(site.Syntax.ToString());
        }
        var tmp = FreshTemp();
        _pending.Add(new VarDeclStmt([new BoundVar(tmp, type)], [rhs]));
        return new Ident(tmp);
    }

    #endregion

    #region Invocations

    private Expr Callee(IMethodSymbol m, IOperation? instance)
    {
        var name = _symbols.MemberName(m);
        return Access(m, instance, name);
    }

    private List<IArgumentOperation> OrderedArgs(IInvocationOperation inv) =>
        inv.Arguments.OrderBy(a => a.Parameter?.Ordinal ?? int.MaxValue).ToList();

    private TExpr Invocation(IInvocationOperation inv)
    {
        var m = inv.TargetMethod;
        if (BclInvocation(inv) is { } mapped) return mapped;

        var args = OrderedArgs(inv);
        if (m.MethodKind == MethodKind.DelegateInvoke)
        {
            var fn = ReceiverT(inv.Instance!);
            return new TExpr(new Apply(fn.E, args.Select(a => ExprAs(a.Value, Map(a.Parameter!.Type, a.Syntax))).ToList()), Map(m.ReturnType, inv.Syntax));
        }
        if (m.MethodKind == MethodKind.LocalFunction)
            return new TExpr(Unsupported("local function", inv), Map(m.ReturnType, inv.Syntax));
        if (!_symbols.IsSourceType(m.ContainingType))
            return new TExpr(Unsupported($"call to {m.ContainingType.Name}.{m.Name}", inv), m.ReturnsVoid ? NumLit : Map(m.ReturnType, inv.Syntax));
        if (args.Any(a => a.Parameter!.RefKind != RefKind.None))
            return new TExpr(Unsupported("ref/out arguments", inv), Map(m.ReturnType, inv.Syntax));

        var call = new Apply(Callee(m, inv.Instance), args.Select(a => ExprAs(a.Value, Map(a.Parameter!.Type, a.Syntax))).ToList());
        var type = m.ReturnsVoid ? NumLit : Map(m.ReturnType, inv.Syntax);
        return IsFunction(m) ? new TExpr(call, type) : new TExpr(HoistCall(call, type, inv), type);
    }

    private bool IsFunction(IMethodSymbol m) => _symbols.Functions.Contains(m.OriginalDefinition);

    #endregion

    #region Operators

    private static string? OperatorText(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.Add => "+",
        BinaryOperatorKind.Subtract => "-",
        BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Remainder => "%",
        BinaryOperatorKind.Equals or BinaryOperatorKind.ObjectValueEquals => "==",
        BinaryOperatorKind.NotEquals or BinaryOperatorKind.ObjectValueNotEquals => "!=",
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        BinaryOperatorKind.ConditionalAnd => "&&",
        BinaryOperatorKind.ConditionalOr => "||",
        _ => null,
    };

    private static bool IsNullConstant(IOperation op) =>
        op.ConstantValue is { HasValue: true, Value: null } ||
        op is IConversionOperation { Operand: var inner } && IsNullConstant(inner);

    private TExpr Binary(IBinaryOperation b)
    {
        var kind = b.OperatorKind;
        var resultType = b.Type is null ? Types.Bool : MapNonNull(b.Type, b.Syntax);
        if (b.OperatorMethod is { } userOp && _symbols.IsSourceType(userOp.ContainingType))
            return new TExpr(Unsupported("user-defined operator", b), resultType);

        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            if (IsNullConstant(b.RightOperand)) return new TExpr(NullTest(b.LeftOperand, kind == BinaryOperatorKind.Equals), Types.Bool);
            if (IsNullConstant(b.LeftOperand)) return new TExpr(NullTest(b.RightOperand, kind == BinaryOperatorKind.Equals), Types.Bool);
        }

        if (b.IsLifted && kind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
            return new TExpr(Unsupported("lifted operator on nullable values", b), resultType);

        var leftType = b.LeftOperand.Type;
        var isBool = leftType?.SpecialType == SpecialType.System_Boolean;
        var isString = b.LeftOperand.Type?.SpecialType == SpecialType.System_String || b.RightOperand.Type?.SpecialType == SpecialType.System_String;

        if (kind == BinaryOperatorKind.Add && isString)
            return new TExpr(new BinaryExpr("+", StringOperand(b.LeftOperand), StringOperand(b.RightOperand)), Types.String);

        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && TypeTranslator.IsSignedIntegral(b.Type))
        {
            // C# truncates toward zero; the prelude's Div/Rem model that. The result is converted back to the
            // C# type, which makes Dafny prove the int.MinValue / -1 case cannot overflow.
            var fn = kind == BinaryOperatorKind.Divide ? "Div" : "Rem";
            return new TExpr(new Apply(new Ident(fn), [ExprAs(b.LeftOperand, Types.Int), ExprAs(b.RightOperand, Types.Int)]), Types.Int);
        }
        if (kind == BinaryOperatorKind.Remainder && TypeTranslator.IsFloating(b.Type))
            return new TExpr(Unsupported("floating-point remainder", b), resultType);

        if (isBool && kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr)
        {
            var op = kind switch { BinaryOperatorKind.And => "&&", BinaryOperatorKind.Or => "||", _ => "!=" };
            return new TExpr(new BinaryExpr(op, Expr(b.LeftOperand), Expr(b.RightOperand)), Types.Bool);
        }

        var text = OperatorText(kind);
        if (text is null)
            return new TExpr(Unsupported($"operator {kind}", b), resultType);

        Expr left, right;
        if (b.IsLifted)
        {
            left = ExprAs(b.LeftOperand, Map(b.LeftOperand.Type!, b.Syntax));
            right = ExprAs(b.RightOperand, Map(b.RightOperand.Type!, b.Syntax));
        }
        else
        {
            left = Expr(b.LeftOperand);
            right = Expr(b.RightOperand);
        }
        NoteEquality(b.LeftOperand.Type, text);
        return new TExpr(new BinaryExpr(text, left, right), text is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" ? Types.Bool : resultType);
    }

    private void NoteEquality(ITypeSymbol? t, string op)
    {
        if (op is "==" or "!=" && t is ITypeParameterSymbol tp) _symbols.NeedsEquality.Add(tp);
    }

    private Expr StringOperand(IOperation op)
    {
        var inner = op is IConversionOperation { IsImplicit: true } c ? c.Operand : op;
        return inner.Type?.SpecialType switch
        {
            SpecialType.System_String => Expr(inner),
            SpecialType.System_Char => new DisplayExpr("seq", [Expr(inner)]),
            _ => Unsupported("string concatenation with a non-string value (needs ToString)", op),
        };
    }

    private Expr NullTest(IOperation operand, bool isNull)
    {
        var x = Translate(operand is IConversionOperation { IsImplicit: true } c ? c.Operand : operand);
        if (TypeTranslator.IsOption(x.T, out _)) return new MemberSel(x.E, isNull ? "None?" : "Some?");
        if (x.T is NamedType { Name: var n } && n.EndsWith('?')) return new BinaryExpr(isNull ? "==" : "!=", x.E, new Lit("null"));
        _diags.Warn("comparison of a non-nullable value with null", operand.Syntax);
        return new Lit(isNull ? "false" : "true");
    }

    private TExpr Unary(IUnaryOperation u)
    {
        if (u.IsLifted) return new TExpr(Unsupported("lifted operator on nullable values", u), MapNonNull(u.Type!, u.Syntax));
        var type = MapNonNull(u.Type!, u.Syntax);
        return u.OperatorKind switch
        {
            UnaryOperatorKind.Not => new TExpr(Negate(Expr(u.Operand)), Types.Bool),
            UnaryOperatorKind.Minus => new TExpr(new UnaryExpr("-", Expr(u.Operand)), type),
            UnaryOperatorKind.Plus => new TExpr(Expr(u.Operand), type),
            _ => new TExpr(Unsupported($"operator {u.OperatorKind}", u), type),
        };
    }

    /// <summary>Logical negation, pushed through comparisons so lifted preconditions read naturally.</summary>
    public static Expr Negate(Expr e) => e switch
    {
        UnaryExpr { Op: "!" } u => u.Operand,
        Lit { Text: "true" } => new Lit("false"),
        Lit { Text: "false" } => new Lit("true"),
        BinaryExpr { Op: "==" } b => b with { Op = "!=" },
        BinaryExpr { Op: "!=" } b => b with { Op = "==" },
        BinaryExpr { Op: "<" } b => b with { Op = ">=" },
        BinaryExpr { Op: "<=" } b => b with { Op = ">" },
        BinaryExpr { Op: ">" } b => b with { Op = "<=" },
        BinaryExpr { Op: ">=" } b => b with { Op = "<" },
        BinaryExpr { Op: "in" } b => b with { Op = "!in" },
        BinaryExpr { Op: "!in" } b => b with { Op = "in" },
        BinaryExpr { Op: "||" } b => new BinaryExpr("&&", Negate(b.Left), Negate(b.Right)),
        BinaryExpr { Op: "&&" } b => new BinaryExpr("||", Negate(b.Left), Negate(b.Right)),
        MemberSel { Member: "Some?" } m => m with { Member = "None?" },
        MemberSel { Member: "None?" } m => m with { Member = "Some?" },
        _ => new UnaryExpr("!", e),
    };

    public static Expr And(Expr a, Expr b) =>
        a is Lit { Text: "true" } ? b : b is Lit { Text: "true" } ? a : new BinaryExpr("&&", a, b);

    private static IOperation StripImplicitConversion(IOperation op) =>
        op is IConversionOperation { IsImplicit: true } c ? StripImplicitConversion(c.Operand) : op;

    private TExpr Conditional(IConditionalOperation c)
    {
        var type = Map(c.Type!, c.Syntax);
        var cond = Expr(c.Condition);
        var whenTrue = StripImplicitConversion(c.WhenTrue);
        var whenFalse = StripImplicitConversion(c.WhenFalse!);
        if (whenTrue is IThrowOperation && whenFalse is IThrowOperation)
            return new TExpr(Unsupported("both branches throw", c), type);
        if (whenTrue is IThrowOperation)
            return new TExpr(new AssertExpr(Negate(cond), Branch(c.WhenFalse!, type)), type);
        if (whenFalse is IThrowOperation)
            return new TExpr(new AssertExpr(cond, Branch(c.WhenTrue, type)), type);
        return new TExpr(new IteExpr(cond, Branch(c.WhenTrue, type), Branch(c.WhenFalse!, type)), type);
    }

    /// <summary>Translate an expression evaluated only conditionally, warning if a call had to be hoisted out of it.</summary>
    private Expr Branch(IOperation op, DType type)
    {
        var before = _pending?.Count ?? 0;
        var e = ExprAs(op, type);
        if ((_pending?.Count ?? 0) > before)
            _diags.Warn("a method call inside a conditional branch was hoisted and is now evaluated unconditionally", op.Syntax);
        return e;
    }

    private TExpr Coalesce(ICoalesceOperation co)
    {
        var type = Map(co.Type!, co.Syntax);
        var value = Simple(Translate(co.Value));
        Expr present, isPresent;
        if (TypeTranslator.IsOption(value.T, out var inner))
        {
            isPresent = new MemberSel(value.E, "Some?");
            present = Coerce(new TExpr(new MemberSel(value.E, "value"), inner), type);
        }
        else
        {
            isPresent = new BinaryExpr("!=", value.E, new Lit("null"));
            present = Coerce(value, type);
        }
        if (StripImplicitConversion(co.WhenNull) is IThrowOperation)
            return new TExpr(new AssertExpr(isPresent, present), type);
        return new TExpr(new IteExpr(isPresent, present, Branch(co.WhenNull, type)), type);
    }

    #endregion

    #region Construction

    private TExpr ObjectCreation(IObjectCreationOperation oc)
    {
        var type = (INamedTypeSymbol)oc.Type!;
        var mapped = MapNonNull(type, oc.Syntax);

        if (_symbols.CtorOf(type) is { } ctor)
            return new TExpr(DatatypeConstruction(ctor, oc), mapped);

        if (TypeTranslator.CollectionKind(type) is { } kind)
            return new TExpr(CollectionCreation(kind, oc), mapped);

        if (_symbols.IsSourceType(type) && _types.IsDafnyReferenceType(type))
        {
            if (oc.Initializer is not null)
                return new TExpr(Unsupported("object initializer on a class", oc), mapped);
            var ctorSym = oc.Constructor!;
            var args = ctorSym.Parameters.Select(p => ExprAs(oc.Arguments.First(a => SymbolEqualityComparer.Default.Equals(a.Parameter, p)).Value, Map(p.Type, oc.Syntax))).ToList();
            var rhs = new NewObjectRhs(mapped, args, ConstructorName(ctorSym));
            return new TExpr(HoistRhs(rhs, mapped, oc), mapped);
        }
        return new TExpr(Unsupported($"construction of {type.ToDisplayString()}", oc), mapped);
    }

    /// <summary>Dafny constructors are distinguished by name; the first C# constructor is anonymous.</summary>
    public static string? ConstructorName(IMethodSymbol ctor)
    {
        var all = ctor.ContainingType.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).ToList();
        var index = all.FindIndex(c => SymbolEqualityComparer.Default.Equals(c, ctor.OriginalDefinition));
        return index <= 0 ? null : $"New{index + 1}";
    }

    private Expr DatatypeConstruction(CtorInfo ctor, IObjectCreationOperation oc)
    {
        var values = new Dictionary<string, Expr>();
        var primary = SymbolModel.PrimaryConstructor(ctor.Type!);
        if (oc.Constructor is { } c && c.Parameters.Length > 0)
        {
            if (!SymbolEqualityComparer.Default.Equals(c.OriginalDefinition, primary?.OriginalDefinition))
                return Unsupported("record constructed through a non-primary constructor", oc);
            foreach (var arg in oc.Arguments)
                values[SymbolModel.Ident(arg.Parameter!.Name)] = ExprAs(arg.Value, Map(arg.Parameter.Type, arg.Syntax));
        }
        if (oc.Initializer is { } init)
        {
            foreach (var i in init.Initializers)
            {
                if (i is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation pr } a)
                    values[SymbolModel.Ident(pr.Property.Name)] = ExprAs(a.Value, Map(pr.Property.Type, a.Syntax));
                else
                    return Unsupported("record initializer element", i);
            }
        }
        var args = new List<Expr>();
        foreach (var f in ctor.Fields)
        {
            if (values.TryGetValue(f.Name, out var v)) args.Add(v);
            else args.Add(DefaultForMissingField(f, oc));
        }
        return ctor.Fields.Count == 0 ? new Ident(ctor.Name) : new Apply(new Ident(ctor.Name), args);
    }

    private Expr DefaultForMissingField(DatatypeField f, IOperation site)
    {
        // A property initializer on the record supplies the value when the creation does not.
        if (f.Property?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax { Initializer: { } init })
        {
            var model = _symbols.ModelFor(init.SyntaxTree);
            if (model.GetOperation(init) is IPropertyInitializerOperation pio)
                return ExprAs(pio.Value, Map(f.Type, init));
        }
        return DefaultValue(f.Type, site);
    }

    private Expr CollectionCreation(string kind, IObjectCreationOperation oc)
    {
        var elems = new List<Expr>();
        var entries = new List<(Expr, Expr)>();
        if (oc.Arguments.Length == 1 && TypeTranslator.CollectionKind(oc.Arguments[0].Value.Type) is not null && oc.Initializer is null)
            return Expr(oc.Arguments[0].Value); // copy constructor
        if (oc.Initializer is { } init)
        {
            foreach (var i in init.Initializers)
            {
                switch (i)
                {
                    case IInvocationOperation { TargetMethod.Name: "Add", Arguments.Length: 1 } add:
                        elems.Add(Expr(add.Arguments[0].Value));
                        break;
                    case IInvocationOperation { TargetMethod.Name: "Add", Arguments.Length: 2 } add2:
                        entries.Add((Expr(add2.Arguments[0].Value), Expr(add2.Arguments[1].Value)));
                        break;
                    case ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true } idx } a:
                        entries.Add((Expr(idx.Arguments[0].Value), Expr(a.Value)));
                        break;
                    default:
                        return Unsupported("collection initializer element", i);
                }
            }
        }
        return kind == "map" ? new MapDisplayExpr(entries) : new DisplayExpr(kind, elems);
    }

    private TExpr With(IWithOperation w)
    {
        var type = MapNonNull(w.Type!, w.Syntax);
        if (_symbols.CtorOf(w.Operand.Type) is null)
            return new TExpr(Unsupported("'with' on a non-record type", w), type);
        var updates = new List<(string, Expr)>();
        foreach (var i in w.Initializer.Initializers)
        {
            if (i is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation pr } a)
                updates.Add((SymbolModel.Ident(pr.Property.Name), ExprAs(a.Value, Map(pr.Property.Type, a.Syntax))));
            else
                return new TExpr(Unsupported("'with' initializer element", i), type);
        }
        return new TExpr(new DatatypeUpdateExpr(ReceiverT(w.Operand).E, updates), type);
    }

    private TExpr ArrayCreation(IArrayCreationOperation ac)
    {
        var type = MapNonNull(ac.Type!, ac.Syntax);
        if (ac.DimensionSizes.Length != 1) return new TExpr(Unsupported("multi-dimensional array", ac), type);
        var elementType = ((IArrayTypeSymbol)ac.Type!).ElementType;
        if (ac.Initializer is { } init)
            return new TExpr(new DisplayExpr("seq", init.ElementValues.Select(Expr).ToList()), type);
        var size = Expr(ac.DimensionSizes[0]);
        var dflt = DefaultValue(elementType, ac);
        return new TExpr(new SeqConstructionExpr(size, new LambdaExpr([new BoundVar("_", null)], dflt)), type);
    }

    private TExpr CollectionExpression(ICollectionExpressionOperation ce)
    {
        var type = MapNonNull(ce.Type!, ce.Syntax);
        var kind = TypeTranslator.CollectionKind(ce.Type) ?? "seq";
        if (kind == "map") return new TExpr(Unsupported("collection expression for a dictionary", ce), type);
        Expr? result = null;
        var run = new List<Expr>();
        void Flush()
        {
            if (run.Count == 0) return;
            var d = new DisplayExpr(kind, run.ToList());
            result = result is null ? d : new BinaryExpr("+", result, d);
            run.Clear();
        }
        foreach (var e in ce.Elements)
        {
            if (e is ISpreadOperation spread)
            {
                Flush();
                var part = Expr(spread.Operand);
                result = result is null ? part : new BinaryExpr("+", result, part);
            }
            else run.Add(Expr(e));
        }
        Flush();
        return new TExpr(result ?? new DisplayExpr(kind, []), type);
    }

    #endregion

    #region Lambdas, strings, conditional access

    private TExpr Lambda(IAnonymousFunctionOperation lam)
    {
        var ps = lam.Symbol.Parameters.Select(p => new BoundVar(LocalName(p), Map(p.Type, lam.Syntax))).ToList();
        var type = new ArrowType(ps.Select(p => p.Type!).ToList(), Map(lam.Symbol.ReturnType, lam.Syntax));
        var saved = _pending;
        _pending = null; // lambda bodies are pure expressions
        try
        {
            var body = BodyToExpr(lam.Body.Operations, 0, Map(lam.Symbol.ReturnType, lam.Syntax), lam);
            return new TExpr(new LambdaExpr(ps, body ?? Unsupported("lambda body that always throws", lam)), type);
        }
        finally
        {
            _pending = saved;
        }
    }

    private TExpr InterpolatedString(IInterpolatedStringOperation s)
    {
        Expr? result = null;
        foreach (var part in s.Parts)
        {
            Expr piece;
            switch (part)
            {
                case IInterpolatedStringTextOperation text:
                    piece = new Lit(StringLiteral((string)text.Text.ConstantValue.Value!));
                    break;
                case IInterpolationOperation { Alignment: null, FormatString: null } hole when hole.Expression.Type?.SpecialType == SpecialType.System_String:
                    piece = Expr(hole.Expression);
                    break;
                case IInterpolationOperation { Alignment: null, FormatString: null } hole when hole.Expression.Type?.SpecialType == SpecialType.System_Char:
                    piece = new DisplayExpr("seq", [Expr(hole.Expression)]);
                    break;
                default:
                    piece = Unsupported("interpolated value that is not a string (needs ToString)", part);
                    break;
            }
            result = result is null ? piece : new BinaryExpr("+", result, piece);
        }
        return new TExpr(result ?? new Lit("\"\""), Types.String);
    }

    private TExpr ConditionalAccess(IConditionalAccessOperation ca)
    {
        var type = Map(ca.Type!, ca.Syntax);
        var receiver = Simple(Translate(ca.Operation));
        Expr isPresent;
        TExpr instance;
        if (TypeTranslator.IsOption(receiver.T, out var inner))
        {
            isPresent = new MemberSel(receiver.E, "Some?");
            instance = new TExpr(new MemberSel(receiver.E, "value"), inner);
        }
        else
        {
            isPresent = new BinaryExpr("!=", receiver.E, new Lit("null"));
            instance = receiver;
        }
        var saved = _implicitInstance;
        _implicitInstance = instance;
        try
        {
            var whenNotNull = Translate(ca.WhenNotNull);
            var absent = TypeTranslator.IsOption(type, out _) ? (Expr)new Ident("None") : new Lit("null");
            return new TExpr(new IteExpr(isPresent, Coerce(whenNotNull, type), absent), type);
        }
        finally
        {
            _implicitInstance = saved;
        }
    }

    #endregion
}
