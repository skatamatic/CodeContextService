using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynTools.Analyzer;

/// <summary>How much commentary is injected into generated code.</summary>
public enum ExplainMode
{
    None,
    ReasonForInclusion,
    ReasonForInclusionAndExclusion
}

/// <summary>
/// Produces the minimal, depth-limited set of definitions required to compile a C# file.
/// Adds optional <c>// path:</c> comments that show why each symbol was kept, including line numbers and source text.
/// Interfaces are emitted unpruned; classes and structs are pruned to actual usage.
/// </summary>
public sealed class DefinitionFinderService : FinderServiceBase, IDefinitionFinderService
{
    public DefinitionFinderService(Action<string> log) : base(log) { }

    #region Public API

    public async Task<IReadOnlyCollection<DefinitionResult>> FindAllDefinitionsAsync(string file, int depth)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        var solution = await GetSolutionAsync(file);
        var doc = LocateDocument(solution, file) ?? throw new FileNotFoundException(file);
        var acc = new Dictionary<string, DefinitionResult>(StringComparer.OrdinalIgnoreCase);
        await WalkFullAsync(solution, doc, depth, acc);
        return acc.Values.ToArray();
    }

    public async Task<DefinitionResult?> FindSingleClassDefinitionAsync(string anyFile, string className)
    {
        if (string.IsNullOrWhiteSpace(className)) throw new ArgumentException("Class name required.", nameof(className));
        var solution = await GetSolutionAsync(anyFile);
        var symbol = await LocateClassSymbolAsync(solution, className);
        if (symbol == null) return null;

        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc == null) return null;
        var node = (await loc.SourceTree!.GetRootAsync()).FindNode(loc.SourceSpan);
        var file = loc.SourceTree.FilePath ?? "(no path)";
        var result = new DefinitionResult { File = file };
        result.Definitions[file] = BuildDefinition(symbol, node);
        return result;
    }

    public Task<IReadOnlyCollection<DefinitionResult>> FindMinimalDefinitionsAsync(
        string file, int depth, ExplainMode explain = ExplainMode.None)
        => WalkMinimalAsync(file, depth, explain);

    #endregion

    #region Full Walk (unpruned)

    private async Task WalkFullAsync(
        Solution solution,
        Document document,
        int maxDepth,
        IDictionary<string, DefinitionResult> acc,
        int level = 0)
    {
        if (level > maxDepth || document.FilePath == null) return;

        var model = await document.GetSemanticModelAsync();
        if (model == null) return;

        var root = await model.SyntaxTree.GetRootAsync();
        foreach (var node in root.DescendantNodes())
        {
            var type = (model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol) as INamedTypeSymbol;
            if (type == null || IsExcluded(type)) continue;

            var srcDef = await SymbolFinder.FindSourceDefinitionAsync(type, solution) as INamedTypeSymbol ?? type;
            foreach (var loc in srcDef.Locations.Where(l => l.IsInSource))
            {
                var file = loc.SourceTree!.FilePath ?? "(no path)";
                var decl = (await loc.SourceTree.GetRootAsync()).FindNode(loc.SourceSpan);

                if (!acc.TryGetValue(file, out var def))
                    def = acc[file] = new DefinitionResult { File = file };

                var typeDisplay = type.ToDisplayString();
                var key = $"{file}:{typeDisplay}";
                if (def.Definitions.ContainsKey(key)) continue;

                def.Definitions[key] = BuildDefinition(type, decl);
                var nextDoc = solution.GetDocument(loc.SourceTree);
                if (nextDoc != null)
                    await WalkFullAsync(solution, nextDoc, maxDepth, acc, level + 1);
            }
        }
    }

    #endregion

    #region Minimal Walk with Explanations

    private sealed record MemberInfo(ISymbol Symbol, HashSet<string> Paths);
    private sealed record Frontier(INamedTypeSymbol Type, int DepthLeft, string Path);

    private async Task<IReadOnlyCollection<DefinitionResult>> WalkMinimalAsync(
        string file, int depth, ExplainMode explain)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));

        var solution = await GetSolutionAsync(file);
        var rootDoc = LocateDocument(solution, file) ?? throw new FileNotFoundException(file);

        var keep = new Dictionary<INamedTypeSymbol, Dictionary<ISymbol, MemberInfo>>(SymbolEqualityComparer.Default);
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<Frontier>();

        // Seed: every type declared in the source file
        await foreach (var rt in GetDeclaredTypesAsync(rootDoc))
        {
            var rootPath = $"{Path.GetFileName(file)}::{rt.Name}";
            queue.Enqueue(new(rt, depth, rootPath));
            var bucket = keep[rt] = new(SymbolEqualityComparer.Default);
            bucket[rt] = new(rt, new() { rootPath });
            foreach (var m in rt.GetMembers())
                bucket[m] = new(m, new() { $"{rootPath} (declared in source file)" });
        }

        // BFS traversal
        while (queue.TryDequeue(out var front))
        {
            if (!seen.Add(front.Type)) continue;

            foreach (var loc in front.Type.Locations.Where(l => l.IsInSource))
            {
                var doc = solution.GetDocument(loc.SourceTree)!;
                var model = await doc.GetSemanticModelAsync();

                if (model == null || loc.SourceTree == null)
                    continue;

                var root = await loc.SourceTree.GetRootAsync();
                var typeNode = GetTypeNode(root, loc);

                foreach (var n in typeNode.DescendantNodes())
                {
                    if (model.GetDeclaredSymbol(n) != null) continue;
                    var sym = model.GetSymbolInfo(n).Symbol;
                    if (sym == null || IsExcluded(sym) || !IsRealUse(n, sym)) continue;

                    var owner = sym is INamedTypeSymbol nt ? nt : sym.ContainingType;
                    if (owner == null) continue;

                    var tree = n.SyntaxTree!;
                    var textLines = await tree.GetTextAsync();
                    var lineSpan = tree.GetLineSpan(n.Span);
                    var lineNum = lineSpan.StartLinePosition.Line + 1;
                    var text = textLines.Lines[lineNum - 1].ToString().Trim();

                    var hop = $"{Path.GetFileName(owner.Locations[0].SourceTree!.FilePath)}::{owner.Name}::{sym.Name}";
                    var path = $"{front.Path}->{hop} @({lineNum}): {text}";

                    if (sym is INamedTypeSymbol ts)
                        AddReason(ts, ts, path);
                    else
                    {
                        AddReason(owner, sym, path);
                        AddInterfaceContracts(owner, sym, path);
                    }

                    if (!SymbolEqualityComparer.Default.Equals(owner, front.Type) && front.DepthLeft > 0)
                        queue.Enqueue(new(owner, front.DepthLeft - 1, path));
                }
            }

            IncludeMandatory(front.Type);
        }

        return await EmitAsync(solution, keep, explain);

        // Helpers

        void AddReason(INamedTypeSymbol t, ISymbol s, string p)
        {
            var bucket = keep.TryGetValue(t, out var b) ? b : keep[t] = new(SymbolEqualityComparer.Default);
            if (!bucket.TryGetValue(s, out var mi))
                mi = bucket[s] = new(s, new());
            mi.Paths.Add(p);

            if (!bucket.TryGetValue(t, out var ti))
                ti = bucket[t] = new(t, new());
            ti.Paths.Add(p);
        }

        void IncludeMandatory(INamedTypeSymbol t)
        {
            foreach (var cc in t.StaticConstructors)
                AddReason(t, cc, $"{t.Name}::(static ctor)");
            foreach (var fld in t.GetMembers().OfType<IFieldSymbol>()
                                 .Where(f => f.IsConst || (f.IsStatic && f.IsReadOnly)))
                AddReason(t, fld, $"{t.Name}::{fld.Name} (const/static)");
        }

        void AddInterfaceContracts(INamedTypeSymbol classType, ISymbol member, string p)
        {
            if (classType.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return;
            foreach (var iface in classType.AllInterfaces)
            {
                foreach (var im in iface.GetMembers(member.Name))
                {
                    var impl = classType.FindImplementationForInterfaceMember(im);
                    if (SymbolEqualityComparer.Default.Equals(impl, member))
                        AddReason(iface, im, $"{p} (implements {iface.Name})");
                }
            }
        }
    }

    #endregion

    #region Real-Use Detector

    private static bool IsRealUse(SyntaxNode node, ISymbol symbol)
    {
        if (node.Parent is MemberDeclarationSyntax md &&
            md.ChildTokens().Any(t => t == node.GetFirstToken()))
            return false;
        if (node.Parent is BaseTypeSyntax or VariableDeclaratorSyntax or ParameterSyntax)
            return false;

        if (symbol.Kind == SymbolKind.Method)
        {
            if (node.Parent is MemberAccessExpressionSyntax mae &&
                mae.Parent is InvocationExpressionSyntax)
                return true;
            if (node.Ancestors().OfType<InvocationExpressionSyntax>().Any())
                return true;
        }

        if (symbol.Kind is SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)
            return node.Parent is MemberAccessExpressionSyntax
                || node is IdentifierNameSyntax
                || node.Parent is AssignmentExpressionSyntax;

        if (symbol.Kind is SymbolKind.Local or SymbolKind.Parameter)
            return node is IdentifierNameSyntax;

        if (symbol is INamedTypeSymbol &&
            node is IdentifierNameSyntax or QualifiedNameSyntax or GenericNameSyntax)
            return true;

        if (node is ObjectCreationExpressionSyntax)
            return symbol.Kind == SymbolKind.Method || symbol is INamedTypeSymbol;

        if (node.Parent is AssignmentExpressionSyntax) return true;

        return false;
    }

    #endregion

    #region Emit Helpers

    private static async Task<IReadOnlyCollection<DefinitionResult>> EmitAsync(
        Solution solution,
        Dictionary<INamedTypeSymbol, Dictionary<ISymbol, MemberInfo>> keep,
        ExplainMode explain)
    {
        var results = new Dictionary<string, DefinitionResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var (type, members) in keep)
        {
            var loc = type.Locations.First(l => l.IsInSource);
            var doc = solution.GetDocument(loc.SourceTree)!;
            var model = await doc.GetSemanticModelAsync();

            if (model == null || loc.SourceTree == null)
                continue;

            var root = await loc.SourceTree.GetRootAsync();
            var node = GetTypeNode(root, loc);

            SyntaxNode rendered;
            if (node is InterfaceDeclarationSyntax ifaceDecl)
            {
                rendered = AddTrivia(ifaceDecl, type, members, explain)
                           .NormalizeWhitespace(eol: Environment.NewLine);
            }
            else if (node is TypeDeclarationSyntax td)
            {
                rendered = RenderComplex(td, members, model, explain);
            }
            else
            {
                rendered = AddTrivia(node, type, members, explain)
                           .NormalizeWhitespace(eol: Environment.NewLine);
            }

            var file = loc.SourceTree.FilePath ?? "(no path)";
            if (!results.TryGetValue(file, out var def))
                def = results[file] = new DefinitionResult { File = file };
            def.Definitions[$"{file}:{type.ToDisplayString()}"] = BuildDefinition(type, rendered);
        }

        return results.Values.ToArray();
    }

    private static SyntaxNode RenderComplex(
        TypeDeclarationSyntax original,
        Dictionary<ISymbol, MemberInfo> map,
        SemanticModel model,
        ExplainMode explain)
    {
        var tuples = original.Members
                             .Select(m => (Node: m, Sym: model.GetDeclaredSymbol(m)!))
                             .ToList();

        original = AddTrivia(original, model.GetDeclaredSymbol(original)!, map, explain);
        var keepSet = map.Keys.ToHashSet(SymbolEqualityComparer.Default);

        var kept = tuples.Where(t => keepSet.Contains(t.Sym))
                         .Select(t => AddTrivia(t.Node, t.Sym, map, explain))
                         .ToList();

        if (explain == ExplainMode.ReasonForInclusionAndExclusion)
            original = AppendExclusionStub(
                original,
                tuples.Where(t => !keepSet.Contains(t.Sym)).Select(t => t.Node),
                model);

        return original.WithMembers(
            SyntaxFactory.List<MemberDeclarationSyntax>(kept))
            .NormalizeWhitespace(eol: Environment.NewLine);
    }

    private static T AddTrivia<T>(
        T node, ISymbol sym,
        Dictionary<ISymbol, MemberInfo> map,
        ExplainMode explain) where T : SyntaxNode
    {
        if (explain == ExplainMode.None
            || !map.TryGetValue(sym, out var info)
            || info.Paths.Count == 0)
            return node;

        var text = string.Join(Environment.NewLine, info.Paths.Select(p => $"// path: {p}"));
        var trivia = SyntaxFactory.ParseLeadingTrivia(text + Environment.NewLine);
        return node.WithLeadingTrivia(trivia.Concat(node.GetLeadingTrivia()));
    }

    private static TypeDeclarationSyntax AppendExclusionStub(
        TypeDeclarationSyntax tds,
        IEnumerable<MemberDeclarationSyntax> pruned,
        SemanticModel model)
    {
        var list = pruned.ToList();
        if (!list.Any()) return tds;

        var sb = new StringBuilder();
        foreach (var m in list)
        {
            var sig = model.GetDeclaredSymbol(m)?
                          .ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
            sb.AppendLine("// excluded – no path from source file");
            sb.AppendLine("// " + sig);
        }

        var brace = tds.CloseBraceToken;
        var trail = brace.TrailingTrivia.InsertRange(
            0,
            SyntaxFactory.ParseTrailingTrivia(Environment.NewLine + sb));
        return tds.WithCloseBraceToken(brace.WithTrailingTrivia(trail));
    }

    #endregion

    #region Utilities

    private static SyntaxNode GetTypeNode(SyntaxNode root, Location loc)
    {
        var n = root.FindNode(loc.SourceSpan);
        return n switch
        {
            TypeDeclarationSyntax t => t,
            EnumDeclarationSyntax e => e,
            DelegateDeclarationSyntax d => d,
            _ => n.AncestorsAndSelf()
                  .First(a => a is TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax)
        };
    }

    private static async IAsyncEnumerable<INamedTypeSymbol> GetDeclaredTypesAsync(Document doc)
    {
        var model = await doc.GetSemanticModelAsync();
        var root = await model!.SyntaxTree.GetRootAsync();
        foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            if (model.GetDeclaredSymbol(t) is INamedTypeSymbol s)
                yield return s;
    }

    private static async Task<INamedTypeSymbol?> LocateClassSymbolAsync(
        Solution solution, string className)
    {
        foreach (var proj in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(
                proj, className, ignoreCase: false, SymbolFilter.Type);
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
