namespace CsToDafny.Dafny;

// A deliberately small Dafny AST: just enough structure to support the
// clean-up passes and precedence-aware printing. Anything the converter
// cannot express is carried as an Opaque node so output is never silently lost.

#region Types

public abstract record DType;

/// <summary>A named type, possibly generic: int, seq&lt;T&gt;, M.Tree&lt;A&gt;, array2&lt;T&gt; ...</summary>
public sealed record NamedType(string Name, IReadOnlyList<DType> Args) : DType
{
    public NamedType(string name) : this(name, []) { }
}

public sealed record TupleType(IReadOnlyList<DType> Elements) : DType;

public sealed record ArrowType(IReadOnlyList<DType> Params, DType Result) : DType;

public static class Types
{
    public static readonly NamedType Int = new("int");
    public static readonly NamedType Bool = new("bool");
    public static readonly NamedType Char = new("char");
    public static readonly NamedType Real = new("real");
    public static readonly NamedType String = new("string");
    public static readonly NamedType Object = new("object");
}

#endregion

#region Expressions

public abstract record Expr;

/// <summary>Literal text emitted verbatim (numbers, true/false, null, escaped strings and chars, this).</summary>
public sealed record Lit(string Text) : Expr;

public sealed record Ident(string Name) : Expr;

/// <summary>Placeholder for C# that could not be translated. Printed as a comment plus a hole.</summary>
public sealed record Opaque(string CSharp) : Expr;

public sealed record MemberSel(Expr Target, string Member) : Expr;

public sealed record Apply(Expr Callee, IReadOnlyList<Expr> Args) : Expr;

public sealed record IndexExpr(Expr Target, IReadOnlyList<Expr> Indices) : Expr;

public sealed record SliceExpr(Expr Target, Expr? Lo, Expr? Hi) : Expr;

public sealed record SeqUpdateExpr(Expr Target, Expr Index, Expr Value) : Expr;

public sealed record DatatypeUpdateExpr(Expr Target, IReadOnlyList<(string Field, Expr Value)> Updates) : Expr;

public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

public sealed record UnaryExpr(string Op, Expr Operand) : Expr;

public sealed record CardinalityExpr(Expr Operand) : Expr;

/// <summary>Kind is one of "seq", "set", "multiset".</summary>
public sealed record DisplayExpr(string Kind, IReadOnlyList<Expr> Elements) : Expr;

public sealed record MapDisplayExpr(IReadOnlyList<(Expr Key, Expr Value)> Entries) : Expr;

public sealed record TupleExpr(IReadOnlyList<Expr> Elements) : Expr;

public sealed record IteExpr(Expr Cond, Expr Then, Expr Else) : Expr;

public sealed record LetExpr(IReadOnlyList<string> Names, IReadOnlyList<Expr> Rhss, Expr Body) : Expr;

public sealed record BoundVar(string Name, DType? Type);

public sealed record LambdaExpr(IReadOnlyList<BoundVar> Params, Expr Body) : Expr;

public sealed record QuantifierExpr(bool IsForall, IReadOnlyList<BoundVar> Vars, Expr Body) : Expr;

/// <summary>set x | range :: term, map x | range :: key := value (Key null =&gt; map x | r :: value).</summary>
public sealed record ComprehensionExpr(string Kind, IReadOnlyList<BoundVar> Vars, Expr Range, Expr? Key, Expr? Term) : Expr;

public sealed record SeqConstructionExpr(Expr Length, Expr Init) : Expr;

public sealed record MatchExpr(Expr Source, IReadOnlyList<MatchCaseExpr> Cases) : Expr;

public sealed record MatchCaseExpr(Pattern Pattern, Expr Body);

public sealed record ConversionExpr(Expr Operand, DType To) : Expr;

/// <summary>assert Cond; Body</summary>
public sealed record AssertExpr(Expr Cond, Expr Body) : Expr;

/// <summary>Multiset conversion: multiset(s).</summary>
public sealed record MultisetOfExpr(Expr Operand) : Expr;

#endregion

#region Patterns

public abstract record Pattern;

public sealed record WildPattern : Pattern;

public sealed record VarPattern(string Name) : Pattern;

public sealed record CtorPattern(string Ctor, IReadOnlyList<Pattern> Args, bool IsTuple = false) : Pattern;

public sealed record LitPattern(Expr Value) : Pattern;

#endregion

#region Statements and right-hand sides

public abstract record Rhs;

public sealed record ExprRhs(Expr Expr) : Rhs;

public sealed record NewObjectRhs(DType Type, IReadOnlyList<Expr>? CtorArgs, string? CtorName = null) : Rhs;

public sealed record NewArrayRhs(DType ElementType, IReadOnlyList<Expr> Dims, Expr? Init, IReadOnlyList<Expr>? Elements = null) : Rhs;

public abstract record Stmt;

public sealed record VarDeclStmt(IReadOnlyList<BoundVar> Vars, IReadOnlyList<Rhs>? Rhss) : Stmt;

public sealed record AssignStmt(IReadOnlyList<Expr> Lhss, IReadOnlyList<Rhs> Rhss) : Stmt;

public sealed record CallStmt(Expr Call) : Stmt;

public sealed record BlockStmt(IReadOnlyList<Stmt> Body) : Stmt;

public sealed record IfStmt(Expr? Cond, BlockStmt Then, Stmt? Else) : Stmt;

public sealed record WhileStmt(Expr? Cond, BlockStmt Body, string? Label = null) : Stmt;

public sealed record ForStmt(string Var, Expr Start, Expr End, bool Down, BlockStmt Body, string? Label = null) : Stmt;

public sealed record ReturnStmt(IReadOnlyList<Expr> Values) : Stmt;

/// <summary>Label null =&gt; innermost loop. Count &gt; 1 =&gt; "break break".</summary>
public sealed record BreakStmt(string? Label, int Count = 1, bool IsContinue = false) : Stmt;

public sealed record PrintStmt(IReadOnlyList<Expr> Args) : Stmt;

public sealed record ExpectStmt(Expr Cond, Expr? Message) : Stmt;

public sealed record MatchStmt(Expr Source, IReadOnlyList<MatchCaseStmt> Cases) : Stmt;

public sealed record MatchCaseStmt(Pattern Pattern, IReadOnlyList<Stmt> Body);

public sealed record CommentStmt(string Text) : Stmt;

public sealed record OpaqueStmt(string CSharp) : Stmt;

/// <summary>var x :| x in s;  (choose any element)</summary>
public sealed record AssignSuchThatStmt(string Var, Expr Set) : Stmt;

/// <summary>"new;": ends the first phase of a constructor, after which "this" may be used freely.</summary>
public sealed record NewDividerStmt : Stmt;

public sealed record AssertStmt(Expr Cond, string? Comment = null) : Stmt;

#endregion

#region Declarations

public abstract record Decl
{
    public string? Comment { get; init; }
}

public sealed record Formal(string Name, DType Type);

public sealed record ModuleDecl(string Name, IReadOnlyList<string> Imports, IReadOnlyList<Decl> Members) : Decl;

public sealed record FunctionDecl(
    string Name,
    bool IsStatic,
    bool IsPredicate,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<Formal> Params,
    DType ResultType,
    Expr? Body) : Decl
{
    public IReadOnlyList<Expr> Requires { get; init; } = [];
    public IReadOnlyList<Expr> Ensures { get; init; } = [];
    public IReadOnlyList<string> Reads { get; init; } = [];
    public string? Attributes { get; init; }
}

public sealed record MethodDecl(
    string Name,
    bool IsStatic,
    bool IsConstructor,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<Formal> Params,
    IReadOnlyList<Formal> Returns,
    IReadOnlyList<Stmt>? Body) : Decl
{
    public IReadOnlyList<Expr> Requires { get; init; } = [];
    public IReadOnlyList<Expr> Ensures { get; init; } = [];
    public IReadOnlyList<string> Modifies { get; init; } = [];
    public string? Attributes { get; init; }
}

public sealed record FieldDecl(string Name, DType Type, bool IsConst, bool IsStatic, Expr? Init) : Decl;

public sealed record DatatypeCtor(string Name, IReadOnlyList<Formal> Fields);

public sealed record DatatypeDecl(string Name, IReadOnlyList<string> TypeParams, IReadOnlyList<DatatypeCtor> Ctors, IReadOnlyList<Decl> Members) : Decl;

public sealed record ClassDecl(string Name, IReadOnlyList<string> TypeParams, IReadOnlyList<DType> Extends, IReadOnlyList<Decl> Members, bool IsTrait = false) : Decl;

/// <summary>newtype N = x: Base | Constraint, or (IsSubset) type N = x: Base | Constraint witness W.</summary>
public sealed record NewtypeDecl(string Name, string Var, DType Base, Expr? Constraint, Expr? Witness, bool IsSubset) : Decl;

/// <summary>An abstract type: type Name&lt;T&gt;, used for types the converter has no source for.</summary>
public sealed record OpaqueTypeDecl(string Name, IReadOnlyList<string> TypeParams) : Decl;

/// <summary>Verbatim Dafny text (the prelude).</summary>
public sealed record RawDecl(string Text) : Decl;

#endregion
