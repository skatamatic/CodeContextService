using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Immutable;

namespace RoslynTools.Analyzer;

/// <summary>Searches a solution for symbol references.</summary>
public interface IReferenceFinderService : IDisposable
{
    /// <summary>Returns every reference reachable from <paramref name="sourceFile"/> within <paramref name="depth"/> levels.</summary>
    Task<IReadOnlyCollection<ReferenceResult>> FindAllReferencesAsync(string sourceFile, int depth);
}

/// <inheritdoc cref="IReferenceFinderService"/>
public sealed partial class ReferenceFinderService : FinderServiceBase, IReferenceFinderService
{
    private static readonly ImmutableHashSet<SymbolKind> ReferenceSymbolKinds = ImmutableHashSet.Create(
        SymbolKind.NamedType, SymbolKind.Property, SymbolKind.Field, SymbolKind.Method, SymbolKind.Event,
        SymbolKind.FunctionPointerType, SymbolKind.TypeParameter);

    public ReferenceFinderService(Action<string> log) : base(log) { }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<ReferenceResult>> FindAllReferencesAsync(string sourceFile, int depth)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        var solution = await GetSolutionAsync(sourceFile).ConfigureAwait(false);
        var document = LocateDocument(solution, sourceFile) ??
                        throw new FileNotFoundException($"File '{sourceFile}' not found in solution.");

        var results = new Dictionary<string, ReferenceResult>(StringComparer.OrdinalIgnoreCase);
        await FindReferencesRecursiveAsync(solution, document, depth, results).ConfigureAwait(false);
        return results.Values.ToArray();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Internal recursive implementation                                               
    // ────────────────────────────────────────────────────────────────────────────────
    private async Task FindReferencesRecursiveAsync(
        Solution solution,
        Document document,
        int maxDepth,
        IDictionary<string, ReferenceResult> results,
        int currentDepth = 0,
        HashSet<DocumentId>? visited = null)
    {
        if (currentDepth > maxDepth) return;
        visited ??= new();
        if (!visited.Add(document.Id)) return;

        var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
        if (semanticModel is null) return;

        var root = await semanticModel.SyntaxTree.GetRootAsync().ConfigureAwait(false);
        var nodes = root.DescendantNodes().Where(n => semanticModel.GetSymbolInfo(n).Symbol is not null);

        foreach (var node in nodes)
        {
            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is null || !ReferenceSymbolKinds.Contains(symbol.Kind) || IsExcluded(symbol)) continue;

            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false);
            foreach (var r in refs)
            {
                foreach (var loc in r.Locations)
                {
                    if (loc.Document.FilePath is null) continue;

                    if (!results.TryGetValue(loc.Document.FilePath, out var rr))
                    {
                        rr = new ReferenceResult { File = loc.Document.FilePath };
                        results[loc.Document.FilePath] = rr;
                    }
                    rr.Symbols.Add(new ReferenceSymbol { Kind = symbol.Kind.ToString(), Name = symbol.Name });

                    var refDoc = solution.GetDocument(loc.Document.Id);
                    if (refDoc is not null)
                        await FindReferencesRecursiveAsync(solution, refDoc, maxDepth, results, currentDepth + 1, visited)
                            .ConfigureAwait(false);
                }
            }
        }
    }
}