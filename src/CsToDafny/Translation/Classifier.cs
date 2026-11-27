using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CsToDafny.Translation;

/// <summary>
/// Decides which C# methods become Dafny functions (pure, expression-shaped, calling only functions) and which
/// become methods. Overrides and interface implementations must agree with what they implement, so the decision
/// is made per group, iterated to a fixpoint over the call graph.
/// </summary>
public static class Classifier
{
    /// <summary>The body of a method, property getter or constructor declared in source.</summary>
    public static IBlockOperation? BodyOf(IMethodSymbol m, SymbolModel symbols)
    {
        foreach (var r in m.DeclaringSyntaxReferences)
        {
            var syntax = r.GetSyntax();
            var model = symbols.ModelFor(syntax.SyntaxTree);
            switch (syntax)
            {
                case BaseMethodDeclarationSyntax md:
                    return model.GetOperation(md) switch
                    {
                        IMethodBodyOperation mb => mb.BlockBody ?? mb.ExpressionBody,
                        IConstructorBodyOperation cb => cb.BlockBody ?? cb.ExpressionBody,
                        _ => null,
                    };
                case AccessorDeclarationSyntax ad:
                    return model.GetOperation(ad) is IMethodBodyOperation ab ? ab.BlockBody ?? ab.ExpressionBody : null;
                case ArrowExpressionClauseSyntax arrow:
                    return model.GetOperation(arrow) as IBlockOperation;
                case PropertyDeclarationSyntax { ExpressionBody: { } body }:
                    return model.GetOperation(body) as IBlockOperation;
            }
        }
        return null;
    }

    /// <summary>Methods and computed-property getters declared in source that need a Dafny member.</summary>
    public static IEnumerable<IMethodSymbol> Members(INamedTypeSymbol t)
    {
        foreach (var member in t.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } m:
                    yield return m;
                    break;
                case IPropertySymbol { GetMethod: { } g } p when !p.IsIndexer &&
                    (SymbolModel.IsComputedProperty(p) || p.IsAbstract || t.TypeKind == TypeKind.Interface):
                    yield return g;
                    break;
            }
        }
    }

    /// <param name="effectful">Members that must be methods regardless of their body (stubs of stateful classes).</param>
    public static void Classify(SymbolModel symbols, Func<IMethodSymbol, bool>? effectful = null)
    {
        var all = symbols.SourceTypes.SelectMany(Members).ToList();
        var parent = new Dictionary<IMethodSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        IMethodSymbol Find(IMethodSymbol m)
        {
            while (parent.TryGetValue(m, out var p) && !SymbolEqualityComparer.Default.Equals(p, m)) m = p;
            return m;
        }
        void Union(IMethodSymbol a, IMethodSymbol b)
        {
            a = Find(a.OriginalDefinition);
            b = Find(b.OriginalDefinition);
            if (!SymbolEqualityComparer.Default.Equals(a, b)) parent[a] = b;
        }
        foreach (var m in all) parent.TryAdd(m.OriginalDefinition, m.OriginalDefinition);

        foreach (var m in all)
        {
            if (m.OverriddenMethod is { } o && parent.ContainsKey(o.OriginalDefinition)) Union(m, o);
            if (m.AssociatedSymbol is IPropertySymbol { OverriddenProperty.GetMethod: { } og } && parent.ContainsKey(og.OriginalDefinition)) Union(m, og);
        }
        foreach (var t in symbols.SourceTypes)
        foreach (var iface in t.AllInterfaces)
        foreach (var im in Members(iface.OriginalDefinition))
        {
            var target = im.AssociatedSymbol is IPropertySymbol ip
                ? (t.FindImplementationForInterfaceMember(ip) as IPropertySymbol)?.GetMethod
                : t.FindImplementationForInterfaceMember(im) as IMethodSymbol;
            if (target is not null && parent.ContainsKey(target.OriginalDefinition)) Union(im, target);
        }

        var groups = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var m in all)
        {
            var root = Find(m.OriginalDefinition);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            if (!list.Contains(m, SymbolEqualityComparer.Default)) list.Add(m);
        }
        var isFunction = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var callees = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);

        foreach (var (root, members) in groups)
        {
            var bodies = members.Select(m => (m, body: BodyOf(m, symbols))).Where(x => x.body is not null).ToList();
            // Abstract members with no implementation in source are integration points: treat them as effectful.
            var candidate = bodies.Count > 0 && members.All(m => !m.ReturnsVoid && m.Parameters.All(p => p.RefKind == RefKind.None) && !m.IsAsync)
                            && !members.Any(m => effectful?.Invoke(m) == true)
                            && bodies.All(x => IsPureShape(x.body!, symbols));
            isFunction[root] = candidate;
            callees[root] = bodies.SelectMany(x => Callees(x.body!, symbols)).ToList();
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var root in groups.Keys)
            {
                if (!isFunction[root]) continue;
                if (callees[root].Any(c => parent.ContainsKey(c.OriginalDefinition) && !isFunction[Find(c.OriginalDefinition)]))
                {
                    isFunction[root] = false;
                    changed = true;
                }
            }
        } while (changed);

        foreach (var (root, members) in groups)
            if (isFunction[root])
                foreach (var m in members) symbols.Functions.Add(m.OriginalDefinition);
    }

    private static IEnumerable<IMethodSymbol> Callees(IOperation body, SymbolModel symbols)
    {
        foreach (var d in body.DescendantsAndSelf())
        {
            switch (d)
            {
                case IInvocationOperation inv when symbols.IsSourceType(inv.TargetMethod.ContainingType):
                    yield return inv.TargetMethod.OriginalDefinition;
                    break;
                case IPropertyReferenceOperation { Property.GetMethod: { } g } pr when symbols.IsSourceType(pr.Property.ContainingType):
                    yield return g.OriginalDefinition;
                    break;
                case IMethodReferenceOperation mr when symbols.IsSourceType(mr.Method.ContainingType):
                    yield return mr.Method.OriginalDefinition;
                    break;
            }
        }
    }

    /// <summary>Can this body be one Dafny expression? No mutation, loops, allocation of objects, or effectful statements.</summary>
    private static bool IsPureShape(IBlockOperation body, SymbolModel symbols)
    {
        foreach (var d in body.DescendantsAndSelf())
        {
            switch (d)
            {
                case IAssignmentOperation a when a.Parent is IObjectOrCollectionInitializerOperation:
                case IDeconstructionAssignmentOperation { Target: IDeclarationExpressionOperation }:
                    break;
                case IAssignmentOperation:
                case IIncrementOrDecrementOperation:
                case ILoopOperation:
                case ITryOperation:
                case ILocalFunctionOperation:
                case IUsingOperation:
                case ILockOperation:
                case IReturnOperation { Kind: OperationKind.YieldReturn or OperationKind.YieldBreak }:
                    return false;
                case IBranchOperation br when br.BranchKind != BranchKind.Break || !InSwitch(br):
                    return false;
                case IVariableDeclaratorOperation decl when decl.GetVariableInitializer() is null && decl.Parent?.Parent is not IForEachLoopOperation:
                    return false;
                case IObjectCreationOperation oc when oc.Type is { } t && symbols.IsSourceType(t) && symbols.DatatypeOf(t) is null:
                    return false;
                case IExpressionStatementOperation es when es.Operation is IInvocationOperation inv:
                    var owner = inv.TargetMethod.ContainingType.ToDisplayString();
                    if (!(owner == "System.Diagnostics.Debug" && inv.TargetMethod.Name == "Assert") &&
                        !(owner.StartsWith("System.Argument", StringComparison.Ordinal) && inv.TargetMethod.Name.StartsWith("ThrowIf", StringComparison.Ordinal)))
                        return false;
                    break;
                case IExpressionStatementOperation es2 when es2.Operation is not (IThrowOperation or IDeconstructionAssignmentOperation):
                    return false;
            }
        }
        return true;
    }

    private static bool InSwitch(IOperation op)
    {
        for (var p = op.Parent; p is not null; p = p.Parent)
        {
            if (p is ISwitchOperation) return true;
            if (p is ILoopOperation) return false;
        }
        return false;
    }
}
