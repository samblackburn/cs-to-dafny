using System.Text;

namespace CsToDafny.Dafny;

/// <summary>Pretty-prints the Dafny AST with minimal, precedence-correct parentheses.</summary>
public sealed class Printer
{
    private readonly StringBuilder _sb = new();
    private int _indent;
    private const string IndentUnit = "  ";

    public static string Print(IEnumerable<Decl> decls)
    {
        var p = new Printer();
        var first = true;
        foreach (var d in decls)
        {
            if (!first) p._sb.AppendLine();
            first = false;
            p.PrintDecl(d);
        }
        return p._sb.ToString();
    }

    public static string PrintExpr(Expr e) => ExprToString(e, 0);

    #region Declarations

    private void Line(string s) => _sb.Append(string.Concat(Enumerable.Repeat(IndentUnit, _indent))).AppendLine(s);

    private void PrintDecl(Decl d)
    {
        if (d.Comment is { } c)
            foreach (var l in c.Split('\n'))
                Line("// " + l.TrimEnd('\r'));
        switch (d)
        {
            case ModuleDecl m:
                Line($"module {m.Name} {{");
                _indent++;
                foreach (var imp in m.Imports) Line("import " + imp);
                if (m.Imports.Count > 0 && m.Members.Count > 0) _sb.AppendLine();
                PrintMembers(m.Members);
                _indent--;
                Line("}");
                break;
            case FunctionDecl f:
                PrintFunction(f);
                break;
            case MethodDecl m:
                PrintMethod(m);
                break;
            case FieldDecl f:
            {
                var kw = (f.IsStatic ? "static " : "") + (f.IsConst ? "const " : "var ");
                var init = f.Init is null ? "" : " := " + ExprToString(f.Init, 0);
                Line($"{kw}{f.Name}: {TypeToString(f.Type)}{init}");
                break;
            }
            case DatatypeDecl dt:
            {
                var ctors = string.Join(" | ", dt.Ctors.Select(CtorToString));
                var head = $"datatype {dt.Name}{TypeParams(dt.TypeParams)} = {ctors}";
                if (dt.Members.Count == 0)
                {
                    Line(head);
                }
                else
                {
                    Line(head + " {");
                    _indent++;
                    PrintMembers(dt.Members);
                    _indent--;
                    Line("}");
                }
                break;
            }
            case ClassDecl cl:
            {
                var kw = cl.IsTrait ? "trait" : "class";
                var ext = cl.Extends.Count == 0 ? "" : " extends " + string.Join(", ", cl.Extends.Select(TypeToString));
                Line($"{kw} {cl.Name}{TypeParams(cl.TypeParams)}{ext} {{");
                _indent++;
                PrintMembers(cl.Members);
                _indent--;
                Line("}");
                break;
            }
            case NewtypeDecl nt:
            {
                var kw = nt.IsSubset ? "type" : "newtype";
                var constraint = nt.Constraint is null ? "true" : ExprToString(nt.Constraint, 0);
                var witness = nt.Witness is null ? "" : " witness " + ExprToString(nt.Witness, 0);
                Line($"{kw} {nt.Name} = {nt.Var}: {TypeToString(nt.Base)} | {constraint}{witness}");
                break;
            }
            case OpaqueTypeDecl ot:
                Line($"type {ot.Name}{TypeParams(ot.TypeParams)}");
                break;
            case RawDecl raw:
                foreach (var l in raw.Text.TrimEnd().Split('\n')) Line(l.TrimEnd('\r'));
                break;
            default:
                throw new InvalidOperationException($"Unknown decl {d.GetType().Name}");
        }
    }

    private void PrintMembers(IReadOnlyList<Decl> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            // Blank line between members, except between consecutive fields.
            if (i > 0 && !(members[i] is FieldDecl && members[i - 1] is FieldDecl)) _sb.AppendLine();
            PrintDecl(members[i]);
        }
    }

    private static string CtorToString(DatatypeCtor c) =>
        c.Fields.Count == 0 ? c.Name : $"{c.Name}({string.Join(", ", c.Fields.Select(FormalToString))})";

    private static string FormalToString(Formal f) => $"{f.Name}: {TypeToString(f.Type)}";

    private static string TypeParams(IReadOnlyList<string> tps) => tps.Count == 0 ? "" : $"<{string.Join(", ", tps)}>";

    private void PrintFunction(FunctionDecl f)
    {
        var kw = (f.IsStatic ? "static " : "") + (f.IsPredicate ? "predicate" : "function");
        var result = f.IsPredicate ? "" : ": " + TypeToString(f.ResultType);
        var attrs = f.Attributes is null ? "" : f.Attributes + " ";
        Line($"{kw} {attrs}{f.Name}{TypeParams(f.TypeParams)}({string.Join(", ", f.Params.Select(FormalToString))}){result}");
        PrintSpecs(f.Requires, f.Reads.Count == 0 ? [] : [("reads", string.Join(", ", f.Reads))], f.Ensures);
        if (f.Body is null) return;
        Line("{");
        _indent++;
        foreach (var l in ExprToString(f.Body, 0).Split('\n')) Line(l);
        _indent--;
        Line("}");
    }

    private void PrintMethod(MethodDecl m)
    {
        var kw = m.IsConstructor ? "constructor" : (m.IsStatic ? "static method" : "method");
        var name = m.IsConstructor ? (m.Name.Length > 0 ? " " + m.Name : "") : " " + m.Name;
        var attrs = m.Attributes is null ? "" : " " + m.Attributes;
        var head = $"{kw}{attrs}{name}{TypeParams(m.TypeParams)}({string.Join(", ", m.Params.Select(FormalToString))})";
        if (m.Returns.Count > 0)
            head += $" returns ({string.Join(", ", m.Returns.Select(FormalToString))})";
        Line(head);
        PrintSpecs(m.Requires, m.Modifies.Count == 0 ? [] : [("modifies", string.Join(", ", m.Modifies))], m.Ensures);
        if (m.Body is null) return;
        Line("{");
        _indent++;
        foreach (var s in m.Body) PrintStmt(s);
        _indent--;
        Line("}");
    }

    private void PrintSpecs(IReadOnlyList<Expr> requires, IReadOnlyList<(string Kw, string Text)> frames, IReadOnlyList<Expr> ensures)
    {
        _indent++;
        foreach (var r in requires) Line("requires " + ExprToString(r, 0).Replace("\n", " "));
        foreach (var (kw, text) in frames) Line($"{kw} {text}");
        foreach (var e in ensures) Line("ensures " + ExprToString(e, 0).Replace("\n", " "));
        _indent--;
    }

    #endregion

    #region Statements

    private void PrintBlockBody(BlockStmt b)
    {
        _indent++;
        foreach (var s in b.Body) PrintStmt(s);
        _indent--;
    }

    private void PrintStmt(Stmt s)
    {
        switch (s)
        {
            case VarDeclStmt v:
            {
                var vars = string.Join(", ", v.Vars.Select(bv => bv.Type is null ? bv.Name : $"{bv.Name}: {TypeToString(bv.Type)}"));
                Line(v.Rhss is null ? $"var {vars};" : $"var {vars} := {string.Join(", ", v.Rhss.Select(RhsToString))};");
                break;
            }
            case AssignStmt a:
                Line($"{string.Join(", ", a.Lhss.Select(l => ExprToString(l, 0)))} := {string.Join(", ", a.Rhss.Select(RhsToString))};");
                break;
            case CallStmt c:
                Line(ExprToString(c.Call, 0) + ";");
                break;
            case BlockStmt b:
                Line("{");
                PrintBlockBody(b);
                Line("}");
                break;
            case IfStmt i:
                PrintIf(i, "if");
                break;
            case WhileStmt w:
            {
                var label = w.Label is null ? "" : $"label {w.Label}: ";
                Line($"{label}while {(w.Cond is null ? "true" : ExprToString(w.Cond, 0))} {{");
                PrintBlockBody(w.Body);
                Line("}");
                break;
            }
            case ForStmt f:
            {
                var label = f.Label is null ? "" : $"label {f.Label}: ";
                Line($"{label}for {f.Var} := {ExprToString(f.Start, 0)} {(f.Down ? "downto" : "to")} {ExprToString(f.End, 0)} {{");
                PrintBlockBody(f.Body);
                Line("}");
                break;
            }
            case ReturnStmt r:
                Line(r.Values.Count == 0 ? "return;" : $"return {string.Join(", ", r.Values.Select(e => ExprToString(e, 0)))};");
                break;
            case BreakStmt b:
            {
                var kw = b.IsContinue ? "continue" : "break";
                if (b.Label is not null) Line($"{kw} {b.Label};");
                else Line(string.Join(" ", Enumerable.Repeat("break", b.Count - 1).Append(kw)) + ";");
                break;
            }
            case PrintStmt p:
                Line($"print {string.Join(", ", p.Args.Select(e => ExprToString(e, 0)))};");
                break;
            case ExpectStmt e:
                Line(e.Message is null ? $"expect {ExprToString(e.Cond, 0)};" : $"expect {ExprToString(e.Cond, 0)}, {ExprToString(e.Message, 0)};");
                break;
            case MatchStmt m:
                Line($"match {ExprToString(m.Source, 0)} {{");
                _indent++;
                foreach (var c in m.Cases)
                {
                    Line($"case {PatternToString(c.Pattern)} =>");
                    _indent++;
                    foreach (var st in c.Body) PrintStmt(st);
                    _indent--;
                }
                _indent--;
                Line("}");
                break;
            case CommentStmt c:
                foreach (var l in c.Text.Split('\n')) Line("// " + l.TrimEnd('\r'));
                break;
            case AssignSuchThatStmt st:
                Line($"var {st.Var} :| {st.Var} in {ExprToString(st.Set, PRel + 1)};");
                break;
            case AssertStmt a:
                Line($"assert {ExprToString(a.Cond, 0)};" + (a.Comment is null ? "" : " // " + a.Comment));
                break;
            case OpaqueStmt o:
                Line($"UNTRANSLATED(/* {SanitizeComment(o.CSharp)} */);");
                break;
            default:
                throw new InvalidOperationException($"Unknown stmt {s.GetType().Name}");
        }
    }

    private void PrintIf(IfStmt i, string kw)
    {
        Line($"{kw} {(i.Cond is null ? "*" : ExprToString(i.Cond, 0))} {{");
        PrintBlockBody(i.Then);
        switch (i.Else)
        {
            case null:
                Line("}");
                break;
            case IfStmt elseIf:
                // Print "} else if c {" chains on one line.
                _sb.Append(string.Concat(Enumerable.Repeat(IndentUnit, _indent))).Append("} ");
                var saved = _indent;
                _indent = 0;
                var start = _sb.Length;
                PrintIf(elseIf, "else if");
                _indent = saved;
                // Re-indent the lines printed with zero indent (all but the first).
                var printed = _sb.ToString(start, _sb.Length - start);
                _sb.Length = start;
                var lines = printed.Split('\n');
                _sb.Append(lines[0]).Append('\n');
                for (var k = 1; k < lines.Length; k++)
                    if (lines[k].Length > 0) _sb.Append(string.Concat(Enumerable.Repeat(IndentUnit, _indent))).Append(lines[k]).Append('\n');
                break;
            case BlockStmt b:
                Line("} else {");
                PrintBlockBody(b);
                Line("}");
                break;
            default:
                Line("} else {");
                _indent++;
                PrintStmt(i.Else);
                _indent--;
                Line("}");
                break;
        }
    }

    private static string RhsToString(Rhs r) => r switch
    {
        ExprRhs e => ExprToString(e.Expr, 0),
        NewObjectRhs n => n.CtorArgs is null
            ? $"new {TypeToString(n.Type)}"
            : $"new {TypeToString(n.Type)}{(n.CtorName is null ? "" : "." + n.CtorName)}({string.Join(", ", n.CtorArgs.Select(a => ExprToString(a, 0)))})",
        NewArrayRhs a when a.Elements is not null => $"new {TypeToString(a.ElementType)}[] [{string.Join(", ", a.Elements.Select(x => ExprToString(x, 0)))}]",
        NewArrayRhs a => $"new {TypeToString(a.ElementType)}[{string.Join(", ", a.Dims.Select(x => ExprToString(x, 0)))}]" +
                         (a.Init is null ? "" : $"({ExprToString(a.Init, 0)})"),
        _ => throw new InvalidOperationException()
    };

    #endregion

    #region Types

    public static string TypeToString(DType t) => t switch
    {
        NamedType n when n.Args.Count == 0 => n.Name,
        NamedType n => $"{n.Name}<{string.Join(", ", n.Args.Select(TypeToString))}>",
        TupleType tt => $"({string.Join(", ", tt.Elements.Select(TypeToString))})",
        ArrowType { Params.Count: 1 } a when a.Params[0] is not TupleType and not ArrowType =>
            $"{TypeToString(a.Params[0])} -> {TypeToString(a.Result)}",
        ArrowType a => $"({string.Join(", ", a.Params.Select(TypeToString))}) -> {TypeToString(a.Result)}",
        _ => throw new InvalidOperationException()
    };

    #endregion

    #region Expressions

    // Binding strength; larger binds tighter.
    private const int PLow = 0;       // lambda, quantifier, let, if-then-else, match
    private const int PEquiv = 1;     // <==>
    private const int PImplies = 2;   // ==> <==
    private const int PLogic = 3;     // && ||
    private const int PRel = 4;       // == != < <= > >= in !in !!
    private const int PShift = 5;     // << >>
    private const int PAdd = 6;       // + -
    private const int PMul = 7;       // * / %
    private const int PBit = 8;       // & | ^
    private const int PAs = 9;        // as
    private const int PUnary = 10;    // - !
    private const int PPrimary = 11;

    private static int BinaryPrec(string op) => op switch
    {
        "<==>" => PEquiv,
        "==>" or "<==" => PImplies,
        "&&" or "||" => PLogic,
        "==" or "!=" or "<" or "<=" or ">" or ">=" or "in" or "!in" or "!!" => PRel,
        "<<" or ">>" => PShift,
        "+" or "-" => PAdd,
        "*" or "/" or "%" => PMul,
        "&" or "|" or "^" => PBit,
        _ => throw new InvalidOperationException("Unknown operator " + op)
    };

    private static int Prec(Expr e) => e switch
    {
        BinaryExpr b => BinaryPrec(b.Op),
        UnaryExpr => PUnary,
        ConversionExpr => PAs,
        LambdaExpr or QuantifierExpr or LetExpr or IteExpr or MatchExpr or ComprehensionExpr or AssertExpr => PLow,
        Lit l when l.Text.StartsWith('-') => PUnary,
        _ => PPrimary
    };

    private static string ExprToString(Expr e, int ctx)
    {
        var s = Raw(e);
        return Prec(e) < ctx ? $"({s})" : s;
    }

    private static string Args(IEnumerable<Expr> args) => string.Join(", ", args.Select(a => ExprToString(a, 0)));

    private static string Raw(Expr e)
    {
        switch (e)
        {
            case Lit l: return l.Text;
            case Ident i: return i.Name;
            case Opaque o: return $"UNTRANSLATED(/* {SanitizeComment(o.CSharp)} */)";
            case MemberSel m: return $"{ExprToString(m.Target, PPrimary)}.{m.Member}";
            case Apply a: return $"{ExprToString(a.Callee, PPrimary)}({Args(a.Args)})";
            case IndexExpr ix: return $"{ExprToString(ix.Target, PPrimary)}[{Args(ix.Indices)}]";
            case SliceExpr sl:
                return $"{ExprToString(sl.Target, PPrimary)}[{(sl.Lo is null ? "" : ExprToString(sl.Lo, 0))}..{(sl.Hi is null ? "" : ExprToString(sl.Hi, 0))}]";
            case SeqUpdateExpr su:
                return $"{ExprToString(su.Target, PPrimary)}[{ExprToString(su.Index, 0)} := {ExprToString(su.Value, 0)}]";
            case DatatypeUpdateExpr du:
                return $"{ExprToString(du.Target, PPrimary)}.({string.Join(", ", du.Updates.Select(u => $"{u.Field} := {ExprToString(u.Value, 0)}"))})";
            case BinaryExpr b: return BinaryToString(b);
            case UnaryExpr u:
            {
                var operand = ExprToString(u.Operand, PUnary);
                // Avoid "--x" or "- -1" being read as something else.
                return u.Op == "-" && operand.StartsWith('-') ? $"-({operand})" : u.Op + operand;
            }
            case CardinalityExpr c: return $"|{ExprToString(c.Operand, 0)}|";
            case DisplayExpr d:
                return d.Kind switch
                {
                    "seq" => $"[{Args(d.Elements)}]",
                    "set" => $"{{{Args(d.Elements)}}}",
                    "multiset" => $"multiset{{{Args(d.Elements)}}}",
                    _ => throw new InvalidOperationException()
                };
            case MapDisplayExpr md:
                return $"map[{string.Join(", ", md.Entries.Select(kv => $"{ExprToString(kv.Key, 0)} := {ExprToString(kv.Value, 0)}"))}]";
            case TupleExpr t: return $"({Args(t.Elements)})";
            case IteExpr ite:
            {
                var head = $"if {ExprToString(ite.Cond, 0)} then {ExprToString(ite.Then, PEquiv)}";
                var els = ExprToString(ite.Else, 0);
                // else-if chains and long conditionals read better one branch per line.
                var multiline = ite.Else is IteExpr || head.Length + els.Length > 90 || head.Contains('\n') || els.Contains('\n');
                return multiline ? $"{head}\nelse {els}" : $"{head} else {els}";
            }
            case AssertExpr ae:
                return $"assert {ExprToString(ae.Cond, 0)};\n{ExprToString(ae.Body, 0)}";
            case LetExpr let:
                return $"var {string.Join(", ", let.Names)} := {Args(let.Rhss)};\n{ExprToString(let.Body, 0)}";
            case LambdaExpr lam:
            {
                var ps = lam.Params.Count == 1 && lam.Params[0].Type is null
                    ? lam.Params[0].Name
                    : $"({string.Join(", ", lam.Params.Select(BoundVarToString))})";
                return $"{ps} => {ExprToString(lam.Body, 0)}";
            }
            case QuantifierExpr q:
                return $"{(q.IsForall ? "forall" : "exists")} {string.Join(", ", q.Vars.Select(BoundVarToString))} :: {ExprToString(q.Body, 0)}";
            case ComprehensionExpr c:
            {
                var head = $"{c.Kind} {string.Join(", ", c.Vars.Select(BoundVarToString))} | {ExprToString(c.Range, 0)}";
                if (c.Term is null) return head;
                var term = c.Key is null ? ExprToString(c.Term, 0) : $"{ExprToString(c.Key, 0)} := {ExprToString(c.Term, 0)}";
                return $"{head} :: {term}";
            }
            case SeqConstructionExpr sc: return $"seq({ExprToString(sc.Length, 0)}, {ExprToString(sc.Init, 0)})";
            case MatchExpr m:
            {
                var sb = new StringBuilder($"match {ExprToString(m.Source, 0)}");
                foreach (var c in m.Cases)
                {
                    var body = ExprToString(c.Body, 0).Replace("\n", "\n  ");
                    sb.Append($"\ncase {PatternToString(c.Pattern)} =>\n  {body}");
                }
                return sb.ToString();
            }
            case ConversionExpr cv: return $"{ExprToString(cv.Operand, PAs + 1)} as {TypeToString(cv.To)}";
            case MultisetOfExpr ms: return $"multiset({ExprToString(ms.Operand, 0)})";
            default: throw new InvalidOperationException($"Unknown expr {e.GetType().Name}");
        }
    }

    private static string BinaryToString(BinaryExpr b)
    {
        var p = BinaryPrec(b.Op);
        string left, right;
        switch (p)
        {
            case PImplies when b.Op == "==>":
                left = ExprToString(b.Left, p + 1);
                right = ExprToString(b.Right, p);
                break;
            case PLogic:
            case PBit:
                // Same operator associates; mixing && with || (or & with |) needs parentheses.
                left = SameOrTighter(b.Left, b.Op, p);
                right = SameOrTighter(b.Right, b.Op, p);
                break;
            case PRel:
                left = ExprToString(b.Left, p + 1);
                right = ExprToString(b.Right, p + 1);
                break;
            default:
                left = ExprToString(b.Left, p);
                right = ExprToString(b.Right, p + 1);
                break;
        }
        return $"{left} {b.Op} {right}";
    }

    private static string SameOrTighter(Expr child, string op, int p) =>
        child is BinaryExpr cb && BinaryPrec(cb.Op) == p && cb.Op != op
            ? $"({Raw(child)})"
            : ExprToString(child, p);

    private static string BoundVarToString(BoundVar bv) => bv.Type is null ? bv.Name : $"{bv.Name}: {TypeToString(bv.Type)}";

    public static string PatternToString(Pattern p) => p switch
    {
        WildPattern => "_",
        VarPattern v => v.Name,
        LitPattern l => ExprToString(l.Value, 0),
        CtorPattern { IsTuple: true } c => $"({string.Join(", ", c.Args.Select(PatternToString))})",
        CtorPattern c when c.Args.Count == 0 => c.Ctor,
        CtorPattern c => $"{c.Ctor}({string.Join(", ", c.Args.Select(PatternToString))})",
        _ => throw new InvalidOperationException()
    };

    private static string SanitizeComment(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Replace("*/", "* /"), @"\s+", " ").Trim();

    #endregion
}
