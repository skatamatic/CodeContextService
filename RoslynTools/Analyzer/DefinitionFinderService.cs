using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynTools.Analyzer;

/// <summary>Controls how much commentary is injected.</summary>
public enum ExplainMode
{
    None,
    ReasonForInclusion,
    ReasonForInclusionAndExclusion
}

/// <summary>Produces minimal, depth-limited source definitions for a C# file.</summary>
public sealed class DefinitionFinderService : FinderServiceBase, IDefinitionFinderService
{
    public DefinitionFinderService(Action<string> log) : base(log) { }

    #region PUBLIC CONTRACT

    public async Task<IReadOnlyCollection<DefinitionResult>> FindAllDefinitionsAsync(string file, int depth)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        var solution = await GetSolutionAsync(file);
        var doc = LocateDocument(solution, file)
                       ?? throw new FileNotFoundException(file);
        var acc = new Dictionary<string, DefinitionResult>();
        await WalkFullAsync(solution, doc, depth, acc);
        return acc.Values.ToArray();
    }

    public async Task<DefinitionResult?> FindSingleClassDefinitionAsync(string anyFile, string className)
    {
        var solution = await GetSolutionAsync(anyFile);
        var sym = await LocateClassSymbolAsync(solution, className);
        if (sym is null) return null;

        var loc = sym.Locations.First(l => l.IsInSource);
        var node = (await loc.SourceTree!.GetRootAsync()).FindNode(loc.SourceSpan);
        return new DefinitionResult
        {
            File = loc.SourceTree.FilePath,
            Definitions =
            {
                [loc.SourceTree.FilePath] = BuildDefinition(sym, node)
            }
        };
    }

    public Task<IReadOnlyCollection<DefinitionResult>> FindMinimalDefinitionsAsync(
        string file, int depth, ExplainMode explain = ExplainMode.None)
        => RunMinimalAsync(file, depth, explain);

    #endregion

    #region FULL WALK (unpruned)

    private async Task WalkFullAsync(
        Solution solution, Document doc, int maxDepth,
        IDictionary<string, DefinitionResult> acc, int level = 0)
    {
        if (level > maxDepth || doc.FilePath is null) return;

        var model = await doc.GetSemanticModelAsync();
        var root = await model!.SyntaxTree.GetRootAsync();

        foreach (var node in root.DescendantNodes())
        {
            var type = (model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol) as INamedTypeSymbol;
            if (type is null || IsExcluded(type)) continue;

            var src = await SymbolFinder.FindSourceDefinitionAsync(type, solution) as INamedTypeSymbol ?? type;
            foreach (var loc in src.Locations.Where(l => l.IsInSource))
            {
                var file = loc.SourceTree!.FilePath;
                var decl = (await loc.SourceTree.GetRootAsync()).FindNode(loc.SourceSpan);

                var def = acc.TryGetValue(file, out var r) ? r : acc[file] = new() { File = file };
                var key = $"{file}:{type.ToDisplayString()}";
                if (def.Definitions.ContainsKey(key)) continue;

                def.Definitions[key] = BuildDefinition(type, decl);
                await WalkFullAsync(solution, solution.GetDocument(loc.SourceTree)!, maxDepth, acc, level + 1);
            }
        }
    }

    #endregion

    #region MINIMAL WALK WITH EXPLANATIONS

    private sealed record MemberInfo(ISymbol Symbol, HashSet<string> Paths);
    private sealed record Frontier(INamedTypeSymbol Type, int DepthLeft, string Path);

    private async Task<IReadOnlyCollection<DefinitionResult>> RunMinimalAsync(
        string file, int maxDepth, ExplainMode explain)
    {
        if (maxDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));

        var solution = await GetSolutionAsync(file);
        var rootDoc = LocateDocument(solution, file)
                       ?? throw new FileNotFoundException(file);

        var keep = new Dictionary<INamedTypeSymbol, Dictionary<ISymbol, MemberInfo>>(SymbolEqualityComparer.Default);
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<Frontier>();

        // seed with every type declared in the source file
        await foreach (var rootType in GetDeclaredTypesAsync(rootDoc))
        {
            var rootPath = $"{Path.GetFileName(file)}::{rootType.Name}";
            queue.Enqueue(new(rootType, maxDepth, rootPath));

            keep[rootType] = new(SymbolEqualityComparer.Default)
            {
                [rootType] = new(rootType, new() { rootPath }) // type-level path
            };

            foreach (var m in rootType.GetMembers())
                keep[rootType][m] = new(m, new() { $"{rootPath} (declared in source file)" });
        }

        // BFS across reference graph
        while (queue.TryDequeue(out var f))
        {
            if (!seen.Add(f.Type)) continue;

            foreach (var loc in f.Type.Locations.Where(l => l.IsInSource))
            {
                var doc = solution.GetDocument(loc.SourceTree)!;
                var model = await doc.GetSemanticModelAsync();
                var root = await loc.SourceTree.GetRootAsync();
                var node = GetTypeNode(root, loc);

                foreach (var sn in node.DescendantNodes())
                {
                    if (model.GetDeclaredSymbol(sn) != null) continue;  // ignore declarations
                    var sym = model.GetSymbolInfo(sn).Symbol;
                    if (sym is null || !IsRealUse(sn, sym) || IsExcluded(sym)) continue;

                    var owner = sym is INamedTypeSymbol nt ? nt : sym.ContainingType;
                    if (owner is null) continue;

                    var hop = $"{Path.GetFileName(owner.Locations[0].SourceTree!.FilePath)}::{owner.Name}::{sym.Name}";
                    var fullPath = $"{f.Path}->{hop}";

                    AddReason(owner, sym, fullPath);

                    if (!SymbolEqualityComparer.Default.Equals(owner, f.Type) && f.DepthLeft > 0)
                        queue.Enqueue(new(owner, f.DepthLeft - 1, fullPath));
                }
            }

            IncludeMandatoryMembers(f.Type);
        }

        return await EmitAsync(solution, keep, explain);

        // helpers --------------------------------------------------------------
        void AddReason(INamedTypeSymbol t, ISymbol s, string path)
        {
            var bucket = keep.TryGetValue(t, out var b) ? b : keep[t] = new(SymbolEqualityComparer.Default);

            if (!bucket.TryGetValue(s, out var mi))
                mi = bucket[s] = new(s, new());
            mi.Paths.Add(path);

            // ensure the type itself has trivia
            if (!bucket.TryGetValue(t, out var ti))
                ti = bucket[t] = new(t, new());
            ti.Paths.Add(path);
        }

        void IncludeMandatoryMembers(INamedTypeSymbol t)
        {
            foreach (var cctor in t.StaticConstructors)
                AddReason(t, cctor, $"{t.Name}::(static ctor)");
            foreach (var fld in t.GetMembers().OfType<IFieldSymbol>()
                                   .Where(f => f.IsConst || (f.IsStatic && f.IsReadOnly)))
                AddReason(t, fld, $"{t.Name}::{fld.Name} (const/static)");
        }
    }

    /// <summary>True if <paramref name="node"/> represents an actual use of <paramref name="symbol"/>.</summary>
    private static bool IsRealUse(SyntaxNode node, ISymbol symbol)
    {
        // calls, property/field/event access
        if (node.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax)
            return true;

        // identifier on its own (lhs/rhs)
        if (node is IdentifierNameSyntax id &&
            symbol.Kind is SymbolKind.Property or SymbolKind.Field or SymbolKind.Event or SymbolKind.Local or SymbolKind.Parameter)
            return true;

        // event hookup / assignment
        if (node.Parent is AssignmentExpressionSyntax) return true;

        // constructor call counts only for .ctor, not for the type itself
        if (node is ObjectCreationExpressionSyntax)
            return symbol.Kind == SymbolKind.Method; // .ctor

        return false;
    }

    #endregion

    #region EMIT

    private static async Task<IReadOnlyCollection<DefinitionResult>> EmitAsync(
        Solution solution,
        Dictionary<INamedTypeSymbol, Dictionary<ISymbol, MemberInfo>> keep,
        ExplainMode explain)
    {
        var results = new Dictionary<string, DefinitionResult>();

        foreach (var (type, members) in keep)
        {
            var loc = type.Locations.First(l => l.IsInSource);
            var doc = solution.GetDocument(loc.SourceTree)!;
            var model = await doc.GetSemanticModelAsync();
            var root = await loc.SourceTree.GetRootAsync();
            var node = GetTypeNode(root, loc);

            SyntaxNode rendered = node switch
            {
                TypeDeclarationSyntax td => RenderComplexType(td, members, model, explain),
                _ => AddTrivia(node, type, members, explain)
                                            .NormalizeWhitespace(eol: Environment.NewLine)
            };

            var file = loc.SourceTree.FilePath;
            if (!results.TryGetValue(file, out var def)) def = results[file] = new() { File = file };
            def.Definitions[$"{file}:{type.ToDisplayString()}"] = BuildDefinition(type, rendered);
        }

        return results.Values.ToArray();
    }

    private static SyntaxNode RenderComplexType(
        TypeDeclarationSyntax original,
        Dictionary<ISymbol, MemberInfo> map,
        SemanticModel model,
        ExplainMode explain)
    {
        // capture (node,symbol) before mutation
        var memberTuples = original.Members
                                   .Select(m => (Node: m, Sym: model.GetDeclaredSymbol(m)!))
                                   .ToList();

        original = AddTrivia(original, model.GetDeclaredSymbol(original)!, map, explain);

        var keepSet = map.Keys.ToHashSet(SymbolEqualityComparer.Default);

        var kept = memberTuples
                   .Where(t => keepSet.Contains(t.Sym))
                   .Select(t => AddTrivia(t.Node, t.Sym, map, explain))
                   .ToList();

        if (explain == ExplainMode.ReasonForInclusionAndExclusion)
            original = AppendExclusionStub(original,
                                           memberTuples.Where(t => !keepSet.Contains(t.Sym))
                                                       .Select(t => t.Node),
                                           model);

        return original.WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(kept))
                       .NormalizeWhitespace(eol: Environment.NewLine);
    }

    private static T AddTrivia<T>(
        T node, ISymbol sym,
        Dictionary<ISymbol, MemberInfo> map,
        ExplainMode explain) where T : SyntaxNode
    {
        if (explain == ExplainMode.None || !map.TryGetValue(sym, out var mi) || mi.Paths.Count == 0)
            return node;

        var trivia = SyntaxFactory.ParseLeadingTrivia(
            string.Join(Environment.NewLine, mi.Paths.Select(p => $"// path: {p}")) + Environment.NewLine);
        return node.WithLeadingTrivia(trivia.Concat(node.GetLeadingTrivia()));
    }

    private static TypeDeclarationSyntax AppendExclusionStub(
        TypeDeclarationSyntax tds,
        IEnumerable<MemberDeclarationSyntax> pruned,
        SemanticModel model)
    {
        var list = pruned.ToList();
        if (list.Count == 0) return tds;

        var sb = new StringBuilder();
        foreach (var m in list)
        {
            var sig = model.GetDeclaredSymbol(m)?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
            sb.AppendLine("// excluded – no path from source file");
            if (sig == null)
                sb.AppendLine("// sig null");
            else
                sb.AppendLine("// " + sig);
        }

        var brace = tds.CloseBraceToken;
        var trail = brace.TrailingTrivia.InsertRange(0,
                      SyntaxFactory.ParseTrailingTrivia(Environment.NewLine + sb));
        return tds.WithCloseBraceToken(brace.WithTrailingTrivia(trail));
    }

    #endregion

    #region UTILS

    private static SyntaxNode GetTypeNode(SyntaxNode root, Location loc)
    {
        var n = root.FindNode(loc.SourceSpan);
        return n switch
        {
            TypeDeclarationSyntax t => t,
            EnumDeclarationSyntax e => e,
            DelegateDeclarationSyntax d => d,
            _ => n.AncestorsAndSelf().First(a =>
                    a is TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax)
        };
    }

    private static async IAsyncEnumerable<INamedTypeSymbol> GetDeclaredTypesAsync(Document doc)
    {
        var model = await doc.GetSemanticModelAsync();
        var root = await model!.SyntaxTree.GetRootAsync();
        foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            if (model.GetDeclaredSymbol(t) is INamedTypeSymbol sym)
                yield return sym;
    }

    private static async Task<INamedTypeSymbol?> LocateClassSymbolAsync(
        Solution solution, string className)
    {
        foreach (var proj in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(proj, className, ignoreCase: false, SymbolFilter.Type);
            var hit = decls.OfType<INamedTypeSymbol>()
                             .FirstOrDefault(s => s.Name.Equals(className, StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        return null;
    }

    public Task<IReadOnlyCollection<DefinitionResult>> FindAggregatedMinimalDefinitionsAsync(IEnumerable<string> sourceFiles, int depth, ExplainMode explain = ExplainMode.None, bool excludeTargetSourceFileDefinitionsPerFile = false)
    {
        throw new NotImplementedException();
    }

    #endregion
}
