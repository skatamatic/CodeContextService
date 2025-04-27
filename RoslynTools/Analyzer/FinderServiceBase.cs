using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RoslynTools.Analyzer;

/// <summary>Base class that encapsulates shared Roslyn‑related helpers.</summary>
public abstract class FinderServiceBase : IDisposable
{
    // ────────────────────────────────────────────────────────────────────────────────
    // Filtering constants                                                             
    // ────────────────────────────────────────────────────────────────────────────────
    protected static readonly ImmutableHashSet<string> ExcludedNamespaces =
        ImmutableHashSet.Create("System", "Microsoft", "Unity", "UnityEngine", "Newtonsoft", "NSubstitute", "Moq", "NUnit");

    protected static readonly ImmutableArray<string> SingleClassPriorityProjects = ImmutableArray.Create(
        "Assembly-CSharp", "Assembly-CSharp-firstpass", "Assembly-CSharp-Editor", "Assembly-CSharp-Editor-firstpass",
        "Scope.Common", "ScopeAR.Core", "ScopeAR.RemoteAR.UI", "Scope.BundleLoader", "ScopePlayer",
        "ARTrackingPlatformServices-ASM", "Automation", "ScenarioSessions.Events", "Scope.WebModels",
        "ScenarioSessions", "ARTrackingServiceLoactor-ASM", "Scope.Style", "Scope.Requests", "SessionPlayback-asm",
        "ScopeMSMixedRealityService-ASM", "IntelligentPluginVersioning", "Scope.Cache", "DocumentViewer", "UIState",
        "ScenarioLoading", "VoiceCommands", "SessionPersistence-asm", "Scope.Core.Input", "ScopeARKit-ASM",
        "Scope.Endpoints", "Scope.Style.Editor", "Scope.Build");

    protected static readonly Regex ExcludedNamespaceRegex = new($"^({string.Join("|", ExcludedNamespaces)})", RegexOptions.Compiled);

    // ────────────────────────────────────────────────────────────────────────────────
    // Instance state                                                                  
    // ────────────────────────────────────────────────────────────────────────────────
    private readonly Action<string> _log;
    private readonly Dictionary<string, (Solution Solution, MSBuildWorkspace WS)> _solutionCache = new(StringComparer.OrdinalIgnoreCase);

    // ────────────────────────────────────────────────────────────────────────────────
    // Ctor / Dispose                                                                  
    // ────────────────────────────────────────────────────────────────────────────────
    protected FinderServiceBase(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Dispose()
    {
        foreach (var (_, (_, ws)) in _solutionCache) ws.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Solution helpers                                                                
    // ────────────────────────────────────────────────────────────────────────────────
    protected async Task<Solution> GetSolutionAsync(string anyPathInsideSolution)
    {
        var slnPath = new BaseSolutionTools().FindSolutionFile(anyPathInsideSolution);
        Debug.Assert(Path.IsPathRooted(slnPath));

        if (_solutionCache.TryGetValue(slnPath, out var cached)) return cached.Solution;

        var ws = MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (_, e) => _log($"[Workspace] {e.Diagnostic.Message}");
        _log($"[Workspace] Loading solution '{slnPath}' …");
        var progress = new Progress<ProjectLoadProgress>(p => _log($"{p.Operation} – {p.FilePath} – {p.ElapsedTime}"));
        var solution = await ws.OpenSolutionAsync(slnPath, progress);
        _solutionCache[slnPath] = (solution, ws);
        return solution;
    }

    protected static bool IsExcluded(ISymbol symbol)
        => symbol.ContainingNamespace is not null && ExcludedNamespaceRegex.IsMatch(symbol.ContainingNamespace.ToDisplayString());

    protected static async Task<SyntaxNode?> ExtractNodeAsync(Location loc)
    {
        var root = await loc.SourceTree!.GetRootAsync();
        return root.FindNode(loc.SourceSpan);
    }

    // Common utility to pretty‑print code fragments                                     
    protected static string Minify(string code)
    {
        var lines = code.Split(Environment.NewLine);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        if (nonEmpty.Length == 0) return code;
        var indent = nonEmpty.Min(l => l.TakeWhile(char.IsWhiteSpace).Count());
        var trimmed = lines.Select(l => l.Length >= indent ? l[indent..] : l).ToArray();
        return string.Join(Environment.NewLine, trimmed).Trim();
    }

    protected static Definition BuildDefinition(INamedTypeSymbol symbol, SyntaxNode node)
    {
        static IEnumerable<string> NamespaceParts(INamespaceSymbol? ns)
        {
            for (var cur = ns; cur is not null && !string.IsNullOrEmpty(cur.Name); cur = cur.ContainingNamespace)
                yield return cur.Name;
        }

        return new Definition
        {
            Symbol = symbol.Name,
            Code = Minify(node.ToFullString()),
            Namespace = string.Join('.', NamespaceParts(symbol.ContainingNamespace).Reverse())
        };
    }

    protected static Document? LocateDocument(Solution solution, string sourceFile)
        => solution.Projects.SelectMany(p => p.Documents)
                   .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath ?? string.Empty),
                                                      Path.GetFullPath(sourceFile),
                                                      StringComparison.OrdinalIgnoreCase));
}