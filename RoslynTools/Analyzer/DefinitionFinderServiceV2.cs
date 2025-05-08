using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynTools.Analyzer;

/// <summary>Produces minimal, depth-limited source definitions for a C# file.</summary>
public sealed class DefinitionFinderServiceV2 : FinderServiceBase, IDefinitionFinderService
{
    public DefinitionFinderServiceV2(Action<string> log) : base(log) { }

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
            Definitions = { [loc.SourceTree.FilePath] = BuildDefinition(sym, node) }
        };
    }

    public async Task<IReadOnlyCollection<DefinitionResult>> FindMinimalDefinitionsAsync(
        string file, int depth, ExplainMode explain = ExplainMode.None)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));

        var solution = await GetSolutionAsync(file);
        var rootDoc = LocateDocument(solution, file)
                       ?? throw new FileNotFoundException(file);

        var (keepMap, rootTypes) = await CrawlAsync(solution, rootDoc, depth);
        return await EmitAsync(solution, keepMap, rootTypes, explain);
    }
    #endregion

    #region FULL WALK (unchanged)
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

    #region MINIMAL WALK
    private sealed record MemberInfo(ISymbol Symbol, HashSet<string> Paths);
    private sealed record Frontier(ISymbol Symbol, int DepthLeft, string Path);

    private async Task<(Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>> Keep,
                        HashSet<INamedTypeSymbol> RootTypes)>
        CrawlAsync(Solution solution, Document rootDoc, int maxDepth)
    {
        var keep = new Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>>(SymbolEqualityComparer.Default);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<Frontier>();
        var rootTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // 1. root-file types – keep whole
        await foreach (var t in GetDeclaredTypesAsync(rootDoc))
        {
            rootTypes.Add(t);
            Register(t, t, SigWithLine(t));

            foreach (var m in t.GetMembers())
                Register(t, m, $"{SigWithLine(t)} (declared in source file)");

            // implemented interfaces become roots
            foreach (var iface in t.AllInterfaces)
            {
                Register(iface, iface, $"{SigWithLine(t)} implements {iface.Name}");
                foreach (var mem in iface.GetMembers())
                    Register(iface, mem, $"{SigWithLine(t)} implements {iface.Name}");
            }
        }

        // 2. seed BFS with every use-site symbol
        await foreach (var (sym, head) in EnumerateUseSiteSymbolsAsync(rootDoc))
            queue.Enqueue(new(sym, maxDepth, head));

        // 3. BFS
        while (queue.TryDequeue(out var f))
        {
            if (!seen.Add(f.Symbol)) continue;
            if (f.Symbol.Locations.All(l => !l.IsInSource)) continue;

            var owner = f.Symbol is INamedTypeSymbol nt ? nt : f.Symbol.ContainingType;
            if (owner is null) continue;

            Register(owner, f.Symbol, f.Path);
            if (SymbolEqualityComparer.Default.Equals(owner, f.Symbol))
                Register(owner, owner, f.Path);

            IncludeMandatory(owner);

            if (f.DepthLeft == 0) continue;

            foreach (var child in await CollectReferencedSymbolsAsync(f.Symbol, solution))
            {
                var childOwner = child is INamedTypeSymbol nt2 ? nt2 : child.ContainingType;
                if (childOwner is null) continue;

                var nextDepth = SymbolEqualityComparer.Default.Equals(childOwner, owner)
                                ? f.DepthLeft : f.DepthLeft - 1;
                if (nextDepth < 0) continue;

                var nextPath = $"{f.Path}->{SigWithLine(child)}";
                queue.Enqueue(new(child, nextDepth, nextPath));
            }
        }
        return (keep, rootTypes);

        //-----------------------------------------------------------------
        void Register(INamedTypeSymbol t, ISymbol s, string path)
        {
            if (!keep.TryGetValue(t, out var bucket))
                bucket = keep[t] = new(StringComparer.Ordinal);

            var key = s.ToDisplayString();
            if (!bucket.TryGetValue(key, out var mi))
                mi = bucket[key] = new(s, new());
            mi.Paths.Add(path);
        }

        void IncludeMandatory(INamedTypeSymbol t)
        {
            foreach (var cctor in t.StaticConstructors)
                Register(t, cctor, $"{t.Name}::(static ctor)");
            foreach (var fld in t.GetMembers().OfType<IFieldSymbol>()
                                 .Where(f => f.IsConst || (f.IsStatic && f.IsReadOnly)))
                Register(t, fld, $"{t.Name}::{fld.Name} (const/static)");
        }
    }
    #endregion

    #region EMIT
    private static async Task<IReadOnlyCollection<DefinitionResult>> EmitAsync(
        Solution solution,
        Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>> keep,
        HashSet<INamedTypeSymbol> rootTypes,
        ExplainMode explain)
    {
        var results = new Dictionary<string, DefinitionResult>();

        foreach (var (type, members) in keep)
        {
            var firstRef = type.DeclaringSyntaxReferences[0];
            var firstTree = firstRef.SyntaxTree;
            var doc = solution.GetDocument(firstTree)!;

            SyntaxNode rendered;
            var firstSyntax = await firstRef.GetSyntaxAsync();

            var keepAllMembers = rootTypes.Contains(type);

            if (firstSyntax is TypeDeclarationSyntax firstTds)
            {
                var tuples = new List<(MemberDeclarationSyntax Node, ISymbol Sym)>();

                foreach (var r in type.DeclaringSyntaxReferences)
                {
                    var n = await r.GetSyntaxAsync();
                    if (n is not TypeDeclarationSyntax tds) continue;

                    var pd = solution.GetDocument(tds.SyntaxTree)!;
                    var pm = await pd.GetSemanticModelAsync();

                    foreach (var m in tds.Members)
                    {
                        switch (m)
                        {
                            case BaseMethodDeclarationSyntax or EventDeclarationSyntax:
                            case PropertyDeclarationSyntax:
                                if (pm.GetDeclaredSymbol(m) is ISymbol sA)
                                    tuples.Add((m, sA));

                                // include accessor symbols so they match call sites
                                if (m is PropertyDeclarationSyntax pds && pds.AccessorList != null)
                                {
                                    foreach (var acc in pds.AccessorList.Accessors)
                                        if (pm.GetDeclaredSymbol(acc) is ISymbol accSym)
                                            tuples.Add((m, accSym));
                                }
                                break;

                            case FieldDeclarationSyntax fds:
                                foreach (var v in fds.Declaration.Variables)
                                    if (pm.GetDeclaredSymbol(v) is ISymbol sB)
                                        tuples.Add((m, sB));
                                break;

                            case EventFieldDeclarationSyntax efd:
                                foreach (var v in efd.Declaration.Variables)
                                    if (pm.GetDeclaredSymbol(v) is ISymbol sC)
                                        tuples.Add((m, sC));
                                break;
                        }
                    }
                }

                IReadOnlyCollection<(MemberDeclarationSyntax Node, ISymbol Sym)> keptSeq;
                if (keepAllMembers)
                    keptSeq = tuples;
                else
                {
                    var wanted = new HashSet<string>(members.Keys, StringComparer.Ordinal);
                    keptSeq = tuples.Where(t => wanted.Contains(t.Sym.ToDisplayString())).ToList();
                }

                // if no members kept, annotate type with “declared but not used”
                if (!keepAllMembers && keptSeq.Count == 0)
                {
                    members[type.ToDisplayString()].Paths.Clear();
                    members[type.ToDisplayString()].Paths.Add("(declared but not used)");
                }

                var keptNodes = keptSeq.Select(t =>
                        AddTrivia(t.Node, t.Sym.ToDisplayString(), members, rootTypes, explain)).ToList();

                var decorated = AddTrivia(firstTds, type.ToDisplayString(), members, rootTypes, explain);
                rendered = decorated.WithMembers(SyntaxFactory.List(keptNodes));
            }
            else
            {
                rendered = AddTrivia(firstSyntax, type.ToDisplayString(), members, rootTypes, explain);
            }

            var file = firstTree.FilePath;
            if (!results.TryGetValue(file, out var def)) def = results[file] = new() { File = file };
            def.Definitions[$"{file}:{type.ToDisplayString()}"] = BuildDefinition(type, rendered);
        }

        return results.Values.ToArray();
    }

    private static T AddTrivia<T>(T node,
                                  string symKey,
                                  Dictionary<string, MemberInfo> bucket,
                                  HashSet<INamedTypeSymbol> rootTypes,
                                  ExplainMode explain) where T : SyntaxNode
    {
        if (explain == ExplainMode.None) return node;
        if (!bucket.TryGetValue(symKey, out var mi) || mi.Paths.Count == 0) return node;

        // indent comment block equal to node indentation
        var origTrivia = node.GetLeadingTrivia();
        var indentTok = origTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var indentStr = indentTok.ToFullString();

        var comment = string.Join(Environment.NewLine,
                                  mi.Paths.Select(p => $"{indentStr}// path: {p}"));

        if (origTrivia.Count == 0 || origTrivia.First().Kind() != SyntaxKind.EndOfLineTrivia)
            comment += Environment.NewLine;

        var injected = SyntaxFactory.ParseLeadingTrivia(comment);
        return node.WithLeadingTrivia(injected.Concat(origTrivia));
    }
    #endregion

    #region SYMBOL HELPERS
    private async Task<IEnumerable<ISymbol>> CollectReferencedSymbolsAsync(ISymbol sym, Solution solution)
    {
        if (sym is not (IMethodSymbol or IPropertySymbol))
            return Enumerable.Empty<ISymbol>();

        var sx = sym.DeclaringSyntaxReferences.FirstOrDefault();
        if (sx == null) return Enumerable.Empty<ISymbol>();

        var node = await sx.GetSyntaxAsync();
        var doc = solution.GetDocument(node.SyntaxTree)!;
        var model = await doc.GetSemanticModelAsync();
        var op = model.GetOperation(node);
        if (op == null) return Enumerable.Empty<ISymbol>();

        var col = new ReferencedSymbolCollector();
        col.Visit(op);
        return col.Collected;
    }

    private sealed class ReferencedSymbolCollector : OperationWalker
    {
        public readonly HashSet<ISymbol> Collected = new(SymbolEqualityComparer.Default);
        public override void Visit(IOperation op)
        {
            switch (op)
            {
                case IInvocationOperation inv: Collected.Add(inv.TargetMethod); break;
                case IMemberReferenceOperation mem: Collected.Add(mem.Member); break;
                case IObjectCreationOperation obj: Collected.Add(obj.Constructor); break;
            }
            base.Visit(op);
        }
    }

    private static async IAsyncEnumerable<(ISymbol Sym, string Path)> EnumerateUseSiteSymbolsAsync(Document doc)
    {
        var model = await doc.GetSemanticModelAsync();
        var root = await model.SyntaxTree.GetRootAsync();
        var text = await doc.GetTextAsync();

        foreach (var node in root.DescendantNodes())
        {
            if (model.GetDeclaredSymbol(node) != null) continue;

            var sym = model.GetSymbolInfo(node).Symbol;
            if (sym == null) continue;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line;
            var file = Path.GetFileName(doc.FilePath);
            yield return (sym, $"({file}:{line + 1}) {text.Lines[line].ToString().Trim()}");
        }
    }

    private static string SigWithLine(ISymbol s)
    {
        var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
        return loc == null
             ? $"[meta]{s.Name}"
             : $"({Path.GetFileName(loc.SourceTree!.FilePath)}:{loc.GetLineSpan().StartLinePosition.Line + 1}) {s.Name}";
    }
    #endregion

    #region UTILS
    private static async Task<INamedTypeSymbol?> LocateClassSymbolAsync(
        Solution solution, string className)
    {
        foreach (var proj in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(proj, className, false, SymbolFilter.Type);
            var hit = decls.OfType<INamedTypeSymbol>()
                             .FirstOrDefault(s => s.Name.Equals(className, StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        return null;
    }

    private static async IAsyncEnumerable<INamedTypeSymbol> GetDeclaredTypesAsync(Document doc)
    {
        var model = await doc.GetSemanticModelAsync();
        var root = await model!.SyntaxTree.GetRootAsync();
        foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            if (model.GetDeclaredSymbol(t) is INamedTypeSymbol sym)
                yield return sym;
    }
    #endregion
}
