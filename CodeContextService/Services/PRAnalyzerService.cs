using RoslynTools.Analyzer;
using CodeContextService.Model;

namespace CodeContextService.Services;

public class PRAnalyzerService
{
    readonly DefinitionFinderService referenceFinder;
    readonly GitHubIntegrationService github;
    readonly ILogger<PRAnalyzerService>? _log;

    public PRAnalyzerService(GitHubIntegrationService github, DefinitionFinderService definitionFinder, ILogger<PRAnalyzerService>? log = null)
    {
        this.referenceFinder = definitionFinder;
        this.github = github;
        _log = log;
    }

    public async Task<AnalysisResult> RunAnalysis(string token,
        string owner,
        string repo,
        int prNumber,
        Action<string>? log = null)
    {
        log ??= (_ => { });

        var definitionMap = new Dictionary<string, IEnumerable<Definition>>();
        string? clonePath = null;

        try
        {
            log($"Cloning repository '{repo}'");
            clonePath = await github.CloneRepository(token, owner, repo);
            log($"Cloned to {clonePath}");

            log($"Fetching PR {prNumber} for owner {owner}");
            var raw = await github.GetPullRequestDiffAsync(token, owner, repo, prNumber);
            var diff = ParseUnifiedDiff(raw);
            log($"Fetched diff, files changed: {diff.Count()}");

            foreach (var file in diff.Where(f => f.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                log($"Analyzing {file.FileName}...");
                try
                {
                    var results = await referenceFinder.FindAllDefinitionsAsync(Path.Combine(clonePath, file.FileName), 1);
                    var flat = results.SelectMany(r => r.Definitions.Values);
                    definitionMap[file.FileName] = flat;
                    log($"Found {flat.Count()} definitions");
                }
                catch (Exception ex)
                {
                    log($"❌ Error loading {file.FileName} - {ex.Message}");
                }
            }

            log("Analysis complete.");

            return new AnalysisResult
            {
                DefinitionMap = definitionMap,
                FileDiffs = diff
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(clonePath) && Directory.Exists(clonePath))
            {
                try
                {
                    Directory.Delete(clonePath, recursive: true);
                    _log?.LogInformation("Cleaned up clone directory {Path}", clonePath);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Failed to delete clone directory {Path}; consider manual cleanup", clonePath);
                }
            }
        }
    }

    private IEnumerable<FileDiff> ParseUnifiedDiff(string diff)
    {
        var diffs = new List<FileDiff>();
        
        FileDiff? current = null;

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git"))
            {
                if (current != null) 
                    diffs.Add(current);

                var parts = line.Split(' ');
                var pathPart = parts[^1];
                var fileName = pathPart.StartsWith("b/") ? pathPart[2..] : pathPart;
                current = new FileDiff { FileName = fileName };
                continue;
            }

            if (current == null) 
                continue;

            if (line.StartsWith("+") && !line.StartsWith("+++")) 
                current.Added++;
            else if (line.StartsWith("-") && !line.StartsWith("---")) 
                current.Removed++;

            current.DiffLines.Add(line);
        }

        if (current != null) 
            diffs.Add(current);
        
        return diffs;
    }
}
