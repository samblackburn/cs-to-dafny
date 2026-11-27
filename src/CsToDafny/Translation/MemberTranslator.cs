using CsToDafny.Dafny;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>A translated expression together with the Dafny type it naturally has.</summary>
public readonly record struct TExpr(Expr E, DType T);

/// <summary>
/// Translates the body of one C# member (method, property getter, constructor, lambda) from Roslyn's
/// IOperation tree into Dafny. Split across partial files: expressions, patterns, statements, function bodies.
/// </summary>
public sealed partial class MemberTranslator
{
    private readonly SymbolModel _symbols;
    private readonly TypeTranslator _types;
    private readonly Diagnostics _diags;
    private readonly ConverterOptions _options;

    /// <summary>The type whose member is being translated.</summary>
    private readonly INamedTypeSymbol _containingType;

    /// <summary>Pattern designations that are just another name for an existing value (e.g. "s is Circle c").</summary>
    private readonly Dictionary<ISymbol, Expr> _aliases = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Statements that must run before the expression currently being translated: Dafny forbids method calls
    /// and object allocation inside expressions, so they are hoisted into temporaries. Null in function context.
    /// </summary>
    private List<Stmt>? _pending;

    private int _tempCounter;

    /// <summary>Set when a method body assigns to a field of "this".</summary>
    public bool ModifiesThis { get; private set; }

    public MemberTranslator(SymbolModel symbols, TypeTranslator types, Diagnostics diags, ConverterOptions options, INamedTypeSymbol containingType)
    {
        _symbols = symbols;
        _types = types;
        _diags = diags;
        _options = options;
        _containingType = containingType;
    }

    private static readonly NamedType NumLit = new("#numeric-literal");
    private static readonly NamedType NullLitType = new("#null");

    private string FreshTemp() => $"tmp{_tempCounter++}";

    private DType Map(ITypeSymbol t, SyntaxNode? site) => _types.Map(t, site);

    /// <summary>Map a type, treating reference types as non-null (used where the type came from flow analysis).</summary>
    private DType MapNonNull(ITypeSymbol t, SyntaxNode? site) => _types.Map(t, nullable: false, site);

    private static string LocalName(ISymbol s) => SymbolModel.Ident(s.Name);

    private readonly Dictionary<ILocalSymbol, DType> _localTypes = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// The Dafny type of a local. C# declares every implicitly typed ("var") reference local as nullable, so for
    /// those the nullability comes from the flow state of the initializer instead.
    /// </summary>
    private DType LocalType(ILocalSymbol local)
    {
        if (_localTypes.TryGetValue(local, out var cached)) return cached;
        var syntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var nullable = local.NullableAnnotation == NullableAnnotation.Annotated;
        if (nullable && local.Type.IsReferenceType && syntax is not null)
        {
            var model = _symbols.ModelFor(syntax.SyntaxTree);
            switch (syntax)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                    { Parent: Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax { Type.IsVar: true }, Initializer: { } init }:
                    nullable = model.GetTypeInfo(init.Value).Nullability.FlowState == NullableFlowState.MaybeNull;
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.ForEachStatementSyntax { Type.IsVar: true } fe:
                    nullable = model.GetForEachStatementInfo(fe).ElementType?.NullableAnnotation == NullableAnnotation.Annotated;
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.SingleVariableDesignationSyntax:
                    nullable = false; // pattern designations are never null
                    break;
            }
        }
        return _localTypes[local] = _types.Map(local.Type, nullable, syntax);
    }

    private Expr Unsupported(string what, IOperation op)
    {
        _diags.Unsupported(what, op.Syntax);
        return new Opaque(op.Syntax.ToString());
    }

    #region Coercion

    /// <summary>
    /// Insert the conversion needed to use <paramref name="x"/> where <paramref name="expected"/> is required:
    /// numeric "as" conversions, Option wrapping (Some/None) and Option unwrapping (.value, which makes
    /// Dafny prove the value is not null).
    /// </summary>
    private Expr Coerce(TExpr x, DType expected)
    {
        if (TypeTranslator.TypeEquals(x.T, expected)) return x.E;
        if (x.T == NullLitType)
            return TypeTranslator.IsOption(expected, out _) ? new Ident("None") : new Lit("null");
        var expIsOpt = TypeTranslator.IsOption(expected, out var expInner);
        var actIsOpt = TypeTranslator.IsOption(x.T, out var actInner);
        if (expIsOpt && !actIsOpt) return new Apply(new Ident("Some"), [Coerce(x, expInner)]);
        if (actIsOpt && !expIsOpt) return Coerce(new TExpr(new MemberSel(x.E, "value"), actInner), expected);
        if (actIsOpt && expIsOpt) return x.E;

        var numericSource = x.T == NumLit || TypeTranslator.IsNumeric(x.T) || x.T is NamedType { Name: "char" };
        var numericTarget = TypeTranslator.IsNumeric(expected) || expected is NamedType { Name: "char" };
        if (numericSource && numericTarget)
        {
            if (x.E is Lit lit && IsIntegerLiteral(lit.Text))
                return expected is NamedType { Name: "real" } ? new Lit(lit.Text + ".0")
                    : expected is NamedType { Name: "char" } ? new ConversionExpr(x.E, expected)
                    : x.E;
            if (x.E is UnaryExpr { Op: "-", Operand: Lit l2 } && IsIntegerLiteral(l2.Text) && expected is NamedType { Name: not "real" and not "char" })
                return x.E;
            if (x.T == NumLit) return x.E;
            return new ConversionExpr(x.E, expected);
        }
        return x.E;
    }

    private static bool IsIntegerLiteral(string text) => text.Length > 0 && (char.IsDigit(text[0])) && !text.Contains('.');

    /// <summary>Translate an operation and coerce it to the Dafny image of its C# type (non-null for references).</summary>
    private Expr Expr(IOperation op) => Coerce(Translate(op), ExpectedFor(op));

    private Expr ExprAs(IOperation op, DType expected) => Coerce(Translate(op), expected);

    /// <summary>Translate an expression that must be pure (field initializers, constants).</summary>
    public Expr PureExpr(IOperation op, DType expected)
    {
        _pending = null;
        return ExprAs(op, expected);
    }

    /// <summary>The Dafny type an operation's value is used at: nullable only if its C# type says so.</summary>
    private DType ExpectedFor(IOperation op)
    {
        // Roslyn annotates expression types with their flow state: a reference known to be non-null here
        // is NotAnnotated, so an Option-typed variable used after a null check is unwrapped with .value.
        return op.Type is null ? NumLit : Map(op.Type, op.Syntax);
    }

    #endregion
}
