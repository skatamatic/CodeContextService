using RoslynTools.Analyzer;
using CodeContextService.Model;
using Newtonsoft.Json;

namespace CodeContextService.Services;

public enum DefinitionAnalysisMode
{
    Full,
    Minified,
    MinifiedExplain
}

public class PRAnalyzerService
{
    readonly DefinitionFinderServiceV2 referenceFinder;
    readonly GitHubIntegrationService github;

    public PRAnalyzerService(GitHubIntegrationService github, DefinitionFinderServiceV2 definitionFinder)
    {
        this.referenceFinder = definitionFinder;
        this.github = github;
    }

    public async Task<AnalysisResult> RunAnalysis(string token,
        string owner,
        string repo,
        int prNumber,
        int depth,
        DefinitionAnalysisMode mode,
        Action<string>? log = null)
    {
        log ??= (_ => { });

        var definitionMap = new Dictionary<string, IEnumerable<Definition>>();

        log($"Fetching PR details for owner {owner}, repo {repo}, PR #{prNumber} to determine branch.");
        string? prBranchName;
        try
        {
            prBranchName = await github.GetPullRequestHeadBranchAsync(token, owner, repo, prNumber);
            log($"Pull request #{prNumber} is from branch: '{prBranchName}'.");
        }
        catch (Exception ex)
        {
            log($"❌ Error fetching PR branch details: {ex.Message}. Cloning default branch instead.");
            prBranchName = null;
            log($"Warning: Could not determine PR branch. Will attempt to clone default branch of '{repo}'.");
        }

        log($"Cloning repository '{repo}' (branch: '{prBranchName ?? "default"}').");
        // Pass the prBranchName to CloneRepository.
        // The CloneRepository method in GitHubIntegrationService is already designed to use the 'branch' parameter.
        var path = await github.CloneRepository(token, owner, repo, prBranchName);
        log($"Cloned to {path}");

        log($"Fetching PR diff for PR #{prNumber}");
        var raw = await github.GetPullRequestDiffAsync(token, owner, repo, prNumber);
        var diff = ParseUnifiedDiff(raw);
        log($"Fetched diff, files changed: {diff.Count()}");

        bool omitSourceFile = true;

        var aggregateResults = mode switch
        {
            DefinitionAnalysisMode.Minified => await referenceFinder.FindAggregatedMinimalDefinitionsAsync(
                diff.Select(f => Path.Combine(path, f.FileName)),
                depth,
                ExplainMode.None,
                excludeTargetSourceFileDefinitionsPerFile: omitSourceFile
            ),
            DefinitionAnalysisMode.MinifiedExplain => await referenceFinder.FindAggregatedMinimalDefinitionsAsync(
                diff.Select(f => Path.Combine(path, f.FileName)),
                depth,
                ExplainMode.ReasonForInclusion,
                excludeTargetSourceFileDefinitionsPerFile: omitSourceFile
            ),
            _ => Enumerable.Empty<DefinitionResult>()
        };

        Dictionary<string, IEnumerable<Definition>> flatAggregate = new();
        foreach (var result in aggregateResults)
        {
            flatAggregate[result.File] = result.Definitions.Values;
        }

        var json = JsonConvert.SerializeObject(flatAggregate, Formatting.Indented);
        log($"Flat analysis complete complete:\n{json}");

        foreach (var file in diff.Where(f => f.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            log($"Analyzing {file.FileName}...");
            try
            {
                IEnumerable<DefinitionResult> results = mode switch
                {
                    DefinitionAnalysisMode.Full => await referenceFinder.FindAllDefinitionsAsync(
                        Path.Combine(path, file.FileName),
                        depth
                    ),
                    DefinitionAnalysisMode.Minified => await referenceFinder.FindMinimalDefinitionsAsync(
                        Path.Combine(path, file.FileName),
                        depth,
                        excludeTargetSourceFileDefinitions: omitSourceFile
                    ),
                    DefinitionAnalysisMode.MinifiedExplain => await referenceFinder.FindMinimalDefinitionsAsync(
                        Path.Combine(path, file.FileName),
                        depth, ExplainMode.ReasonForInclusion,
                        excludeTargetSourceFileDefinitions: omitSourceFile
                    ),
                    _ => throw new NotImplementedException(),
                };
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

        var analysisResult = new AnalysisResult
        {
            DefinitionMap = definitionMap,
            FileDiffs = diff,
            MergedResults = aggregateResults
        };

        // Cleanup
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            log($"❌ Error deleting cloned repository: {ex.Message}");
        }

        return analysisResult;
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